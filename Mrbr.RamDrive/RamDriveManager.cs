namespace HighSpeedRamDrive;

/// <summary>Coordinates a set of mounted drives without owning any user interface.</summary>
public sealed class RamDriveManager : IAsyncDisposable, IDisposable {
    private readonly List<RamDriveSession> _drives;

    public IReadOnlyList<RamDriveSession> Drives => _drives;

    private RamDriveManager(List<RamDriveSession> drives) => _drives = drives;

    public static RamDriveManager Mount(IEnumerable<RamDriveOptions> configurations) {
        ArgumentNullException.ThrowIfNull(configurations);
        var options = configurations.Select(configuration =>
            (configuration ?? throw new ArgumentException("Drive configurations cannot contain null entries.", nameof(configurations))).Validate()).ToArray();
        if (options.Length == 0)
            throw new ArgumentException("At least one drive configuration is required.", nameof(configurations));
        if (options.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Length)
            throw new ArgumentException("Drive names must be unique.", nameof(configurations));
        var fixedMounts = options.Where(item => item.MountPoint is not null).Select(item => item.MountPoint!)
            .ToArray();
        if (fixedMounts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fixedMounts.Length)
            throw new ArgumentException("Configured drive letters must be unique.", nameof(configurations));

        var mounted = new List<RamDriveSession>(options.Length);
        try {
            var reservedMounts = fixedMounts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (RamDriveOptions configured in options) {
                RamDriveOptions item = configured;
                if (item.MountPoint is null) {
                    string? selected = RamDriveSession.GetAvailableMountPoints()
                        .FirstOrDefault(candidate => !reservedMounts.Contains(candidate));
                    if (selected is null)
                        throw new InvalidOperationException("No free drive letters are available for all configured RAM drives.");
                    item = item with { MountPoint = selected };
                }
                reservedMounts.Add(item.MountPoint!);
                mounted.Add(RamDriveSession.Mount(item));
            }
            return new RamDriveManager(mounted);
        } catch {
            for (int i = mounted.Count - 1; i >= 0; i--) {
                try { mounted[i].AbortMountAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            }
            throw;
        }
    }

    public IReadOnlyList<RamDriveStatus> GetStatuses() => _drives.Select(drive => drive.GetStatus()).ToArray();

    public RamDriveSession GetDrive(string name) => _drives.FirstOrDefault(drive =>
        string.Equals(drive.Options.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"No managed RAM drive is named '{name}'.");

    public async Task<IReadOnlyList<RamDriveUnmountResult>> UnmountAllAsync(
        Func<RamDriveSession, IRamDriveSaveFailureHandler?>? failureHandlerSelector = null,
        CancellationToken cancellationToken = default) {
        var results = new List<RamDriveUnmountResult>(_drives.Count);
        List<Exception>? errors = null;
        for (int i = _drives.Count - 1; i >= 0; i--) {
            cancellationToken.ThrowIfCancellationRequested();
            RamDriveSession drive = _drives[i];
            try {
                results.Add(await drive.UnmountAsync(failureHandlerSelector?.Invoke(drive), cancellationToken)
                    .ConfigureAwait(false));
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                (errors ??= []).Add(ex);
            }
        }
        if (errors is not null)
            throw new AggregateException("One or more RAM drives failed to unmount cleanly.", errors);
        return results;
    }

    public async ValueTask DisposeAsync() {
        List<Exception>? errors = null;
        for (int i = _drives.Count - 1; i >= 0; i--) {
            try { await _drives[i].DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        if (errors is not null)
            throw new AggregateException("One or more RAM drives could not be unmounted.", errors);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
