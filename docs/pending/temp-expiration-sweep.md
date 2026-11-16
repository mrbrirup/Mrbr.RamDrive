# Age-based temp-entry sweep

**Status:** Pending · P1  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Add an explicit sweep for stale temporary entries that were not covered by a live `RamDriveTempWorkspace` lease, for example files left by a terminated worker or a previous application run on a persistent drive.

## Proposed usage

```csharp
RamDriveTempSweepResult result = await drive.PurgeExpiredTempEntriesAsync(
    olderThan: TimeSpan.FromHours(8),
    cancellationToken: cancellationToken);
```

## Requirements

- Keep the existing per-workspace TTL behavior; this is an additional drive-level maintenance operation.
- Restrict deletion to a library-owned temp namespace or an explicitly selected relative root.
- Define the age source (`LastWriteTimeUtc` is the proposed default); expose the cutoff so applications can choose a policy.
- Skip reparse points and never traverse outside the selected root.
- Handle open/delete-pending files without corrupting open handles; return counts and per-path failures.
- Support cancellation and progress without writing to console or displaying UI.
- Make dry-run/report-only behavior available before enabling destructive cleanup on persistent drives.

## Acceptance

Tests cover stale versus fresh files, open handles, reparse points, cancellation, path traversal, and cleanup failures. The sweep cannot touch files outside its registered root.
