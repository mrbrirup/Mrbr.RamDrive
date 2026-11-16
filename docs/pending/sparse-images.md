# Sparse disk-image allocation

**Status:** Pending · depends on block-device backend  
**Related:** [Pending work index](../pending-work.md) · [VHDX/raw backend](vhdx-raw-backend.md)

## Goal

Allow large virtual capacities without immediately consuming the full host-disk size, while reporting logical capacity separately from physical allocation.

## Proposed usage

```csharp
new RamDriveDiskImageOptions {
    Format = RamDriveDiskImageFormat.Vhdx,
    Allocation = RamDriveImageAllocation.Sparse,
    MaxPhysicalBytes = 100L * 1024 * 1024 * 1024
};
```

## Requirements

- Define fixed versus dynamically expanding image semantics and the host disk-space ceiling.
- Report virtual capacity, current allocated host bytes, and remaining expansion budget separately.
- Handle host disk-full errors as writes, not only at image creation.
- Specify zero-fill semantics for unallocated sectors and behavior when trimming/releasing blocks.
- Make compaction explicit and safe against concurrent image users.
- Benchmark sequential and random workloads against fixed-size images.

## Acceptance

Sparse images attach as expected, unallocated sectors read as zero, host allocation grows within configured bounds, and disk-full never corrupts image metadata.
