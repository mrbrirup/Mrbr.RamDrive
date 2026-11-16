# NUMA-aware allocation

**Status:** Pending · P3 · benchmark-led  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Optionally allocate or schedule drive memory near the CPUs processing it on multi-socket/NUMA systems.

## Proposed usage

```csharp
var options = new RamDriveOptions {
    MemoryPlacement = RamDriveMemoryPlacement.PreferNode,
    NumaNode = 1
};
```

## Requirements

- Establish measurable NUMA locality benefit for this workload before adding API surface.
- Review the process-wide shared chunk cache; per-drive placement may require cache partitioning or node-tagged chunk pools.
- Define fallback behavior on single-node systems and when the requested node is unavailable.
- Keep placement advisory unless the allocator can guarantee it; report actual placement where possible.
- Keep platform-specific interop isolated behind an allocation provider.

## Acceptance

Benchmarks show a repeatable throughput/latency improvement on supported hardware without regressions on ordinary systems. Unsupported platforms fall back cleanly.
