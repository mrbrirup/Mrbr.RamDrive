# Sensitive-buffer clearing policy

**Status:** Pending · P2  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Provide an opt-in best-effort policy for clearing application data from reusable buffers and returned chunks when they are no longer in active use.

## Proposed usage

```csharp
var options = new RamDriveOptions {
    // Illustrative: decide whether release-time clearing is enabled per drive.
    ClearChunksOnRelease = true
};
```

`RamDriveTransferOptions.ClearBuffersOnReturn` already covers transfer buffers. The pending work concerns storage chunks and an explicit drive/workspace policy.

## Requirements

- Clear only after no live file handle can read or write the chunk.
- Define interaction with the shared chunk cache: clear before caching or discard the chunk instead of caching it.
- Keep the high-throughput default unchanged; measure clearing cost and permit opt-in selection.
- Ensure failure/cancellation paths also clear buffers when configured.
- State clearly that managed clearing is best effort and does not guarantee removal from page files, crash dumps, hibernation files, device caches, or copies elsewhere.

## Acceptance

Tests inspect reused buffers for cleared content and exercise delete, resize, cancellation, and cache return paths. Benchmarks report the throughput and allocation impact.
