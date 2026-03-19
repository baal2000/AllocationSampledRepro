// Repro: AllocationSampled (EventID 303) has no typed schema in TraceEvent 3.1.30.
//
// Requirements: .NET 10+ SDK and runtime.
// AllocationSampled is never emitted on .NET 9 or earlier regardless of keywords.
//
// What this shows:
//   1. Generate a live .nettrace with AllocationSamplingKeyword on .NET 10.
//   2. Parse via the native TraceEvent API  → PayloadNames is null, PayloadByName returns null.
//   3. Parse via raw EventData() bytes      → TypeName and ObjectSize decoded successfully.

using System.Diagnostics.Tracing;
using System.Text;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

const string ProviderName             = "Microsoft-Windows-DotNETRuntime";
const int    AllocationSampledEventId = 303;
const long   AllocationSamplingKeyword = 0x80000000000L;

if (Environment.Version.Major < 10)
{
    Console.Error.WriteLine($"ERROR: .NET 10+ required. Current runtime: {Environment.Version}");
    Console.Error.WriteLine("AllocationSampled (EventID 303) is never emitted on .NET 9 or earlier.");
    return 1;
}

Console.WriteLine($"Runtime: {Environment.Version}");
Console.WriteLine($"TraceEvent: {typeof(EventPipeEventSource).Assembly.GetName().Version}");

// ── Step 1: Self-instrument and capture a live trace ────────────────────────

var traceFile = Path.ChangeExtension(Path.GetTempFileName(), ".nettrace");
Console.WriteLine($"\nCapturing trace to: {traceFile}");

{
    var client   = new DiagnosticsClient(Environment.ProcessId);
    var provider = new EventPipeProvider(ProviderName, EventLevel.Informational, AllocationSamplingKeyword);
    using var session = client.StartEventPipeSession([provider], requestRundown: false);

    // Allocate enough objects to guarantee at least one sampled event.
    var sink = new List<string>(200_000);
    for (int i = 0; i < 200_000; i++) sink.Add(i.ToString("D8"));
    _ = sink.Count;

    using var file = File.Create(traceFile);
    var copy = Task.Run(() =>
    {
        try { session.EventStream.CopyTo(file); }
        catch (EndOfStreamException) { }
    });
    Thread.Sleep(300);
    session.Stop();
    copy.Wait(TimeSpan.FromSeconds(5));
}

Console.WriteLine("Trace captured.\n");

// ── Step 2: Parse via native TraceEvent API (broken) ───────────────────────

Console.WriteLine("══ Native TraceEvent API (broken) ═════════════════════════════════════");
int nativeSeen  = 0;
int clrTypedSeen = 0;

using (var source = new EventPipeEventSource(traceFile))
{
    // Typed CLR parser — should receive AllocationSampled if ClrTraceEventParser knew about EventID 303.
    source.Clr.All += data =>
    {
        if ((int)data.ID == AllocationSampledEventId) clrTypedSeen++;
    };

    source.Dynamic.All += data =>
    {
        if (!string.Equals(data.ProviderName, ProviderName, StringComparison.Ordinal)) return;
        if ((int)data.ID != AllocationSampledEventId) return;
        if (nativeSeen++ > 0) return; // Print only the first event for brevity

        var names = data.PayloadNames;
        Console.WriteLine($"  PayloadNames:                {FormatNames(names)}");
        Console.WriteLine($"  PayloadByName(\"TypeName\"):   {data.PayloadByName("TypeName") ?? "<null>"}");
        Console.WriteLine($"  PayloadByName(\"ObjectSize\"): {data.PayloadByName("ObjectSize") ?? "<null>"}");

        object? val0 = null;
        try   { val0 = data.PayloadValue(0); }
        catch (Exception ex) { val0 = $"<{ex.GetType().Name}>"; }
        Console.WriteLine($"  PayloadValue(0):             {val0 ?? "<null>"}");
    };
    source.Process();
}

Console.WriteLine($"  source.Clr.All hits for EventID 303:  {clrTypedSeen}  (0 = not routed through ClrTraceEventParser)");
Console.WriteLine($"  source.Dynamic.All hits for EventID 303: {nativeSeen}");

if (nativeSeen == 0)
{
    Console.Error.WriteLine("\nWARNING: zero AllocationSampled events captured.");
    Console.Error.WriteLine("Increase allocation volume or ensure .NET 10+ runtime is active.");
    File.Delete(traceFile);
    return 2;
}

// ── Step 3: Parse via raw EventData() bytes (working workaround) ────────────

Console.WriteLine("\n══ Raw EventData() workaround (works) ══════════════════════════════════");
int rawDecoded = 0;

using (var source = new EventPipeEventSource(traceFile))
{
    source.Dynamic.All += data =>
    {
        if (!string.Equals(data.ProviderName, ProviderName, StringComparison.Ordinal)) return;
        if ((int)data.ID != AllocationSampledEventId) return;

        if (TryParseAllocationSampled(data.EventData(), data.PointerSize, out var typeName, out var objectSize))
        {
            rawDecoded++;
            if (rawDecoded <= 5)
                Console.WriteLine($"  [{rawDecoded}] TypeName={typeName ?? "<empty>"}  ObjectSize={objectSize} bytes");
        }
    };
    source.Process();
}

Console.WriteLine($"  Total events decoded via raw bytes: {rawDecoded} / {nativeSeen}");

Console.WriteLine();
if (rawDecoded > 0)
    Console.WriteLine("RESULT: native API yields no payload data; raw byte parsing works.\n" +
                      "        ClrTraceEventParser needs a typed schema for EventID 303.");
else
    Console.WriteLine("RESULT: raw byte parser also failed — allocation volume may need to increase.");

File.Delete(traceFile);
return 0;

// ── Helpers ──────────────────────────────────────────────────────────────────

static string FormatNames(string[]? names) =>
    names is null ? "<null>" : names.Length == 0 ? "<empty array>" : string.Join(", ", names);

// Raw byte parser — workaround for missing ClrTraceEventParser support.
//
// Binary layout (from dotnet/runtime PR #104955 and the reference consumer at
// src/tests/tracing/eventpipe/randomizedallocationsampling/manual/AllocationProfiler/Program.cs):
//
//   AllocationKind    win:UInt32        4 bytes
//   ClrInstanceID     win:UInt16        2 bytes
//   TypeID            win:Pointer       4|8 bytes  (pointer-sized)
//   TypeName          win:UnicodeString variable   (null-terminated UTF-16LE)
//   Address           win:Pointer       4|8 bytes  (pointer-sized)
//   ObjectSize        win:UInt64        8 bytes
//   SampledByteOffset win:UInt64        8 bytes
//
static bool TryParseAllocationSampled(
    byte[] payload, int pointerSize,
    out string? typeName, out long objectSize)
{
    typeName   = null;
    objectSize = 0L;

    int typeNameStart = 4 + 2 + pointerSize;           // fixed prefix length
    int suffixLen     = pointerSize + 8 + 8;            // Address + ObjectSize + SampledByteOffset

    if (payload.Length < typeNameStart + 2 + suffixLen)
        return false;

    // Scan for UTF-16LE null terminator (\0\0).
    int typeNameEnd = typeNameStart;
    while (typeNameEnd + 1 < payload.Length)
    {
        if (payload[typeNameEnd] == 0 && payload[typeNameEnd + 1] == 0) break;
        typeNameEnd += 2;
    }

    if (typeNameEnd + 1 >= payload.Length || typeNameEnd + 2 + suffixLen > payload.Length)
        return false;

    int byteLen = typeNameEnd - typeNameStart;
    typeName = byteLen > 0 ? Encoding.Unicode.GetString(payload, typeNameStart, byteLen) : null;
    objectSize = BitConverter.ToInt64(payload, typeNameEnd + 2 + pointerSize);
    return true;
}
