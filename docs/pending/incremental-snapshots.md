# Incremental snapshots

**Status:** Pending · P2  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Reduce repeated snapshot time and storage by writing only changed data after a compatible base snapshot.

## Proposed usage

```csharp
await drive.SaveIncrementalSnapshotAsync(baseSnapshot, deltaPath, options, cancellationToken);
await RamDriveSnapshot.RestoreChainAsync(baseSnapshot, [deltaPath], driveOptions, cancellationToken);
```

Names are illustrative; a single-file materialized format may be safer than a long chain.

## Requirements

- Define dirty tracking for writes, truncates, rename, delete, attributes, and directory changes.
- Give each base/delta a stable identifier, parent reference, version, and integrity hashes.
- Detect missing, reordered, corrupt, or incompatible chain members before exposing a restored drive.
- Bound chain length and provide compaction/materialization.
- Specify consistency behavior when writers are active; reuse the quiescence callback or explicit best-effort mode.
- Benchmark CPU, memory, read amplification, and disk usage against full `.mrd` saves.

## Acceptance

Restore produces the same file tree and selected metadata as a full snapshot. Corruption and missing-base errors leave no partially mounted public drive.
