# Versioned JSON profile import/export

**Status:** Pending · P1  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Persist named drive workload profiles as validated, versioned JSON so applications can share configuration without hard-coding every capacity and chunk size.

## Proposed usage

```csharp
RamDriveProfile profile = await RamDriveProfileStore.LoadAsync(profilePath, cancellationToken);
RamDriveOptions options = RamDriveOptions.FromProfile(profile);
await RamDriveProfileStore.SaveAsync(profile, profilePath, cancellationToken);
```

## Requirements

- Include a schema version and reject unsupported future versions with a useful error.
- Validate name, mount point, capacity, chunk size, labels, and relative startup directories before returning a profile.
- Avoid persisting callbacks, passwords, security descriptors, or machine-global settings in the profile format.
- Use atomic file replacement so an interrupted save does not destroy the previous profile.
- Keep user-specific profile location and UI choices in the referencing application.
- Provide migration guidance when the profile schema changes.

## Acceptance

Round-trip, malformed JSON, unsupported-version, invalid path, and atomic-save failure cases are covered. Profile data maps to the existing `RamDriveOptions` and workload presets.
