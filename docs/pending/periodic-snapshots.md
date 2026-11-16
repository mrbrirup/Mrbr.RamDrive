# Periodic snapshot scheduling

**Status:** Pending · P2  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Offer an opt-in policy for repeated snapshots without making the library silently create background workers for every mounted drive.

## Proposed usage

```csharp
await using var schedule = drive.StartSnapshotSchedule(new RamDriveSnapshotScheduleOptions {
    Interval = TimeSpan.FromMinutes(15),
    DestinationPath = snapshotPath,
    FailureHandler = appFailureHandler
});
```

## Requirements

- Scheduling is explicit and disposable; default mounts remain volatile and do not autosave.
- Define overlap behavior (serialize, skip, or queue one pending capture) and cancellation semantics.
- Reuse atomic `.mrd` save, caller quiescence, metadata selection, compression, and failure decisions.
- Report progress and failures to application callbacks; never prompt from the library.
- Support retention/rotation policy separately from writing a new snapshot; preserve the last good copy on failure.
- Distinguish cache-seed cadence from disaster-recovery durability.

## Acceptance

Tests cover overlapping ticks, cancellation, failure retry, application shutdown, and retention while confirming no snapshot is created unless the schedule is explicitly started.
