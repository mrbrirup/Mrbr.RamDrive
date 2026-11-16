using Fsp;

namespace HighSpeedRamDrive;

/// <summary>Owns a mounted WinFsp RAM drive and its temporary workspaces.</summary>
public sealed class RamDriveSession : IAsyncDisposable, IDisposable {
    private readonly FileSystemHost _host;
    private readonly HashSet<RamDriveTempWorkspace> _workspaces = [];
    private readonly object _workspaceLock = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private int _disposed;
    private bool _snapshotSavedOnUnmount;

    public RamDriveOptions Options { get; }
    public RamFileSystem FileSystem { get; }
    public string MountPoint { get; }
    public string RootPath { get; }
    internal string TempNamespacePath { get; }

    private RamDriveSession(RamDriveOptions options, RamFileSystem fileSystem, FileSystemHost host, string mountPoint) {
        Options = options;
        FileSystem = fileSystem;
        _host = host;
        MountPoint = mountPoint;
        RootPath = mountPoint + "\\";
        TempNamespacePath = Path.Combine(RootPath, $".mrbr-temp-{Guid.NewGuid():N}");
    }

    /// <summary>Mounts a drive. The default configuration is a volatile 2 GiB application temp drive.</summary>
    public static RamDriveSession Mount(RamDriveOptions? options = null) => Mount(options, DriveLetterSelection.LastAvailable);

    internal static RamDriveSession Mount(RamDriveOptions? options, DriveLetterSelection driveLetterSelection) {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WinFsp RAM drives can only be mounted on Windows.");
        if (!Enum.IsDefined(driveLetterSelection))
            throw new ArgumentOutOfRangeException(nameof(driveLetterSelection));

        options = (options ?? RamDriveOptions.ApplicationTemp()).Validate();
        var fileSystem = new RamFileSystem(options.CapacityBytes, options.ChunkSize, options.VolumeLabel);
        IEnumerable<string> candidates = options.MountPoint is null
            ? GetAvailableMountPoints(driveLetterSelection)
            : [options.MountPoint];

        int lastStatus = 0;
        bool triedMountPoint = false;
        foreach (string mountPoint in candidates) {
            triedMountPoint = true;
            var host = new FileSystemHost(fileSystem);
            int status = host.Mount(mountPoint);
            if (status >= 0)
                return new RamDriveSession(options with { MountPoint = mountPoint }, fileSystem, host, mountPoint);

            lastStatus = status;
            host.Dispose();
            if (options.MountPoint is not null)
                break;
        }

        string error;
        if (options.MountPoint is not null)
            error = $"WinFsp failed to mount {options.MountPoint}. Status: 0x{lastStatus:X8}.";
        else if (triedMountPoint)
            error = $"No available drive letter could be mounted. Last WinFsp status: 0x{lastStatus:X8}.";
        else
            error = "No free drive letters are available.";
        throw new InvalidOperationException(error);
    }

    /// <summary>Creates the default volatile app workspace drive, optionally with a different capacity.</summary>
    public static RamDriveSession MountApplicationTemp(long capacityBytes = 2L * 1024 * 1024 * 1024) =>
        Mount(RamDriveOptions.ApplicationTemp() with { CapacityBytes = capacityBytes });

    public RamDriveStatus GetStatus() {
        long inUse = FileSystem.ChunkPoolInUseBytes;
        return new RamDriveStatus(Options.Name, MountPoint, Volatile.Read(ref _disposed) == 0,
            Options.CapacityBytes, inUse, Math.Max(0, Options.CapacityBytes - inUse),
            ChunkPool.CachedBytes, Options.ChunkSize);
    }

    public Task<RamDriveSnapshotResult> SaveSnapshotAsync(string destinationPath,
        RamDriveSnapshotOptions? options = null, CancellationToken cancellationToken = default) =>
        RamDriveSnapshot.SaveAsync(this, destinationPath, options, cancellationToken);

    public static Task<RamDriveSession> LoadSnapshotAsync(string snapshotPath,
        RamDriveOptions? driveOptions = null, RamDriveSnapshotLoadOptions? loadOptions = null,
        CancellationToken cancellationToken = default) =>
        RamDriveSnapshot.LoadAsync(snapshotPath, driveOptions, loadOptions, cancellationToken);

    /// <summary>Creates a quota-limited temporary directory that is removed on disposal or expiry.</summary>
    public RamDriveTempWorkspace CreateTempWorkspace(TempWorkspaceOptions? options = null) {
        _lifecycleGate.Wait();
        try {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            options = (options ?? new TempWorkspaceOptions()).Validate();

            Directory.CreateDirectory(TempNamespacePath);
            string path = Path.Combine(TempNamespacePath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            string virtualPath = "\\" + Path.GetRelativePath(RootPath, path).Replace(Path.DirectorySeparatorChar, '\\');
            bool scopeRegistered = false;
            try {
                var quota = FileSystem.RegisterTempScope(virtualPath, options.MaxBytes, options.MaxEntries);
                scopeRegistered = true;
                var workspace = new RamDriveTempWorkspace(this, path, virtualPath, options, quota);
                lock (_workspaceLock)
                    _workspaces.Add(workspace);
                return workspace;
            } catch {
                if (scopeRegistered)
                    FileSystem.UnregisterTempScope(virtualPath);
                Directory.Delete(path, recursive: true);
                throw;
            }
        } finally {
            _lifecycleGate.Release();
        }
    }

    internal void OnWorkspaceRemoved(RamDriveTempWorkspace workspace) {
        lock (_workspaceLock)
            _workspaces.Remove(workspace);
    }

    internal async ValueTask AbortMountAsync() {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try {
            if (Volatile.Read(ref _disposed) == 0) {
                _host.Dispose();
                Interlocked.Exchange(ref _disposed, 1);
            }
        } finally {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Saves when configured, then unmounts. A failure handler decides retry, discard, or keep-mounted.</summary>
    public async Task<RamDriveUnmountResult> UnmountAsync(
        IRamDriveSaveFailureHandler? failureHandler = null,
        CancellationToken cancellationToken = default) {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (Volatile.Read(ref _disposed) != 0)
                return new RamDriveUnmountResult(true, _snapshotSavedOnUnmount, false, 0, null);

            bool snapshotSaved = false;
            Exception? lastSaveError = null;
            int attempts = 0;
            bool shouldUnmount = true;
            if (!string.IsNullOrWhiteSpace(Options.SaveSnapshotOnUnmountPath)) {
                while (!snapshotSaved) {
                    cancellationToken.ThrowIfCancellationRequested();
                    attempts++;
                    try {
                        await RamDriveSnapshot.SaveAsync(this, Options.SaveSnapshotOnUnmountPath,
                            Options.SnapshotOptions, cancellationToken).ConfigureAwait(false);
                        snapshotSaved = true;
                        lastSaveError = null;
                    } catch (Exception ex) when (ex is not OperationCanceledException) {
                        lastSaveError = ex;
                        if (failureHandler is null)
                            return new RamDriveUnmountResult(false, false, false, attempts, lastSaveError);

                        var action = await failureHandler.OnSaveFailedAsync(
                            new RamDriveSaveFailureContext(Options.SaveSnapshotOnUnmountPath, attempts, ex),
                            cancellationToken).ConfigureAwait(false);
                        switch (action) {
                            case RamDriveSaveFailureAction.Retry:
                                continue;
                            case RamDriveSaveFailureAction.UnmountWithoutSaving:
                                shouldUnmount = true;
                                break;
                            case RamDriveSaveFailureAction.KeepMounted:
                                shouldUnmount = false;
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(action));
                        }
                        break;
                    }
                }
            }

            if (!shouldUnmount)
                return new RamDriveUnmountResult(false, false, true, attempts, lastSaveError);

            RamDriveTempWorkspace[] workspaces;
            lock (_workspaceLock)
                workspaces = [.. _workspaces];

            List<Exception>? cleanupErrors = null;
            foreach (var workspace in workspaces) {
                try {
                    await workspace.DisposeAsync().ConfigureAwait(false);
                } catch (Exception ex) {
                    (cleanupErrors ??= []).Add(ex);
                }
            }

            _host.Dispose();
            _snapshotSavedOnUnmount = snapshotSaved;
            Interlocked.Exchange(ref _disposed, 1);
            if (cleanupErrors is not null)
                throw new AggregateException("One or more temporary workspaces could not be removed before unmount.", cleanupErrors);

            return new RamDriveUnmountResult(true, snapshotSaved, false, attempts, lastSaveError);
        } finally {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync() {
        var result = await UnmountAsync(Options.SaveFailureHandler).ConfigureAwait(false);
        if (!result.IsUnmounted)
            throw new RamDriveUnmountException(result);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal static IEnumerable<string> GetAvailableMountPoints(DriveLetterSelection selection = DriveLetterSelection.LastAvailable) {
        var occupied = DriveInfo.GetDrives().Select(drive => drive.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string letters = selection switch {
            DriveLetterSelection.FirstAvailable => "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
            DriveLetterSelection.LastAvailable => "ZYXWVUTSRQPONMLKJIHGFEDCBA",
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };
        foreach (char letter in letters) {
            string mountPoint = $"{letter}:";
            if (!occupied.Contains(mountPoint + "\\"))
                yield return mountPoint;
        }
    }
}
