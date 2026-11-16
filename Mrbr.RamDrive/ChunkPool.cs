using System.Collections.Concurrent;
using System.Numerics;

namespace HighSpeedRamDrive;

/// <summary>Provides per-drive quotas backed by a process-wide, reusable chunk cache.</summary>
public sealed class ChunkPool {
    public const int DefaultChunkSize = 1024 * 1024;
    private const int MinimumChunkSize = 4096;
    private static readonly SharedChunkCache Cache = new();
    private readonly long _capacityBytes;
    private long _inUseBytes;

    public int ChunkSize { get; }
    public int ChunkShift { get; }
    public long CapacityBytes => _capacityBytes;
    public long InUseBytes => Interlocked.Read(ref _inUseBytes);
    public static long CachedBytes => Cache.CachedBytes;
    public static long MemoryLimitBytes => Cache.MemoryLimitBytes;

    public ChunkPool(long capacityBytes, int chunkSize = DefaultChunkSize) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        if (chunkSize < MinimumChunkSize || !int.IsPow2(chunkSize))
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be a power of two of at least 4 KiB.");

        _capacityBytes = capacityBytes;
        ChunkSize = chunkSize;
        ChunkShift = BitOperations.TrailingZeroCount((uint)chunkSize);
    }

    public bool TryRentMany(int count, List<byte[]> destination) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentNullException.ThrowIfNull(destination);
        if (count == 0)
            return true;

        long byteCount = (long)count * ChunkSize;
        if (!TryReserveInUse(byteCount))
            return false;

        int oldCount = destination.Count;
        try {
            destination.EnsureCapacity(checked(oldCount + count));
            for (int i = 0; i < count; i++) {
                if (!Cache.TryRent(ChunkSize, out var chunk)) {
                    RollBackRent(destination, oldCount, byteCount);
                    return false;
                }
                destination.Add(chunk);
            }
            return true;
        } catch (OutOfMemoryException) {
            RollBackRent(destination, oldCount, byteCount);
            return false;
        } catch {
            RollBackRent(destination, oldCount, byteCount);
            throw;
        }
    }

    public void Return(byte[] chunk) {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length != ChunkSize)
            throw new ArgumentException("The chunk was allocated for a different drive profile.", nameof(chunk));

        Interlocked.Add(ref _inUseBytes, -ChunkSize);
        Cache.Return(chunk);
    }

    private bool TryReserveInUse(long bytes) {
        while (true) {
            long current = Interlocked.Read(ref _inUseBytes);
            if (bytes > _capacityBytes - current)
                return false;
            if (Interlocked.CompareExchange(ref _inUseBytes, current + bytes, current) == current)
                return true;
        }
    }

    private void RollBackRent(List<byte[]> destination, int oldCount, long byteCount) {
        for (int i = destination.Count - 1; i >= oldCount; i--) {
            Cache.Return(destination[i]);
            destination.RemoveAt(i);
        }
        Interlocked.Add(ref _inUseBytes, -byteCount);
    }

    private sealed class SharedChunkCache {
        private readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> _chunksBySize = new();
        private readonly long _memoryLimitBytes = CalculateMemoryLimit();
        private long _residentBytes;
        private long _cachedBytes;

        internal long ResidentBytes => Interlocked.Read(ref _residentBytes);
        internal long CachedBytes => Interlocked.Read(ref _cachedBytes);
        internal long MemoryLimitBytes => _memoryLimitBytes;

        internal bool TryRent(int chunkSize, out byte[] chunk) {
            var pool = _chunksBySize.GetOrAdd(chunkSize, static _ => new ConcurrentBag<byte[]>());
            if (pool.TryTake(out chunk!)) {
                Interlocked.Add(ref _cachedBytes, -chunkSize);
                chunk.AsSpan().Clear();
                return true;
            }

            if (!TryReserveResidentBytes(chunkSize) && !TryTrimAndReserveResidentBytes(chunkSize)) {
                chunk = null!;
                return false;
            }

            try {
                chunk = GC.AllocateUninitializedArray<byte>(chunkSize);
                chunk.AsSpan().Clear();
                return true;
            } catch (OutOfMemoryException) {
                Interlocked.Add(ref _residentBytes, -chunkSize);
                chunk = null!;
                return false;
            }
        }

        internal void Return(byte[] chunk) {
            _chunksBySize.GetOrAdd(chunk.Length, static _ => new ConcurrentBag<byte[]>()).Add(chunk);
            Interlocked.Add(ref _cachedBytes, chunk.Length);
        }

        private bool TryReserveResidentBytes(int bytes) {
            while (true) {
                long current = Interlocked.Read(ref _residentBytes);
                if (bytes > _memoryLimitBytes - current)
                    return false;
                if (Interlocked.CompareExchange(ref _residentBytes, current + bytes, current) == current)
                    return true;
            }
        }

        private bool TryTrimAndReserveResidentBytes(int requestedSize) {
            while (true) {
                if (TryReserveResidentBytes(requestedSize))
                    return true;

                bool freedChunk = false;
                foreach (var item in _chunksBySize) {
                    if (item.Key != requestedSize && item.Value.TryTake(out var unusedChunk)) {
                        Interlocked.Add(ref _cachedBytes, -unusedChunk.Length);
                        Interlocked.Add(ref _residentBytes, -unusedChunk.Length);
                        freedChunk = true;
                        break;
                    }
                }
                if (!freedChunk)
                    return false;
            }
        }

        private static long CalculateMemoryLimit() {
            long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (available <= 0)
                available = 8L * 1024 * 1024 * 1024;
            return Math.Max(MinimumChunkSize, available * 3 / 4);
        }
    }
}
