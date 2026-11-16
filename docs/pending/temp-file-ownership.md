# Caller-owned temp-file lease mode

**Status:** Pending · P2  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Keep the current `RamDriveTempFile` behavior, which deletes its file on disposal, and add an explicit ownership choice for temporary files that should remain in the workspace after the stream closes.

## Proposed usage

```csharp
await using var file = workspace.CreateTempFile(deleteOnDispose: false);
await file.Stream.WriteAsync(data, cancellationToken);
// Stream closes here; the file remains until the workspace or caller removes it.
```

## Requirements

- Make deletion policy explicit at creation time; do not infer it from a filename or extension.
- Always close the stream on disposal. Delete only when the selected lease owns deletion.
- Preserve workspace byte/entry quota accounting regardless of lease deletion mode.
- Surface deletion failures and permit a caller retry where the file is owned by the lease.
- Document that workspace disposal remains responsible for recursive cleanup of all descendants.
- Prevent a lease from deleting a path outside its registered workspace.

## Acceptance

Tests verify both ownership modes, double disposal, open-stream disposal, workspace disposal, and deletion failure recovery.
