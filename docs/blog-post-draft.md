# Blog post draft: A RAM drive for video-analysis scratch files

**Working title:** Keep the source on disk, put the churn in RAM: a practical
Windows RAM drive for video analysis

**Audience:** .NET developers and computer-vision users with spare memory who
process video and produce large amounts of temporary output.

**Thesis:** A RAM drive can help when an application repeatedly writes and reads
temporary files. It is most useful as a configurable scratch space alongside the
source disk, not as a blanket replacement for an SSD or as a way to make GPU
inference faster.

## Draft

Video analysis often starts with one source file and creates many more files along
the way. A YOLO26 pipeline, for example, may decode a video, save selected frames
as PNG or JPEG files, resize or annotate them, and pass those images into inference.
The source may be read once, while the temporary frames and intermediate images
are repeatedly created, inspected, and discarded.

That pattern suggests a simple split: leave the original video on its normal disk
and direct generated images and other disposable intermediates to a RAM drive.
For a Windows application that expects paths and ordinary file APIs, a RAM-backed
filesystem can provide that scratch location without making the application
manage a custom in-memory file format.

Mrbr.RamDrive is a C# library for creating that kind of workspace. It uses WinFsp
to expose managed memory as a Windows file system. An application can create a
workload-configured drive, use regular file paths for the tools in its pipeline,
and unmount the drive when its work is complete.

```csharp
using HighSpeedRamDrive;

await using var frameDrive = RamDriveFactory.CreateImageDrive(new RamDriveSettings {
    FindFreeDriveLetter = DriveLetterSelection.Provided,
    DriveLetter = "R",
    DriveSizing = DriveSizing.DefinedSize,
    Size = 8
});

string framesDirectory = Path.Combine(frameDrive.RootPath, "frames");
Directory.CreateDirectory(framesDirectory);

// Configure the decoder or frame-extraction step to write its PNG/JPEG files
// beneath framesDirectory, then point later pipeline stages at those paths.
```

The video stays where it is. The application can read it from `C:\` (or another
source drive) and write extracted images to `R:`. If the inference library accepts
an in-memory image buffer directly, passing the buffer through can avoid file
creation altogether; the RAM drive is useful when one of the tools requires a
filesystem path or when intermediate files need to be shared between processes.

### Workload-shaped defaults

Different temporary workloads benefit from different allocation sizes. The
library provides helpers for video, images, databases, small/medium/large files,
application temp space, builds, caches, and general files. Each helper starts
with a capacity and chunk-size default, while a `RamDriveSettings` object chooses
the drive letter and can override the size.

Letters can be selected from the first free letter scanning `A:` to `Z:`, from
the last free letter scanning `Z:` to `A:`, or supplied explicitly. Capacity can
be a defined size in GiB (rounded down to a whole GiB) or a percentage of currently
free or total physical RAM. These are capacity limits; the library allocates RAM
as files are written instead of reserving the full configured size when it mounts.

The drive is temporary by default. That makes it useful for extracted frames,
thumbnails, transient image conversions, compiler intermediates, package caches,
database test data, and other files that can be regenerated. The library also
supports quota- and expiry-limited temp workspaces, asynchronous file and
directory transfers, and `.mrd` snapshots when an application needs to preserve
or seed data. Snapshotting a database still requires application-level
coordination so the captured files form a consistent database state.

### What the comparison showed

In one workstation comparison, a WinFsp-backed RAM drive was tested against the
machine's C: NVMe drive with direct, unbuffered I/O. The RAM drive delivered much
higher sequential write throughput in that test: about 4.1 GiB/s at queue depth 1
versus about 330 MiB/s for the NVMe drive. A queue-depth-1 sequential read was
roughly similar, while the NVMe drive was faster for queue-depth-8 sequential
reads. Small random reads at queue depth 1 favored the RAM drive, but at higher
queue depth the RAM drive showed worse latency and lower throughput.

Those numbers describe one machine, one filesystem implementation, and one test
configuration. They are not a general ranking of RAM drives and NVMe devices.
They illustrate why the actual workload matters: a RAM drive can help with
write-heavy scratch work, while a storage device may still be better for
high-concurrency reads. The project README contains additional benchmark details.

### RAM is not GPU memory

Putting frames on a RAM drive does not move them into a GPU's dedicated memory or
make YOLO inference execute faster by itself. The model, decoder, preprocessing,
and data-transfer path determine CPU and GPU use. The drive only changes where
filesystem-backed temporary bytes are stored. Measure the end-to-end pipeline to
see whether file I/O is actually a bottleneck.

It may also be unnecessary to write every decoded frame to disk. If the decoder
and inference APIs can pass image buffers directly, an in-memory pipeline can
remove both disk access and filesystem callbacks. Use the RAM drive when path-based
intermediate files make integration simpler, when tools communicate through
folders, or when the files are reused by multiple stages.

### A practical rule of thumb

Keep valuable source media and final results on durable storage. Put disposable,
high-churn intermediates on the RAM drive, set a capacity that leaves headroom for
Windows and the inference workload, and make cleanup or snapshot behavior
explicit. This gives video pipelines a fast scratch area without treating
volatile memory as a permanent home for source footage or results.

## Editorial notes before publication

- Add the specific machine configuration and full benchmark table if the article
  is going to quote the comparison numbers.
- Confirm the exact model and runtime versions used in any public YOLO26 example.
- Add a diagram showing source video on disk, extracted frames on the RAM drive,
  and inference running in the consuming application.
- Link the [WinFsp license and FLOSS exception](https://github.com/winfsp/winfsp/blob/master/License.txt)
  and preserve the required attribution. The project code itself is MIT-licensed;
  WinFsp's terms apply separately.
