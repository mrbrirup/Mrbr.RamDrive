# Child-process TEMP/TMP environment lease

**Status:** Pending · P1  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Offer a convenient way for an application to launch a child process with `TEMP` and `TMP` pointed at a `RamDriveTempWorkspace`. The lease should scope these values to that child process and avoid changing machine or user environment variables.

## Proposed usage

```csharp
await using var workspace = drive.CreateTempWorkspace();
var start = new ProcessStartInfo(toolPath);
RamDriveProcessTempEnvironment.Apply(start, workspace);
using var process = Process.Start(start)!;
await process.WaitForExitAsync(cancellationToken);
```

The API may instead return a configured `ProcessStartInfo`; names are provisional.

## Requirements

- Set both `TEMP` and `TMP` in `ProcessStartInfo.Environment` to the workspace path.
- Do not call `Environment.SetEnvironmentVariable` for process-wide, user-wide, or machine-wide scope.
- Validate that the workspace is mounted, unexpired, and not disposed before launch.
- Document that descendants inherit environment values only when the caller launches them from the configured process.
- Define cleanup guidance for child processes that outlive the workspace; disposing the workspace must surface sharing or deletion failures through the existing cleanup path.
- Preserve caller-supplied environment values outside `TEMP`/`TMP`.

## Acceptance

A child process started with the helper reports the workspace path for both variables. A child started without it sees its normal inherited values. The helper performs no UI and does not change global environment state.
