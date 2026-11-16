# Space reservation and low-space notifications

**Status:** Pending · P1  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Let workloads reserve an expected amount of space before starting, receive structured low-space/allocation-failure signals, and optionally spill work to a disk-backed directory.

## Proposed usage

```csharp
if (!drive.TryReserve(expectedBytes, out RamDriveReservation? reservation))
    return await ProcessOnDiskAsync(input, cancellationToken);

using (reservation)
    await ProcessOnDriveAsync(input, cancellationToken);
```

## Requirements

- Define whether reservation consumes capacity, constrains writes, or is advisory; avoid double-counting actual allocated chunks.
- Make reservations disposable and safe under concurrent callers.
- Expose a typed allocation-failed result/event with requested bytes, current usage, and capacity.
- Provide configurable low-space thresholds with rate limiting/coalescing so callbacks do not flood the host UI.
- Keep UI, retry, and spillover policy in the referencing application.
- Cover quotas, chunk rounding, multiple drives, and release after exceptions.

## Acceptance

Concurrent reservation tests prove the configured capacity cannot be over-reserved; failed writes provide enough structured information for a caller to retry or spill.
