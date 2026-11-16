# Block-device drive emulation

**Status:** Pending · architecture phase  
**Related:** [Pending work index](../pending-work.md) · [VHDX/raw backend](vhdx-raw-backend.md)

## Goal

Expose an actual block device backed by RAM or an image file so Windows sees disk sectors and can attach a partitioned filesystem. This is separate from the current file-level WinFsp mount.

## Proposed usage

```csharp
await using var disk = await RamDriveBlockDevice.CreateAsync(new RamDriveBlockDeviceOptions {
    CapacityBytes = 8L * 1024 * 1024 * 1024,
    LogicalSectorSize = 512,
    PhysicalSectorSize = 4096
}, cancellationToken);

await disk.AttachAsync(cancellationToken);
```

Exact API depends on the chosen Windows virtual-disk or storage provider.

## Requirements

- Choose the block-device implementation and driver/Windows API before designing public types.
- Define sector sizes, geometry, flush/discard semantics, concurrency, and device lifetime.
- Isolate platform-specific privileged operations and report required elevation clearly.
- Ensure the device can be safely detached even after failed writes or interrupted saves.
- Keep file-level WinFsp sessions and `.mrd` snapshots working independently.
- Benchmark sector I/O, CPU overhead, and RAM use against the existing file-level filesystem.

## Acceptance

Windows recognizes the device as a disk, external tools can inspect sectors and partitions, and attach/detach plus failure paths do not leak handles or silently lose persisted state.
