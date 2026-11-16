# Reparse-point persistence

**Status:** Pending · P2 · filesystem prerequisite  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Represent supported reparse points in the file system and optionally preserve their tags/data in snapshots and transfers.

## Proposed usage

```csharp
await drive.SaveSnapshotAsync(path, new RamDriveSnapshotOptions {
    Metadata = RamDriveSnapshotMetadata.Default |
               RamDriveSnapshotMetadata.ReparsePointData
}, cancellationToken);
```

## Requirements

- Add WinFsp reparse-point callbacks and define the supported tag set (for example, symbolic links only initially).
- Preserve link targets as link data; never follow a link while enumerating/importing/exporting a tree.
- Reject unsupported tags explicitly and prevent path traversal or link cycles during restore.
- Keep reparse data opt-in and validate target/tag lengths before mounting restored content.
- Add conflict and privilege behavior for link creation on Windows.

## Acceptance

Tests cover supported link round-trip, dangling links, cycles, traversal attempts, unsupported tags, and snapshot tampering without following links.
