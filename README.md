# Mrbr.RamDrive

A RAM-backed file system for Windows, written in C# targeting .NET 11 and using
[WinFsp](https://github.com/winfsp/winfsp).

## Building

Install the .NET 11 SDK, then build the library and console test app:

```powershell
dotnet build .\Mrbr.RamDrive.slnx
```

## Running one or more RAM drives

Run on Windows and ensure the selected drive letters are free. The NuGet
dependency supplies the .NET bindings; WinFsp's native runtime and driver must
also be installed. The app can download and install them for you.

With no arguments, the console test app mounts the default video workload on the
last available drive letter. Workload defaults select capacity and chunk size,
while the caller selects a free letter or supplies one explicitly:

```powershell
dotnet run --project .\Mrbr.RamDrive.ConsoleTestApp\Mrbr.RamDrive.ConsoleTestApp.csproj
dotnet run --project .\Mrbr.RamDrive.ConsoleTestApp\Mrbr.RamDrive.ConsoleTestApp.csproj -- --list
dotnet run --project .\Mrbr.RamDrive.ConsoleTestApp\Mrbr.RamDrive.ConsoleTestApp.csproj -- --preset video,images,database
```

Workload defaults have separate capacity limits and allocation chunk sizes; no
workload is tied to a drive letter:

| Workload | Capacity | Chunk | Intended use |
| --- | ---: | ---: | --- |
| `video` | 32 GiB | 1 MiB | AI/video processing and large sequential scratch files |
| `images` | 16 GiB | 256 KiB | Images, thumbnails and temporary renders |
| `text` / `small-files` | 2 GiB | 4 KiB | Small temporary files, logs and JSON |
| `misc` / `medium-files` | 8 GiB | 64 KiB | General purpose or mixed-size temporary files |
| `database` | 16 GiB | 4 KiB | Database scratch files and random small I/O |
| `build` | 16 GiB | 64 KiB | Compiler intermediates, package caches and CI scratch data |
| `cache` | 16 GiB | 256 KiB | Reusable application and model caches |
| `large-files` | 32 GiB | 1 MiB | Large sequential temporary files |

Library callers can use workload helpers directly:

```csharp
using HighSpeedRamDrive;

await using var video = RamDriveFactory.CreateVideoDrive(new RamDriveSettings {
    FindFreeDriveLetter = DriveLetterSelection.Provided,
    DriveLetter = "R",
    DriveSizing = DriveSizing.PercentOfFreeRam,
    Size = 50
});

await using var images = RamDriveFactory.CreateImageDrive(new RamDriveSettings {
    FindFreeDriveLetter = DriveLetterSelection.FirstAvailable,
    Size = 8 // DefinedSize is the default; defined sizes are rounded down to whole GiB.
});
```

`DriveLetterSelection.LastAvailable` searches `Z:` through `A:`; `FirstAvailable`
searches `A:` through `Z:`. `Provided` requires `DriveLetter` and accepts `R` or
`R:`. Supplying a drive letter with either automatic choice is an error.
`DriveSizing.DefinedSize` interprets `Size` as GiB and rounds down to a whole
GiB. Percentage modes interpret `Size` as a percentage greater than 0 and up
to 100 of the currently free or total physical RAM. RAM is not preallocated: the result is a
per-drive capacity limit, while the shared memory guard still controls actual
chunk allocation.

Other helpers include `CreateDatabaseDrive`, `CreateSmallFileDrive`,
`CreateMediumFileDrive`, `CreateLargeFileDrive`, `CreateApplicationTempDrive`,
`CreateBuildDrive`, `CreateCacheDrive`, and `CreateMiscellaneousDrive`. All
accept the same optional `RamDriveSettings` overrides.

The capacities are limits, not preallocated memory. Storage grows as files are
written; each drive reserves memory independently, while reusable chunks are
shared by the process. A process-wide memory guard limits chunk storage to 75% of
the GC-reported available memory. Files on these RAM drives are temporary and are
discarded when the app exits. Keep the console open while using them, then press
**Enter** to unmount every drive mounted by this process.

If a compatible WinFsp runtime is missing, the app offers to download and install
it. Enter **y**, then approve the Windows administrator prompt. After installation,
the app checks the runtime again and continues mounting the selected workload. If
Windows Installer reports that a restart is needed, restart Windows and run the app again instead.
The installer is always launched with `/norestart`; the app does not restart your PC.

The download is the unmodified official
[WinFsp 2.2.26286 (2026 Beta5) MSI](https://github.com/winfsp/winfsp/releases/tag/v2.2B5),
matching the `winfsp.net` dependency. This is a prerelease: the latest stable 2.1
runtime is too old for the project's 2.2 bindings. Its published SHA-256 is checked
before installation. Existing compatible runtimes do not require a download or an
administrator prompt. The runtime check supports x86, x64 and ARM64 processes.

Installation requires an internet connection and administrator approval. If you
decline installation, cancel the administrator prompt, or encounter a download or
installation failure, the app prints the result and exits without mounting.
Windows Installer logs are kept at the temporary path printed by the app; downloaded
MSI files are removed after the attempt. You can also install the matching runtime
manually from the release linked above.

When updating `winfsp.net`, update the version, release URL and published checksum
in `WinFspRuntimeInstaller.cs` together. The app checks that its installer manifest
is compatible with the referenced .NET bindings before installing.

Automatically selected letters are checked against the letters currently in use.
If none can be mounted, the app reports the final WinFsp status and exits.

## Performance and benchmarks

The file callbacks copy directly between WinFsp's native buffer and managed
chunks using `Span<T>.CopyTo`. Chunk sizes are powers of two, so chunk indexing
uses shifts and masks. The runtime optimizes span copy and clear operations for
the current CPU; explicit SIMD was not added because these operations are memory
copies and the measured results did not show a consistent advantage over
`Marshal.Copy`. No per-operation managed allocations were observed in the tested
read/write callbacks. GPU work remains with the consuming video or AI application:
this file system stores data in system RAM and does not run GPU processing or
manage discrete GPU memory.

Run the benchmark harness in Release mode:

```powershell
dotnet run -c Release --project .\Mrbr.RamDrive.Benchmarks\Mrbr.RamDrive.Benchmarks.csproj
```

It measures the in-memory file-system callbacks at 64 KiB and 1 MiB transfer
sizes and compares segmented `Span<T>` copies with `Marshal.Copy`. It does not
mount WinFsp, so the figures isolate the storage callbacks from WinFsp/kernel
overhead. On repeated .NET 11 x64 runs, 1 MiB callback transfers ranged around 16–29
GiB/s across the tested chunk sizes. The 4 KiB chunks were slower for these large
transfers; 64 KiB, 256 KiB and 1 MiB chunks traded places between reads and
writes. Treat the measurements as machine-specific guidance rather than a
hardware guarantee. The presets prioritize workload shape and allocation
granularity; benchmark against your own files and hardware before changing them.

To compare the asynchronous copy helpers through an actual mounted WinFsp drive,
run:

```powershell
dotnet run -c Release --project .\Mrbr.RamDrive.Benchmarks\Mrbr.RamDrive.Benchmarks.csproj -- --file-transfer
```

On the latest .NET 11.0.0 / 12 logical processor run, `Auto` measured 1,411 MiB/s
for a 64 MiB host-to-drive copy and 1,454 MiB/s drive-to-host. The explicit
double-buffered stream reached 1,415 MiB/s import and 1,761 MiB/s export;
`RandomAccess` measured 1,299/1,045 MiB/s. Consecutive runs varied, so these
figures are guidance rather than a stable ranking. For 128 × 64 KiB files,
concurrency 8 measured 26/28 MiB/s (import/export), compared with 6/21 MiB/s at
concurrency 1. The roadmap has the full table and workload details.

## Helper-function roadmap

See [the helper-function roadmap](docs/helper-functions-roadmap.md) for a
competitor feature review and a proposed backlog covering temp workspaces,
import/export, persistence and restore, and specialized workloads.

See the [pending-work checklist](docs/pending-work.md) for outstanding items and
their separate usage/requirements notes.

## Tests

```powershell
dotnet test .\Mrbr.RamDrive.slnx
```

The tests cover runtime compatibility, setup decisions, checksum validation,
download failures and installer arguments. They do not download or install a driver.

## Library helpers

The library has no UI. A referencing application chooses how to present prompts
and progress. The simple default creates a volatile application-temp drive and a
quota-limited workspace:

```csharp
await using var drive = RamDriveSession.MountApplicationTemp();
await using var workspace = drive.CreateTempWorkspace();
await using var output = workspace.CreateTempFile(extension: ".bin");
await output.Stream.WriteAsync(data, cancellationToken);
Console.WriteLine($"Temporary file: {output.Path}");
```

The default drive has a 2 GiB capacity, chooses an available drive letter, and
does not persist on unmount. The workspace defaults to a 512 MiB logical-data
quota, 10,000 entries, and an eight-hour expiry. `RamDriveManager` mounts multiple
configured drives, while `RamDriveDirectoryTransfer` streams folders in and out
with bounded file concurrency. For explicit single-file or batch copy operations,
use `RamDriveFileTransfer`:

```csharp
await RamDriveFileTransfer.ImportFileAsync(drive, sourcePath, "work/input.bin", cancellationToken: cancellationToken);

var files = sourcePaths.Select(path => new RamDriveFileTransferItem(
    path, Path.Combine("work", Path.GetFileName(path))));
await RamDriveFileTransfer.ImportFilesAsync(drive, files, new RamDriveTransferOptions {
    MaxConcurrentFiles = 8,
    CopyMode = RamDriveCopyMode.Auto
}, cancellationToken);

await RamDriveFileTransfer.ExportFileAsync(drive, "work/result.bin", outputPath, cancellationToken: cancellationToken);

await RamDriveFileTransfer.ImportStreamAsync(drive, inputStream, "work/stream.bin", cancellationToken: cancellationToken);
await RamDriveFileTransfer.ImportBufferAsync(drive, imageBytes.AsMemory(), "work/image.bin", cancellationToken: cancellationToken);
await RamDriveFileTransfer.ExportStreamAsync(drive, "work/result.bin", outputStream, cancellationToken: cancellationToken);
```

`Auto` uses pooled sequential buffers for imports and small files, and a
double-buffered stream pipeline for large exports. The
copy calls are asynchronous and `MaxConcurrentFiles` bounds parallel file work.
The stream overloads leave caller-owned streams open; the memory overload writes
the supplied `ReadOnlyMemory<byte>` directly without renting a second copy buffer.
The APIs avoid UI automation, but file access still goes through WinFsp because
the current drive is a user-mode filesystem. `RandomAccess` does not mean
unbuffered I/O; unbuffered sector I/O is not a useful mode for this RAM-backed
filesystem.

`RamDriveSnapshot` currently saves and restores versioned `.mrd` archives. Choose
`DisasterRecovery` or `CacheSeed` as the snapshot purpose and select supported
metadata in `RamDriveSnapshotOptions`. Attributes and timestamps are supported;
ACL and reparse-point data are not represented by this filesystem yet. A caller
can pass an `IRamDriveSaveFailureHandler` to decide whether a failed save should
retry, unmount without saving, or leave the drive mounted. The library never
displays a prompt.

```csharp
await drive.SaveSnapshotAsync(snapshotPath, new RamDriveSnapshotOptions {
    Purpose = RamDriveSnapshotPurpose.DisasterRecovery
}, cancellationToken);
```

VHDX and raw sector images are not supported by this file-level filesystem. They
need a separate block-device backend; the roadmap records this as a distinct
architecture phase.

See the [helper-function roadmap](docs/helper-functions-roadmap.md) for the
competitor review, feature priorities, and design details.

## License

The original code in this repository is licensed under the [MIT License](LICENSE).
Third-party components retain their own licenses; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

WinFsp is a separate dependency licensed under GPLv3 with a special exception for
Free/Libre and Open Source Software (FLOSS). See the
[WinFsp licence and exception](https://github.com/winfsp/winfsp/blob/master/License.txt)
for the exact terms. Qualifying FLOSS software may link to the platform-specific
WinFsp DLLs under the exception without adopting GPLv3, provided it meets the
exception's conditions. Those include carrying the required WinFsp attribution in
the user interface and user-facing documentation, and not linking or distributing
the software with proprietary software when relying on the exception.

### WinFsp attribution

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos

[WinFsp repository](https://github.com/winfsp/winfsp)

See [the blog-post draft](docs/blog-post-draft.md) for a write-up of the project
and its intended workloads.
