# RamDrive helper-function roadmap

**Status:** Competitor research and feature proposal with implementation progress.  
**Research reviewed:** 15 November 2026.

## Purpose

This document turns common features from established RAM-disk tools into a practical helper-function backlog for Mrbr.RamDrive. It covers everyday file and drive operations, temporary workspaces, persistence and restore, and less common use cases.

For the current checkbox backlog and one requirements note per pending item, see [pending work](pending-work.md).

## Product decisions

- The default remains a volatile application-temp drive. General-drive functions, import/export, and persistence are optional benefits for applications that need them.
- UI belongs to the referencing application. The library exposes status, progress, cleanup-error, and save-failure callbacks; it does not display prompts.
- Snapshots support both disaster-recovery and cache-seed use. `Purpose` records caller intent; the caller controls when snapshots are made and whether they are saved again.
- Callers select which supported metadata to save. Attributes and timestamps are supported. ACL and reparse-point data are not represented by the current filesystem and are rejected if requested.
- Temporary workspaces enforce logical-byte and entry-count quotas and expire by default. Per-scope limits remain configurable.
- Two persistence families are desired: a Mrbr.RamDrive archive and interoperable VHDX/raw disk images. The Mrbr archive is implemented; VHDX/raw requires the separate block-device architecture described below.

## Implementation status

| Area | Status |
| --- | --- |
| Default application-temp drive, options, status, and multi-drive manager | Implemented |
| Temp workspaces with byte/entry quotas and expiry | Implemented |
| Streamed, bounded-concurrency directory import/export | Implemented |
| Async one-file and many-file copy with pooled buffers and selectable copy paths | Implemented and benchmarked through a mounted WinFsp drive |
| Mrbr `.mrd` snapshot save/load and save-failure policy callback | Implemented |
| Incremental snapshots, copy-on-write cache seed, ACL/reparse persistence | Follow-up work |
| VHDX/raw interoperable images | Requires block-device backend; not implemented by the file-level backend |

## What other RAM-disk tools provide

| Product/documentation | Notable functionality | Relevance to this library |
| --- | --- | --- |
| [SoftPerfect RAM Disk manual](https://www.softperfect.com/products/ramdisk/manual/adding_disk.htm) | Multiple/manual or startup disks, dynamic allocation, release-on-delete (TRIM), associated image files, save-on-shutdown, and automatic folder creation. Its [CLI](https://www.softperfect.com/products/ramdisk/manual/command_line.htm) can create, destroy, list, inspect, resize, save, load, and save all configured disks. | Strong model for a `RamDriveManager`, status APIs, create-at-startup configuration, and common admin commands. |
| [SoftPerfect RAM File System image manual](https://www.softperfect.com/products/ramfs/manual/image_files.htm) | Related, separate product: versioned A/B image writes, integrity checks, sparse storage, differential saves, and manual, periodic, shutdown, or dismount save triggers. | Useful persistence reliability patterns; these are ideas from the RAM File System product, not claims about SoftPerfect RAM Disk. |
| [Primo Ramdisk feature comparison](https://www.romexsoftware.com/en-us/primo-ramdisk/features.html) and [file-system settings](https://www.romexsoftware.com/en-us/primo-ramdisk/feature-file-system.html) | Multiple disks, configurable filesystem/cluster settings, labels, custom folders, and server/NUMA-oriented options. | Supports keeping presets but adding named, validated configuration objects and startup folder creation. |
| [Primo image features](https://www.romexsoftware.com/en-us/primo-ramdisk/feature-image-file.html) | Manual, timed, shutdown, sleep/hibernate, and before-removal saves; load-only mode; quick/differential save; delayed load; flat, compact, and compressed images. | Shows persistence is a family of explicit policies, rather than just a single `Save()` call. |
| [ImDisk Toolkit documentation](https://sourceforge.net/p/imdisk-toolkit/doc/Home/) and an [open-source RAM-disk service inspired by it](https://github.com/tmcdos/ramdisk) | Saved startup configuration and image support; the service project documents preloading files from a directory, syncing them back on shutdown, exclusions, and TEMP-folder integration. The Toolkit documentation currently marks the project inactive. | Directory import/export and temp integration can be valuable, but they should be opt-in and independent of drive mounting. |

Across these products, the repeated themes are: create/manage more than one volume, control allocation and cleanup, prepare common folders, offer scriptable lifecycle/status operations, and make persistence policy explicit.

## Current project boundary

Mrbr.RamDrive is a **file-level WinFsp filesystem** implemented in managed code. It stores file nodes and byte chunks in memory. It is not currently a raw block device with an NTFS/FAT volume mounted inside it.

Already present:

- WinFsp callbacks for file and directory operations, including create/open/read/write, resize, rename, delete, and metadata.
- Separate capacity and chunk-size configuration per drive; process-wide reusable chunks are shared between drives.
- Workload profiles and console-app selection for video, images, text, miscellaneous files, databases, builds, and caches.
- Allocation-on-write with a configured memory guard.

Added as library helpers:

- `RamDriveFactory` creates video, image, database, small/medium/large-file, application-temp, build, cache, or miscellaneous drives. Workload defaults set capacity and chunk size without assigning fixed letters; `RamDriveSettings` selects a provided or first/last available letter and supports fixed-size or physical-RAM percentage sizing.
- `RamDriveSession.MountApplicationTemp()` creates a volatile 2 GiB app-temp drive and chooses an available letter.
- `RamDriveTempWorkspace` creates a scoped directory, enforces logical-byte and entry quotas through filesystem callbacks, and removes it on disposal or expiry.
- `RamDriveDirectoryTransfer` streams directory trees into and out of a mounted drive with bounded file concurrency, conflict, metadata, cancellation, and size-limit options.
- `RamDriveFileTransfer` copies individual files or batches asynchronously, using pooled buffers and selectable sequential `FileStream`, double-buffered `FileStream`, or offset-based `RandomAccess` I/O.
- `RamDriveManager` coordinates multiple sessions; `RamDriveStatus` reports capacity and memory use.
- `RamDriveSnapshot` saves/restores versioned `.mrd` archives. Save-before-unmount uses an application-provided callback for retry, discard, or keep-mounted decisions.

Still not present:

- Process/user/system TEMP/TMP environment binding. Applications can configure those values for child processes themselves.
- ACL and reparse-point storage, because the current filesystem does not maintain those data.
- Incremental snapshots and a read-only base/copy-on-write cache overlay.
- Raw block devices, VHDX/raw sector images, partitioning, or filesystem formatting.

This boundary matters: Mrbr snapshots serialize the library's file tree and file contents. VHDX/raw interoperability requires a real block-device implementation and valid partition/filesystem layout; renaming an Mrbr archive would not produce an interoperable disk image.

## Suggested helper catalogue

### 1. Common drive and file helpers

| Helper | Suggested behavior | Priority |
| --- | --- | --- |
| `RamDriveOptions` / workload helpers | Validate label, mount point, capacity, and chunk size. Workload defaults set storage tuning; caller settings choose drive letters and fixed or RAM-percentage sizing. | Implemented; JSON profile import/export remains P1 |
| `RamDriveManager` / `RamDriveSession` | Own one or more filesystem hosts, roll back partial mounts, report status, and unmount/dispose in reverse order. | Implemented |
| `RamDriveStatus` | Report configured capacity, bytes in use, free bytes, chunk size, cached bytes, and mount state. Consider file/dir counts and high-water mark later. | Implemented; extended counters P1 |
| File utility methods | Safe scoped temp paths, temp-file creation, streamed and concurrent file/directory import/export, cancellation, and conflict policy. | Implemented |
| Space notifications | `TryReserve`, low-space threshold, and a typed allocation-failed event/result so callers can reduce work or spill to a disk directory. | P1 |
| Import/export configuration | Load and save profile JSON, with schema versioning and validation; provide CLI commands for create/list/status/destroy. | P1 |

The library already has the underlying filesystem callbacks; these helpers should wrap them instead of duplicating storage behavior. Management should return structured status/results and should not require console output.

### 2. Temp files and temp folders

This is the default workflow because the drive is volatile and many intended workloads create disposable files. `RamDriveSession.MountApplicationTemp()` and `CreateTempWorkspace()` implement the basic path.

- `CreateTempFile` / `CreateTempDirectory`: unique, unpredictable names; exclusive creation; optional prefix and extension; return the full path plus a disposable lease. **Implemented.**
- `TempDirectoryScope`: create an isolated root for a job/request and recursively remove its contents when disposed. Include an async-dispose form for callers that await file handles. **Implemented as `RamDriveTempWorkspace`.**
- `TempFileScope`: delete its file on disposal; optionally support a caller-owned file that is only deleted when explicitly requested.
- Optional temp quotas: maximum bytes and file count per scope, in addition to the existing drive capacity. **Implemented and enforced by filesystem callbacks.**
- `PurgeExpiredTempEntries`: remove entries older than a configured age, skipping open or delete-pending files and reporting failures. **Per-scope expiry is implemented; age-based sweep remains follow-up.**
- `RamDriveFileTransfer.ImportFileAsync` and `ExportFileAsync`: copy a file to or from the drive without UI interaction; return the copied byte count.
- `RamDriveFileTransfer.ImportFilesAsync` and `ExportFilesAsync`: process file batches asynchronously with bounded concurrency and aggregate byte/file counts.
- `ImportStreamAsync`, `ExportStreamAsync`, and `ImportBufferAsync`: copy from caller-owned streams or `ReadOnlyMemory<byte>` without requiring an intermediate host file. Stream ownership stays with the caller.
- Optional process TEMP/TMP lease: set process-level values for a launched child process and restore previous values on disposal. Do not change machine-wide or user-wide environment variables by default; that is a separate host/admin operation. **Not implemented.**
- Make temp roots excluded from persistence by default, with explicit include/override support.

Suggested API:

```csharp
await using var drive = RamDriveSession.MountApplicationTemp();
await using var job = drive.CreateTempWorkspace();
await using var frame = job.CreateTempFile(prefix: "frame-", extension: ".png");
await frame.Stream.WriteAsync(frameBytes, cancellationToken);
// The temp file is removed when its lease is disposed; the workspace is removed with the job.

await RamDriveFileTransfer.ImportFileAsync(drive, inputPath, "job/input.mp4", cancellationToken: cancellationToken);
await RamDriveFileTransfer.ExportFileAsync(drive, "job/result.mp4", outputPath, cancellationToken: cancellationToken);
```

Leases must not silently delete outside their own registered root. Cleanup errors should be returned or surfaced, not hidden. A drive disappearing does not make a temp lease a disk-backed recovery mechanism.

### 3. Persist to disk and load persisted data

Provide two related but distinct operations:

1. **Directory import/export:** copy a host directory into the drive at startup, or export the current tree into a host directory. This is easy to inspect and interoperates with normal files, but does not create one self-contained snapshot.
2. **Versioned snapshot:** save the in-memory tree and contents to one archive/image file, then create a new drive from that snapshot. Since this is file-level storage, describe the format as a RamDrive snapshot, not a VHD/NTFS disk image.

Suggested API:

```csharp
await drive.SaveSnapshotAsync(snapshotPath, options, cancellationToken);
await using var restored = await RamDriveSession.LoadSnapshotAsync(snapshotPath, driveOptions, cancellationToken);
await RamDriveDirectoryTransfer.ImportDirectoryAsync(drive, sourceDirectory, transferOptions, cancellationToken);
await RamDriveDirectoryTransfer.ExportDirectoryAsync(drive, destinationDirectory, cancellationToken: cancellationToken);
```

For direct file copies, `RamDriveTransferOptions` selects the copy behavior:

| Workload | Suggested mode | Why |
| --- | --- | --- |
| One small file | `Auto` or `FileStream` | One pooled buffer avoids pipeline setup overhead. |
| One large host-to-drive file | `Auto` or `FileStream`; benchmark `RandomAccess` | `RandomAccess` did not win consistently across repeated runs. |
| One large drive-to-host file | `Auto` or `DoubleBufferedFileStream` | Read-ahead overlapped source reads with destination writes and won locally. |
| Many small files | `Auto` with `MaxConcurrentFiles` around 4–8 | Independent files can progress concurrently; per-file double buffering is unnecessary. |

`Auto` uses one-buffer `FileStream` copying for imports and small files, and double-buffered streams for large exports. For caller-owned streams with a known remaining length above the threshold, it uses double buffering. Defaults use a 128 KiB pooled buffer and `min(Environment.ProcessorCount, 8)` concurrent files. `ClearBuffersOnReturn` can be enabled when clearing pooled copy buffers is more important than throughput. `ImportBufferAsync` writes the caller's `ReadOnlyMemory<byte>` directly in one async call without renting another copy buffer.

The file copy APIs do not use Windows Explorer or another UI. They still use Windows file handles and therefore traverse WinFsp's user-mode filesystem callbacks. `RandomAccess` is offset-based I/O; it is not unbuffered I/O. Unbuffered I/O is not enabled for this RAM filesystem: it does not write directly to physical sectors, and Windows requires aligned offsets, lengths, and buffers for `FILE_FLAG_NO_BUFFERING`.

The optional mounted-drive benchmark compares copy modes for a 64 MiB file and batch sizes for 128 small files:

| Measurement (MiB/s, median of 3) | Import to drive | Export from drive |
| --- | ---: | ---: |
| 64 MiB, `Auto` | 1,411.4 | 1,453.6 |
| 64 MiB, `FileStream` | 1,158.5 | 682.8 |
| 64 MiB, double-buffered stream | 1,414.6 | 1,761.1 |
| 64 MiB, `RandomAccess` | 1,298.7 | 1,044.8 |
| 128 × 64 KiB, concurrency 1 | 6.0 | 20.8 |
| 128 × 64 KiB, concurrency 2 | 11.5 | 25.1 |
| 128 × 64 KiB, concurrency 4 | 19.4 | 25.6 |
| 128 × 64 KiB, concurrency 8 | 26.1 | 28.4 |

An additional single 8 MiB run measured `ReadOnlyMemory<byte>` to drive at 601.7 MiB/s, `MemoryStream` to drive at 559.2 MiB/s, and drive to `MemoryStream` at 1,146.9 MiB/s; the exported bytes were verified. The direct memory write avoids a second pooled copy buffer.

These measurements were taken on 15 November 2026 with .NET 11.0.0, 12 logical processors, a warm host temp file, and an actual WinFsp mount. Consecutive large-file runs varied materially. The benchmark does not show a consistent win for `RandomAccess`; `Auto` therefore uses one-buffer `FileStream` for imports and double-buffered streams for large exports. The small-file run favored more concurrency up to 8; callers should tune it to their data and memory budget. Run it with `dotnet run -c Release --project .\\Mrbr.RamDrive.Benchmarks\\Mrbr.RamDrive.Benchmarks.csproj -- --file-transfer`.

Mrbr `.mrd` snapshot v1 includes a format/version header, drive metadata, normalized relative paths, directory/file entries, selectable attributes and timestamps, lengths, and SHA-256 checksums. It streams file contents so saving a large drive does not require a second full-size copy in RAM. `Purpose` records either disaster-recovery or cache-seed intent; the same archive format supports both.

Reliability requirements:

- Write to a sibling temporary file, flush it, validate the completed snapshot, then atomically replace/rename the target where the platform permits. Preserve the last good snapshot if a save is cancelled or fails. **Implemented for `.mrd`.**
- Validate version, lengths, entry counts, checksums, and destination paths before applying a snapshot. Reject absolute paths, `..` traversal, duplicate normalized names, and entries that exceed configured capacity. **Implemented for `.mrd`.**
- Restore through a staging directory, verify the archive, then mount/import into a new session. Abort the unexposed session if restore fails. **Implemented for `.mrd`; restore needs local staging disk space.**
- Support cancellation and report whether the target snapshot was committed. Do not report a successful save merely because a background task was queued.
- Make autosave opt-in. Manual save and save-before-unmount are implemented. The caller supplies a quiescence callback if it needs a stable view and a failure-policy handler for retry/discard/keep-mounted decisions. Periodic save remains follow-up.
- Compression is optional (`None`, `Fastest`, `Optimal`). Incremental snapshots still need dirty-chunk tracking and benchmarks.

### 4. Niche helpers and common use cases

| Use case | Useful helper idea | Notes |
| --- | --- | --- |
| Video/AI frame pipelines | Per-job temp scope, bounded file-copy concurrency, cleanup after each job. | Store transient inputs/frames; GPU processing and VRAM transfer remain the consuming application's responsibility. |
| Image processing | Thumbnail/render temp workspace; generated-output directory export. | Use a smaller allocation profile for many small images. |
| Build systems and CI | `CreateWorkspaceScope`, cache seed import, cleanup on success/failure, status/low-space reporting. | A per-build scope avoids stale artifacts leaking between jobs. |
| Database scratch | Named temp directory or separate temp drive for spill/sort/tempdb-like workloads. | Do not claim transaction durability. Active database data needs a database-aware durability plan and verified persistence. |
| Test isolation | Create one drive per test fixture, seed input files, expose mount path, tear down automatically. | Useful for deterministic filesystem integration tests; tests must also cover cleanup and failed mounts. |
| Model/package cache | Read-only seed snapshot plus a per-session writable overlay. | Copy-on-write overlay can save startup time and isolate concurrent workers; this is a later storage feature. |
| Download/transcode staging | Temp file with size limit, checksum, then explicit copy/commit to durable storage. | A failed or cancelled operation should clean up the partial temp file. |
| Sensitive scratch data | Optional clear-on-return/clear-on-release policy and volatile-only profile. | Best effort only: RAM pages may be paged, dumped, hibernated, or copied elsewhere. Do not promise forensic erasure. |

Other advanced product features include NUMA placement, custom filesystem formatting, drive emulation, compact/compressed images, delayed/lazy image loading, and save-on-sleep/hibernate. These belong in a later platform-specific layer. The current managed file-level WinFsp storage cannot provide real sector formatting or hard-disk emulation by adding a convenience method.

The requested second persistence family (VHDX/raw) must be a real disk image with a valid partition and filesystem layout. An `.mrd` archive renamed to `.raw` would not be interoperable. VHDX/raw needs a separate block-device provider and host image creation/formatting path; `.mrd` remains the native file-tree snapshot.

## Recommended implementation order and progress

1. **Temp API and management status:** **Implemented.** Process/user/system TEMP environment binding remains opt-in follow-up.
2. **Directory and file import/export:** **Implemented** with pooled buffers, asynchronous bounded concurrency, cancellation, size limits, conflict policy, and progress reporting. The optional mounted benchmark compares copy modes and file concurrency.
3. **Snapshot save/load v1:** **Implemented for `.mrd`** with checksums, atomic commit, staging, and capacity/path validation. Add integration tests for corruption, cancellation, partial failures, and metadata options.
4. **Persistence policies:** **Implemented** manual save, opt-in save-before-unmount, load-only-by-default cache seeds, and caller-controlled retry/discard/keep-mounted decisions. Periodic save remains follow-up.
5. **Incremental and specialized features:** byte/entry quotas and TTL expiry are implemented. Dirty-chunk snapshots and read-only base plus copy-on-write overlay remain follow-up.
6. **Separate architecture:** VHDX/raw images, partitioning, filesystem formatting, NUMA, sleep/hibernate integration, and service-at-boot are not implemented. VHDX/raw needs the block-device provider described above.

## Remaining design limits

- ACL and reparse-point metadata are not represented by the current filesystem; snapshot options reject those selections rather than silently dropping them.
- Restore uses a local staging directory; large snapshots need enough free disk space there.
- The caller owns quiescing writers that require a point-in-time disaster-recovery snapshot. Without a quiescence callback, changes detected during file copy return an error for the caller to retry.
- VHDX/raw is intended as a second persistence format through a future block-device backend; `.mrd` is the only implemented format today.

## Sources

- [SoftPerfect RAM Disk: adding a disk](https://www.softperfect.com/products/ramdisk/manual/adding_disk.htm)
- [SoftPerfect RAM Disk: command-line tool](https://www.softperfect.com/products/ramdisk/manual/command_line.htm)
- [SoftPerfect RAM File System: image files](https://www.softperfect.com/products/ramfs/manual/image_files.htm)
- [Primo Ramdisk: edition feature comparison](https://www.romexsoftware.com/en-us/primo-ramdisk/features.html)
- [Primo Ramdisk: filesystem features](https://www.romexsoftware.com/en-us/primo-ramdisk/feature-file-system.html)
- [Primo Ramdisk: image file features](https://www.romexsoftware.com/en-us/primo-ramdisk/feature-image-file.html)
- [ImDisk Toolkit documentation](https://sourceforge.net/p/imdisk-toolkit/doc/Home/)
- [tmcdos/ramdisk: RAM-disk service and synchronization features](https://github.com/tmcdos/ramdisk)
- [Microsoft .NET `RandomAccess` API](https://learn.microsoft.com/dotnet/api/system.io.randomaccess?view=net-11.0)
- [Microsoft .NET `FileOptions` API](https://learn.microsoft.com/dotnet/api/system.io.fileoptions?view=net-11.0)
- [Microsoft Windows file-buffering and unbuffered-I/O requirements](https://learn.microsoft.com/windows/win32/fileio/file-buffering)
- [WinFsp design and filesystem callback path](https://winfsp.dev/doc/WinFsp-Design/)
