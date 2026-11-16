namespace HighSpeedRamDrive;

internal sealed class RamDriveTempQuota(long maxBytes, int maxEntries) {
    private long _usedBytes;
    private int _entryCount;

    internal long MaxBytes { get; } = maxBytes;
    internal int MaxEntries { get; } = maxEntries;
    internal long UsedBytes => Interlocked.Read(ref _usedBytes);
    internal int EntryCount => Volatile.Read(ref _entryCount);

    internal bool TryAddBytes(long bytes) {
        if (bytes <= 0)
            return true;

        while (true) {
            long current = Interlocked.Read(ref _usedBytes);
            if (bytes > MaxBytes - current)
                return false;
            if (Interlocked.CompareExchange(ref _usedBytes, current + bytes, current) == current)
                return true;
        }
    }

    internal void ReleaseBytes(long bytes) {
        if (bytes > 0)
            Interlocked.Add(ref _usedBytes, -bytes);
    }

    internal bool TryAddEntry() {
        while (true) {
            int current = Volatile.Read(ref _entryCount);
            if (current >= MaxEntries)
                return false;
            if (Interlocked.CompareExchange(ref _entryCount, current + 1, current) == current)
                return true;
        }
    }

    internal void ReleaseEntry() => Interlocked.Decrement(ref _entryCount);
}
