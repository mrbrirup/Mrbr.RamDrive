# Mrbr.RamDrive pending work

**Reviewed:** 15 November 2026  
**Source:** [Helper-function roadmap](helper-functions-roadmap.md)

This is the implementation backlog. Each unchecked item links to its own note with a usage sketch, requirements, and constraints. Completed drive management, temp quotas/expiry, async copy helpers, and `.mrd` snapshot v1 are intentionally omitted.

## Application and temp helpers

- [ ] [Child-process TEMP/TMP environment lease](pending/process-temp-environment.md) — P1
- [ ] [Age-based temp-entry sweep](pending/temp-expiration-sweep.md) — P1
- [ ] [Caller-owned temp-file lease mode](pending/temp-file-ownership.md) — P2
- [ ] [Configured startup directories](pending/startup-directories.md) — P2
- [ ] [Sensitive-buffer clearing policy](pending/sensitive-buffer-clearing.md) — P2

## Configuration and management

- [ ] [Versioned JSON profile import/export](pending/profile-json.md) — P1
- [ ] [Additional drive status counters](pending/status-counters.md) — P2
- [ ] [Space reservation and low-space notifications](pending/space-notifications.md) — P1
- [ ] [Console drive-management commands](pending/drive-cli.md) — P2

## Persistence and metadata

- [ ] [Periodic snapshot scheduling](pending/periodic-snapshots.md) — P2
- [ ] [Incremental snapshots](pending/incremental-snapshots.md) — P2
- [ ] [Read-only cache seed with copy-on-write overlay](pending/copy-on-write-overlay.md) — P2
- [ ] [ACL persistence](pending/acl-persistence.md) — P2; filesystem prerequisite
- [ ] [Reparse-point persistence](pending/reparse-point-persistence.md) — P2; filesystem prerequisite
- [ ] [Snapshot reliability integration coverage](pending/snapshot-reliability-coverage.md) — P1
- [ ] [VHDX and raw disk-image backend](pending/vhdx-raw-backend.md) — Architecture phase
- [ ] [Block-device drive emulation](pending/drive-emulation.md) — Depends on block-device backend
- [ ] [Partitioning and filesystem formatting](pending/partition-formatting.md) — Depends on block-device backend
- [ ] [Sparse disk-image allocation](pending/sparse-images.md) — Depends on block-device backend
- [ ] [Lazy snapshot/image restore](pending/lazy-restore.md) — P3; format-dependent

## Windows platform integration

- [ ] [NUMA-aware allocation](pending/numa-placement.md) — P3; benchmark-led
- [ ] [Sleep and hibernate persistence hooks](pending/sleep-hibernate.md) — P3
- [ ] [Windows service and boot startup](pending/service-startup.md) — P3

## Ordering

1. Finish the P1 application helpers and snapshot reliability coverage.
2. Add profile/management and persistence policy features that stay within the current file-level backend.
3. Design the block-device provider before implementing VHDX/raw, partitioning, formatting, and sparse allocation.
4. Treat NUMA, lazy restore, sleep/hibernate, and service startup as optional platform work after the core storage and lifecycle designs settle.

The library remains UI-free. Any prompt, progress display, retry decision, or child-process interaction belongs to the referencing application. VHDX/raw work must produce actual interoperable disk images; an `.mrd` archive with a different extension does not qualify.
