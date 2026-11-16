using Fsp;
using HighSpeedRamDrive;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

const int sampleCount = 5;
int[] chunkSizes = [4096, 65536, 262144, 1048576];
int[] transferSizes = [65536, 1048576];

if (args.Contains("--file-transfer", StringComparer.OrdinalIgnoreCase)) {
    await FileTransferBenchmark.RunAsync();
    return;
}

Console.WriteLine($"RamDrive in-memory I/O benchmarks | {RuntimeInformation.FrameworkDescription} | {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Logical processors: {Environment.ProcessorCount}; vector acceleration: {System.Numerics.Vector.IsHardwareAccelerated}");
Console.WriteLine("No WinFsp mount is created; this measures the RAM file-system callbacks and copy loops directly.");
Console.WriteLine();
Console.WriteLine("Filesystem callbacks (median of 5 samples)");
Console.WriteLine("Operation  Chunk    Transfer  MiB/s     ns/op     Alloc B/op");

foreach (int transferSize in transferSizes) {
    foreach (int chunkSize in chunkSizes) {
        RunFileSystemCase(chunkSize, transferSize, sampleCount);
    }
}

Console.WriteLine();
Console.WriteLine("Raw segmented copy comparison (1 MiB transfer; median of 5 samples)");
Console.WriteLine("Operation      Chunk    Marshal MiB/s  Span MiB/s  Change");
foreach (int chunkSize in chunkSizes) {
    RunRawCopyCase(chunkSize, 1048576, sampleCount);
}

static void RunFileSystemCase(int chunkSize, int transferSize, int samples) {
    using var fixture = new FileSystemFixture(chunkSize, transferSize);
    int iterations = Math.Clamp((256 * 1024 * 1024) / transferSize, 256, 20000);

    // Warm the callback, JIT and data pages before collecting timed samples.
    for (int i = 0; i < 32; i++) {
        fixture.Write();
        fixture.Read();
    }

    var write = Measure(samples, iterations, transferSize, fixture.Write);
    var read = Measure(samples, iterations, transferSize, fixture.Read);
    PrintRow("Write", chunkSize, transferSize, write);
    PrintRow("Read", chunkSize, transferSize, read);
}

static Measurement Measure(int samples, int iterations, int transferSize, Action operation) {
    var throughput = new double[samples];
    var allocationPerOperation = new double[samples];
    var nanosecondsPerOperation = new double[samples];

    for (int sample = 0; sample < samples; sample++) {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
            operation();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        double seconds = elapsed.TotalSeconds;
        throughput[sample] = (transferSize * (double)iterations / seconds) / (1024 * 1024);
        nanosecondsPerOperation[sample] = seconds * 1_000_000_000 / iterations;
        allocationPerOperation[sample] = allocated / (double)iterations;
    }

    Array.Sort(throughput);
    Array.Sort(nanosecondsPerOperation);
    Array.Sort(allocationPerOperation);
    int median = samples / 2;
    return new Measurement(throughput[median], nanosecondsPerOperation[median], allocationPerOperation[median]);
}

static void PrintRow(string operation, int chunkSize, int transferSize, Measurement measurement) =>
    Console.WriteLine($"{operation,-10} {FormatBytes(chunkSize),-8} {FormatBytes(transferSize),-9} {measurement.MiBPerSecond,8:N1} {measurement.NanosecondsPerOperation,10:N0} {measurement.AllocatedBytesPerOperation,12:N2}");

static void RunRawCopyCase(int chunkSize, int transferSize, int samples) {
    using var fixture = new RawCopyFixture(chunkSize, transferSize);
    int iterations = Math.Clamp((256 * 1024 * 1024) / transferSize, 256, 20000);
    var marshalWrite = Measure(samples, iterations, transferSize, fixture.MarshalToChunks);
    var spanWrite = Measure(samples, iterations, transferSize, fixture.SpanToChunks);
    var marshalRead = Measure(samples, iterations, transferSize, fixture.MarshalFromChunks);
    var spanRead = Measure(samples, iterations, transferSize, fixture.SpanFromChunks);
    PrintCopyRow("Native→chunks", chunkSize, marshalWrite, spanWrite);
    PrintCopyRow("Chunks→native", chunkSize, marshalRead, spanRead);
}

static void PrintCopyRow(string operation, int chunkSize, Measurement marshal, Measurement span) {
    double change = marshal.MiBPerSecond == 0 ? 0 : (span.MiBPerSecond / marshal.MiBPerSecond - 1) * 100;
    Console.WriteLine($"{operation,-14} {FormatBytes(chunkSize),-8} {marshal.MiBPerSecond,13:N1} {span.MiBPerSecond,11:N1} {change,7:+0.0;-0.0;0.0}%");
}

static string FormatBytes(int value) => value switch {
    >= 1048576 when value % 1048576 == 0 => $"{value / 1048576} MiB",
    >= 1024 when value % 1024 == 0 => $"{value / 1024} KiB",
    _ => $"{value} B"
};

readonly record struct Measurement(double MiBPerSecond, double NanosecondsPerOperation, double AllocatedBytesPerOperation);

sealed class FileSystemFixture : IDisposable {
    private readonly RamFileSystem _fileSystem;
    private readonly object _fileNode;
    private readonly IntPtr _nativeBuffer;
    private readonly int _transferSize;

    internal FileSystemFixture(int chunkSize, int transferSize) {
        _transferSize = transferSize;
        _fileSystem = new RamFileSystem(transferSize + (long)chunkSize, chunkSize);
        int status = _fileSystem.Create("\\benchmark.bin", 0, 0,
            (uint)FileAttributes.Archive, Array.Empty<byte>(), (ulong)transferSize,
            out var fileNode, out _, out _, out _);
        if (status != FileSystemBase.STATUS_SUCCESS || fileNode is null)
            throw new InvalidOperationException($"Could not create benchmark file: 0x{status:X8}");

        _fileNode = fileNode;
        _nativeBuffer = Marshal.AllocHGlobal(transferSize);
        unsafe {
            var native = new Span<byte>((void*)_nativeBuffer, transferSize);
            for (int i = 0; i < native.Length; i++)
                native[i] = (byte)(i * 31 + 17);
        }
    }

    internal void Write() {
        int status = _fileSystem.Write(_fileNode, null!, _nativeBuffer, 0, (uint)_transferSize,
            false, false, out uint written, out _);
        if (status < 0 || written != _transferSize)
            throw new InvalidOperationException($"Write benchmark failed: status=0x{status:X8}, bytes={written}");
    }

    internal void Read() {
        int status = _fileSystem.Read(_fileNode, null!, _nativeBuffer, 0, (uint)_transferSize,
            out uint read);
        if (status < 0 || read != _transferSize)
            throw new InvalidOperationException($"Read benchmark failed: status=0x{status:X8}, bytes={read}");
    }

    public void Dispose() {
        _fileSystem.Cleanup(_fileNode, null!, "\\benchmark.bin", FileSystemBase.CleanupDelete);
        Marshal.FreeHGlobal(_nativeBuffer);
    }
}

sealed unsafe class RawCopyFixture : IDisposable {
    private readonly int _chunkSize;
    private readonly int _chunkShift;
    private readonly int _transferSize;
    private readonly byte[][] _chunks;
    private readonly IntPtr _nativeBuffer;

    internal RawCopyFixture(int chunkSize, int transferSize) {
        _chunkSize = chunkSize;
        _chunkShift = BitOperations.TrailingZeroCount((uint)chunkSize);
        _transferSize = transferSize;
        _chunks = new byte[(transferSize + chunkSize - 1) / chunkSize][];
        for (int i = 0; i < _chunks.Length; i++)
            _chunks[i] = GC.AllocateUninitializedArray<byte>(chunkSize);
        _nativeBuffer = Marshal.AllocHGlobal(transferSize);
        new Span<byte>((void*)_nativeBuffer, transferSize).Fill(0x5a);
    }

    internal void MarshalToChunks() {
        int copied = 0;
        while (copied < _transferSize) {
            int chunkIndex = copied >> _chunkShift;
            int chunkOffset = copied & (_chunkSize - 1);
            int count = Math.Min(_transferSize - copied, _chunkSize - chunkOffset);
            Marshal.Copy(IntPtr.Add(_nativeBuffer, copied), _chunks[chunkIndex], chunkOffset, count);
            copied += count;
        }
    }

    internal void SpanToChunks() {
        ReadOnlySpan<byte> source = new((void*)_nativeBuffer, _transferSize);
        int copied = 0;
        while (copied < _transferSize) {
            int chunkIndex = copied >> _chunkShift;
            int chunkOffset = copied & (_chunkSize - 1);
            int count = Math.Min(_transferSize - copied, _chunkSize - chunkOffset);
            source.Slice(copied, count).CopyTo(_chunks[chunkIndex].AsSpan(chunkOffset, count));
            copied += count;
        }
    }

    internal void MarshalFromChunks() {
        int copied = 0;
        while (copied < _transferSize) {
            int chunkIndex = copied >> _chunkShift;
            int chunkOffset = copied & (_chunkSize - 1);
            int count = Math.Min(_transferSize - copied, _chunkSize - chunkOffset);
            Marshal.Copy(_chunks[chunkIndex], chunkOffset, IntPtr.Add(_nativeBuffer, copied), count);
            copied += count;
        }
    }

    internal void SpanFromChunks() {
        Span<byte> destination = new((void*)_nativeBuffer, _transferSize);
        int copied = 0;
        while (copied < _transferSize) {
            int chunkIndex = copied >> _chunkShift;
            int chunkOffset = copied & (_chunkSize - 1);
            int count = Math.Min(_transferSize - copied, _chunkSize - chunkOffset);
            _chunks[chunkIndex].AsSpan(chunkOffset, count).CopyTo(destination[copied..]);
            copied += count;
        }
    }

    public void Dispose() => Marshal.FreeHGlobal(_nativeBuffer);
}
