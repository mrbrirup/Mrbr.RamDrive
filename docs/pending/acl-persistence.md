# ACL persistence

**Status:** Pending · P2 · filesystem prerequisite  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Add optional snapshot persistence for file and directory security descriptors after the filesystem has a defined ACL model.

## Proposed usage

```csharp
await drive.SaveSnapshotAsync(path, new RamDriveSnapshotOptions {
    Metadata = RamDriveSnapshotMetadata.FileAttributes |
               RamDriveSnapshotMetadata.Timestamps |
               RamDriveSnapshotMetadata.AccessControl
}, cancellationToken);
```

## Requirements

- Implement and verify WinFsp security query/set callbacks before serializing ACL data.
- Define owner/group/DACL/SACL handling, inheritance, privilege requirements, and unsupported ACE behavior.
- Store descriptors in a documented, versioned representation and validate their length/content on restore.
- Decide how identities unavailable on the restore machine are handled; never silently broaden access.
- Keep ACL capture optional and fail clearly when requested but unsupported.

## Acceptance

Tests verify ACL round-trip for supported descriptors, inaccessible privilege cases, malformed descriptors, and unavailable principals. The default metadata set remains portable attributes/timestamps.
