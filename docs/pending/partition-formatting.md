# Partitioning and filesystem formatting

**Status:** Pending · depends on block-device backend  
**Related:** [Pending work index](../pending-work.md) · [VHDX/raw backend](vhdx-raw-backend.md)

## Goal

Define partition tables and format an actual block image with an explicitly selected filesystem after the VHDX/raw provider exists.

## Proposed usage

```csharp
var layout = RamDrivePartitionLayout.SinglePartition(startOffset: 1 * MiB);
await image.FormatAsync(layout, RamDriveDiskFileSystem.Ntfs, cancellationToken);
```

## Requirements

- Choose supported partition schemes and filesystem types; expose alignment and reserved-space requirements.
- Prefer well-supported Windows formatting/virtual-disk APIs over writing filesystem structures by hand.
- Require explicit selection where formatting destroys existing image contents.
- Validate image size and sector geometry before partition creation.
- Define mount/format privilege requirements and safe detach behavior.
- Keep native `.mrd` snapshots independent from formatted disk images.

## Acceptance

Formatted images are recognized by Windows, have the configured partition/filesystem layout, and pass independent integrity checks. Reformatting an existing image requires an explicit destructive option.
