using HighSpeedRamDrive;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class FileTransferBenchmark {
    private const int Samples = 3;
    private const int BufferSize = 1024 * 1024;
    private const int SmallFileCount = 128;
    private const int SmallFileBytes = 64 * 1024;

    internal static async Task RunAsync() {
        string workDirectory = Path.Combine(Path.GetTempPath(), $"mrbr-copy-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        try {
            string largeSource = Path.Combine(workDirectory, "large.bin");
            await CreateFileAsync(largeSource, 64L * 1024 * 1024);
            string smallSourceDirectory = Path.Combine(workDirectory, "small-source");
            Directory.CreateDirectory(smallSourceDirectory);
            for (int i = 0; i < SmallFileCount; i++)
                await CreateFileAsync(Path.Combine(smallSourceDirectory, $"{i:D4}.bin"), SmallFileBytes, (byte)i);

            await using var drive = RamDriveSession.Mount(RamDriveOptions.ApplicationTemp() with {
                Name = "copy-benchmark",
                CapacityBytes = 256L * 1024 * 1024,
                ChunkSize = 64 * 1024
            });

            Console.WriteLine($"Mounted {drive.MountPoint}; {RuntimeInformation.FrameworkDescription}; {Environment.ProcessorCount} logical processors");
            Console.WriteLine("Copy path is FileStream through the mounted WinFsp filesystem; WinFsp stays in the measured path.");
            Console.WriteLine("Median of 3 runs, host temp directory is warm after first read. Figures are machine-specific.");
            Console.WriteLine();

            string largeDriveSeed = "bench-large-seed.bin";
            await RamDriveFileTransfer.ImportFileAsync(drive, largeSource, largeDriveSeed,
                new RamDriveTransferOptions { CopyMode = RamDriveCopyMode.FileStream, BufferSize = BufferSize });
            long largeSeedBytes = drive.GetStatus().InUseBytes;
            Console.WriteLine("Large file (64 MiB, one file)");
            Console.WriteLine("Mode                         Import MiB/s   Export MiB/s");
            foreach (RamDriveCopyMode mode in new[] {
                RamDriveCopyMode.Auto,
                RamDriveCopyMode.FileStream,
                RamDriveCopyMode.DoubleBufferedFileStream,
                RamDriveCopyMode.RandomAccess
            }) {
                var options = new RamDriveTransferOptions {
                    CopyMode = mode,
                    BufferSize = BufferSize,
                    DoubleBufferThresholdBytes = mode == RamDriveCopyMode.Auto ? 8L * 1024 * 1024 : 0,
                    MaxConcurrentFiles = 1
                };
                double import = await MeasureLargeImportAsync(drive, largeSource, mode, options, largeSeedBytes);
                double export = await MeasureLargeExportAsync(drive, largeDriveSeed, workDirectory, mode, options);
                Console.WriteLine($"{mode,-28} {import,12:N1} {export,14:N1}");
            }
            File.Delete(Path.Combine(drive.RootPath, largeDriveSeed));
            await WaitForInUseAsync(drive, 0);

            var sourceFiles = Directory.EnumerateFiles(smallSourceDirectory).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            const string smallSeedRoot = "bench-small-seed";
            await RamDriveFileTransfer.ImportFilesAsync(drive,
                sourceFiles.Select(path => new RamDriveFileTransferItem(path,
                    Path.Combine(smallSeedRoot, Path.GetFileName(path)))),
                new RamDriveTransferOptions { CopyMode = RamDriveCopyMode.Auto, MaxConcurrentFiles = 4 });
            long smallTotalBytes = (long)SmallFileCount * SmallFileBytes;
            long smallSeedBytes = drive.GetStatus().InUseBytes;

            Console.WriteLine();
            Console.WriteLine($"Small files ({SmallFileCount} × {SmallFileBytes / 1024} KiB; {smallTotalBytes / 1024 / 1024} MiB total)");
            Console.WriteLine("Files in parallel            Import MiB/s   Export MiB/s");
            foreach (int concurrency in new[] { 1, 2, 4, 8 }) {
                var options = new RamDriveTransferOptions {
                    CopyMode = RamDriveCopyMode.FileStream,
                    BufferSize = 64 * 1024,
                    MaxConcurrentFiles = concurrency
                };
                double import = await MeasureSmallImportAsync(drive, sourceFiles, concurrency, options, smallTotalBytes, smallSeedBytes);
                double export = await MeasureSmallExportAsync(drive, sourceFiles, smallSeedRoot, workDirectory,
                    concurrency, options, smallTotalBytes);
                Console.WriteLine($"{concurrency,-28} {import,12:N1} {export,14:N1}");
            }

            byte[] memoryPayload = new byte[8 * 1024 * 1024];
            memoryPayload.AsSpan().Fill(0x4d);
            const string bufferPath = "bench-buffer-input.bin";
            const string streamPath = "bench-stream-input.bin";
            var memoryOptions = new RamDriveTransferOptions {
                CopyMode = RamDriveCopyMode.FileStream,
                BufferSize = BufferSize,
                MaxConcurrentFiles = 1
            };
            var bufferTimer = Stopwatch.StartNew();
            await RamDriveFileTransfer.ImportBufferAsync(drive, memoryPayload.AsMemory(), bufferPath, memoryOptions);
            bufferTimer.Stop();
            File.Delete(Path.Combine(drive.RootPath, bufferPath));
            await WaitForInUseAsync(drive, smallSeedBytes);

            using var sourceStream = new MemoryStream(memoryPayload, writable: false);
            var streamTimer = Stopwatch.StartNew();
            await RamDriveFileTransfer.ImportStreamAsync(drive, sourceStream, streamPath,
                memoryOptions with { CopyMode = RamDriveCopyMode.Auto });
            streamTimer.Stop();
            using var outputStream = new MemoryStream(memoryPayload.Length);
            var exportTimer = Stopwatch.StartNew();
            await RamDriveFileTransfer.ExportStreamAsync(drive, streamPath, outputStream,
                memoryOptions with { CopyMode = RamDriveCopyMode.Auto });
            exportTimer.Stop();
            if (!outputStream.TryGetBuffer(out ArraySegment<byte> copiedBuffer) ||
                !memoryPayload.AsSpan().SequenceEqual(copiedBuffer.AsSpan()))
                throw new InvalidDataException("Stream copy verification failed.");
            Console.WriteLine();
            Console.WriteLine("8 MiB caller memory/stream transfers (MiB/s)");
            Console.WriteLine($"ReadOnlyMemory<byte> → drive: {Throughput(memoryPayload.Length, bufferTimer.Elapsed):N1}");
            Console.WriteLine($"MemoryStream → drive:         {Throughput(memoryPayload.Length, streamTimer.Elapsed):N1}");
            Console.WriteLine($"drive → MemoryStream:         {Throughput(memoryPayload.Length, exportTimer.Elapsed):N1} (content verified)");
            File.Delete(Path.Combine(drive.RootPath, streamPath));
            await WaitForInUseAsync(drive, smallSeedBytes);

            Directory.Delete(Path.Combine(drive.RootPath, smallSeedRoot), recursive: true);
            await WaitForInUseAsync(drive, 0);
        } finally {
            try { Directory.Delete(workDirectory, recursive: true); } catch { }
        }
    }

    private static async Task<double> MeasureLargeImportAsync(RamDriveSession drive, string source,
        RamDriveCopyMode mode, RamDriveTransferOptions options, long baselineBytes) {
        long bytes = new FileInfo(source).Length;
        var samples = new double[Samples];
        for (int sample = 0; sample < Samples; sample++) {
            string relative = $"bench-large-{mode}-{sample}.bin";
            var timer = Stopwatch.StartNew();
            await RamDriveFileTransfer.ImportFileAsync(drive, source, relative, options);
            timer.Stop();
            samples[sample] = Throughput(bytes, timer.Elapsed);
            string drivePath = Path.Combine(drive.RootPath, relative);
            File.Delete(drivePath);
            await WaitForInUseAsync(drive, baselineBytes);
        }
        return Median(samples);
    }

    private static async Task<double> MeasureLargeExportAsync(RamDriveSession drive, string sourceRelative,
        string workDirectory, RamDriveCopyMode mode, RamDriveTransferOptions options) {
        long bytes = 64L * 1024 * 1024;
        var samples = new double[Samples];
        for (int sample = 0; sample < Samples; sample++) {
            string destination = Path.Combine(workDirectory, $"large-{mode}-{sample}.out");
            var timer = Stopwatch.StartNew();
            await RamDriveFileTransfer.ExportFileAsync(drive, sourceRelative, destination, options);
            timer.Stop();
            samples[sample] = Throughput(bytes, timer.Elapsed);
            File.Delete(destination);
        }
        return Median(samples);
    }

    private static async Task<double> MeasureSmallImportAsync(RamDriveSession drive, string[] sourceFiles,
        int concurrency, RamDriveTransferOptions options, long bytes, long baselineBytes) {
        var samples = new double[Samples];
        for (int sample = 0; sample < Samples; sample++) {
            string relativeRoot = $"bench-small-{concurrency}-{sample}";
            var items = sourceFiles.Select(path => new RamDriveFileTransferItem(path,
                Path.Combine(relativeRoot, Path.GetFileName(path)))).ToArray();
            var timer = Stopwatch.StartNew();
            await RamDriveFileTransfer.ImportFilesAsync(drive, items, options);
            timer.Stop();
            samples[sample] = Throughput(bytes, timer.Elapsed);
            Directory.Delete(Path.Combine(drive.RootPath, relativeRoot), recursive: true);
            await WaitForInUseAsync(drive, baselineBytes);
        }
        return Median(samples);
    }

    private static async Task<double> MeasureSmallExportAsync(RamDriveSession drive, string[] sourceFiles,
        string sourceRoot, string workDirectory, int concurrency, RamDriveTransferOptions options, long bytes) {
        var samples = new double[Samples];
        for (int sample = 0; sample < Samples; sample++) {
            string outputDirectory = Path.Combine(workDirectory, $"small-export-{concurrency}-{sample}");
            var items = sourceFiles.Select(path => new RamDriveFileTransferItem(
                Path.Combine(sourceRoot, Path.GetFileName(path)),
                Path.Combine(outputDirectory, Path.GetFileName(path)))).ToArray();
            var timer = Stopwatch.StartNew();
            await RamDriveFileTransfer.ExportFilesAsync(drive, items, options);
            timer.Stop();
            samples[sample] = Throughput(bytes, timer.Elapsed);
            Directory.Delete(outputDirectory, recursive: true);
        }
        return Median(samples);
    }

    private static async Task CreateFileAsync(string path, long length, byte pattern = 0x5a) {
        byte[] buffer = new byte[BufferSize];
        buffer.AsSpan().Fill(pattern);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (length > 0) {
            int count = (int)Math.Min(buffer.Length, length);
            await output.WriteAsync(buffer.AsMemory(0, count));
            length -= count;
        }
    }

    private static double Throughput(long bytes, TimeSpan elapsed) =>
        bytes / elapsed.TotalSeconds / (1024 * 1024);

    private static double Median(double[] values) {
        Array.Sort(values);
        return values[values.Length / 2];
    }

    private static async Task WaitForInUseAsync(RamDriveSession drive, long expectedMaximumBytes) {
        var timeout = Stopwatch.StartNew();
        while (drive.GetStatus().InUseBytes > expectedMaximumBytes && timeout.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        if (drive.GetStatus().InUseBytes > expectedMaximumBytes)
            throw new TimeoutException("WinFsp did not release the deleted benchmark file's chunks.");
    }
}
