# Additional drive status counters

**Status:** Pending · P2  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Extend `RamDriveStatus` with useful live counts and capacity history while keeping status collection cheap enough for frequent monitoring.

## Proposed usage

```csharp
RamDriveStatus status = drive.GetStatus();
Console.WriteLine($"{status.FileCount} files; peak allocation {status.HighWaterMarkBytes} bytes");
```

## Requirements

- Candidate fields: file count, directory count, high-water allocation, quota-limited workspace count, and last allocation failure.
- Define whether cached chunk bytes are process-wide or drive-local; do not imply that shared-cache values belong to one drive.
- Avoid walking the entire namespace for every status poll; maintain counters in filesystem callbacks or expose an explicitly slower detailed query.
- Make concurrency semantics and counter reset behavior explicit.
- Keep UI and formatting in the referencing application.

## Acceptance

Counters remain consistent through create, delete, rename, overwrite, resize, and failed allocation. Benchmarks confirm status polling does not materially affect file I/O.
