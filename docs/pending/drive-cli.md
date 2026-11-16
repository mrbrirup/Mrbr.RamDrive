# Console drive-management commands

**Status:** Pending · P2  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Add scriptable commands to the console test/management application for creating, listing, inspecting, saving, loading, and unmounting drives.

## Proposed usage

```powershell
ramdrive list
ramdrive status video
ramdrive mount --profile video
ramdrive save video .\video.mrd
ramdrive unmount video
```

These are command sketches only; the current library remains UI-free.

## Requirements

- Keep command parsing and human-readable output in the referencing console application, not in the library.
- Return nonzero exit codes for invalid profiles, mount failures, save failures, and unresolved unmount decisions.
- Offer machine-readable JSON output for scripts without changing library types into console-specific types.
- Never silently force-discard unsaved data; expose the existing save-failure decision flow.
- Require explicit consent for administrative/service operations if those are added later.

## Acceptance

Commands support help, list/status, profile-driven mount, snapshot save/load, and graceful unmount with clear results and exit codes.
