# Configured startup directories

**Status:** Pending · P2  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Allow a drive configuration to declare relative directories such as `temp`, `cache`, `frames`, or `logs` to create immediately after a successful mount.

## Proposed usage

```csharp
var options = RamDriveOptions.ApplicationTemp() with {
    InitialDirectories = ["temp", "cache", "jobs"]
};
await using var drive = RamDriveSession.Mount(options);
```

## Requirements

- Accept only relative paths rooted inside the drive; reject rooted paths, `..`, invalid Windows components, and duplicate normalized paths.
- Create directories after mount and before returning the session to the caller.
- Define rollback if directory creation fails: unmount the just-created host and return a clear failure.
- Keep the default application-temp configuration simple and avoid creating unnecessary folders.
- Support profile-based configurations without adding UI to the library.

## Acceptance

Configured directories exist before `Mount` returns; invalid paths fail validation before mounting; a partial setup failure leaves no hidden mounted drive.
