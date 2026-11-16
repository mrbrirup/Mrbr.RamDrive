# Lazy snapshot/image restore

**Status:** Pending · P3 · format-dependent  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Reduce startup latency by loading or decompressing file contents on first access instead of materializing every file before mount.

## Proposed usage

```csharp
await using var drive = await RamDriveSession.LoadSnapshotAsync(path,
    loadOptions: new RamDriveSnapshotLoadOptions { RestoreMode = RamDriveRestoreMode.Lazy },
    cancellationToken);
```

## Requirements

- Determine whether `.mrd` v1 supports efficient random access; ZIP compression may require format changes or a chunk-indexed format.
- Define when checksum verification occurs and how read errors are surfaced after mount.
- Bound open archive handles, decompression buffers, and cache memory.
- Handle snapshot removal/replacement while a drive is mounted; pin or copy required metadata.
- Support cancellation and explicit eager fallback.
- Measure mount latency, first-read latency, random access, and total memory against eager restore.

## Acceptance

Lazy mode returns a mounted drive before all file payload is resident, returns correct data on demand, verifies integrity before exposing corrupted bytes, and cleans up archive resources on unmount.
