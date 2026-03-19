# AllocationSampledRepro

Minimal self-contained repro for the missing `AllocationSampled` (EventID 303) typed schema in
`Microsoft.Diagnostics.Tracing.TraceEvent` 3.1.30.

## Requirements

- **.NET 10+ SDK and runtime** — `AllocationSampled` is never emitted on .NET 9 or earlier,
  regardless of keywords. Check with `dotnet --version`.
- No other dependencies; NuGet packages are restored automatically on first run.

## Run

```bash
cd AllocationSampledRepro
dotnet run
```

## What it demonstrates

The program runs in three phases:

1. **Capture** — instruments itself via `DiagnosticsClient` with
   `AllocationSamplingKeyword = 0x80000000000`, allocates 200 000 strings to guarantee sampled
   events, and saves a temporary `.nettrace` file.

2. **Native TraceEvent API (broken)** — opens the trace with `EventPipeEventSource` and reads
   EventID 303 via `source.Dynamic.All`. `PayloadNames` is `null`, `PayloadByName` returns
   `null`, and `source.Clr.All` receives zero events for EventID 303 — confirming the event is
   not routed through `ClrTraceEventParser` at all.

3. **Raw byte workaround (works)** — re-opens the same trace and decodes each event's binary
   payload directly using `data.EventData()` + `data.PointerSize`, recovering `TypeName` and
   `ObjectSize` successfully.

## Expected output

```
══ Native TraceEvent API (broken) ═════════════════════════════════════
  PayloadNames:                <null>
  PayloadByName("TypeName"):   <null>
  PayloadByName("ObjectSize"): <null>
  PayloadValue(0):             <null>
  source.Clr.All hits for EventID 303:     0  (0 = not routed through ClrTraceEventParser)
  source.Dynamic.All hits for EventID 303: 47

══ Raw EventData() workaround (works) ══════════════════════════════════
  [1] TypeName=System.String   ObjectSize=96 bytes
  [2] TypeName=System.String   ObjectSize=96 bytes
  [3] TypeName=System.Byte[]   ObjectSize=512 bytes
  ...
  Total events decoded via raw bytes: 47 / 47

RESULT: native API yields no payload data; raw byte parsing works.
        ClrTraceEventParser needs a typed schema for EventID 303.
```

## Binary payload layout

Decoded from the dotnet/runtime reference consumer
(`src/tests/tracing/eventpipe/randomizedallocationsampling/manual/AllocationProfiler/Program.cs`):

```
AllocationKind    win:UInt32        4 bytes
ClrInstanceID     win:UInt16        2 bytes
TypeID            win:Pointer       4 or 8 bytes (pointer-sized)
TypeName          win:UnicodeString variable (null-terminated UTF-16LE)
Address           win:Pointer       4 or 8 bytes (pointer-sized)
ObjectSize        win:UInt64        8 bytes
SampledByteOffset win:UInt64        8 bytes
```

Pointer size is read from `TraceEvent.PointerSize`.

## References

- [dotnet/runtime#104955](https://github.com/dotnet/runtime/pull/104955) — PR that introduced
  `AllocationSampled` in .NET 10
- [`ClrEtwAll.man`](https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/ClrEtwAll.man) —
  canonical ETW manifest defining EventID 303
- [`AllocationProfiler/Program.cs`](https://github.com/dotnet/runtime/blob/main/src/tests/tracing/eventpipe/randomizedallocationsampling/manual/AllocationProfiler/Program.cs) —
  dotnet/runtime reference consumer using raw byte parsing
- [`ClrTraceEventParser.cs.base`](https://github.com/microsoft/perfview/blob/main/src/TraceEvent/Parsers/ClrTraceEventParser.cs.base) —
  file requiring the new typed event definition
