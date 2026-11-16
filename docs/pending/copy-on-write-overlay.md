# Read-only cache seed with copy-on-write overlay

**Status:** Pending · P2  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Mount a reusable cache seed as an immutable base and give each session an isolated writable overlay, avoiding full copies for parallel workers.

## Proposed usage

```csharp
await using var cache = await RamDriveOverlaySession.MountAsync(seedSnapshot, options, cancellationToken);
await cache.WriteFileAsync("index.bin", update, cancellationToken);
```

## Requirements

- Define base lookup, overlay writes, truncates, rename, delete tombstones, and metadata overrides.
- Ensure writes never modify the shared base; concurrent overlays must remain isolated.
- Define overlay capacity/quota behavior and cleanup on cancellation or process termination.
- Verify base snapshot identity and hashes before use; define compatibility and migration rules.
- Provide explicit export/merge behavior. Never mutate the base during merge.
- Benchmark mount time, random reads, write amplification, and memory use against copy-on-start.

## Acceptance

Tests prove two sessions see the same base but not each other's writes, deletes mask base entries, and overlay failure cannot corrupt the seed.
