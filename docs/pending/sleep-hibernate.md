# Sleep and hibernate persistence hooks

**Status:** Pending · P3  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Let a host application participate in pre-sleep/pre-hibernate persistence for drives configured to save before power transitions.

## Proposed usage

```csharp
await using var powerHook = drive.RegisterPowerTransitionHandler(async transition =>
    await drive.SaveSnapshotAsync(path, options, transition.CancellationToken));
```

The application, not the library, owns notification UI and user decisions.

## Requirements

- Define supported Windows power notifications, time budgets, and behavior when the OS does not wait for save completion.
- Reuse caller-provided save-failure policy; never silently discard or block indefinitely.
- Avoid taking dependencies on a UI framework or requiring an interactive session.
- Support unregister/disposal and handle missed notifications.
- Do not claim that volatile RAM survives sleep/hibernate unless the selected Windows/device model guarantees it.
- Benchmark and document save duration for large drives.

## Acceptance

Integration tests or a controllable power-event harness prove registration, cancellation, failure reporting, and cleanup. The feature remains opt-in.
