# VHDX and raw disk-image backend

**Status:** Pending · architecture phase  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Provide a second persistence family alongside `.mrd`: real interoperable VHDX and raw sector images. The existing `.mrd` archive is a file-tree snapshot and cannot be renamed into either format.

## Proposed usage

```csharp
await drive.ExportDiskImageAsync(path, new RamDriveDiskImageOptions {
    Format = RamDriveDiskImageFormat.Vhdx, // or Raw
    PartitionLayout = layout,
    FileSystem = RamDriveDiskFileSystem.Ntfs
}, cancellationToken);
```

Exact API depends on the backend design.

## Requirements

- Introduce a block-device/provider abstraction; do not route sector operations through the existing file-level callbacks.
- Define image capacity, logical/physical sector sizes, allocation policy, sparse/dynamic behavior, and flush semantics.
- VHDX output must follow the VHDX format and be attachable by supported Windows tooling; raw output must contain sectors, not ZIP entries.
- Select a partition/filesystem layout explicitly; preserve the native `.mrd` save/load API as a separate option.
- Handle file-image corruption, write interruption, atomic commit, and disk-full errors without replacing a last-good image.
- Decide Windows privileges, attachment/detachment, and resource ownership before exposing a mounted block volume.

## Architecture boundary

The current WinFsp implementation is a file system with managed nodes/chunks. VHDX/raw requires a block-device layer plus partition and filesystem formatting. Implement this only after choosing the Windows virtual-disk API/backend and its driver/privilege model.

## Acceptance

Images attach/mount using independent Windows or compatible tools, pass format validation, preserve file contents/metadata according to options, and survive interrupted save/load tests.
