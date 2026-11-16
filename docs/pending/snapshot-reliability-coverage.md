# Snapshot reliability integration coverage

**Status:** Pending · P1  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Build integration coverage for `.mrd` save/load and save-before-unmount policy before expanding persistence formats.

## Proposed usage

Keep mount-independent validation in the normal test suite, and put tests that need WinFsp behind an explicit integration-test fixture:

```csharp
[Fact]
public async Task Cancelled_restore_does_not_publish_a_partial_drive()
{
    await using var fixture = await RamDriveIntegrationFixture.CreateAsync();
    using var cancellation = new CancellationTokenSource();
    cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        fixture.LoadSnapshotAsync("large.mrd", cancellation.Token));
    Assert.Empty(fixture.PublishedDriveLetters);
}
```

The fixture should inject filesystem/save failures and capture the caller-facing result, rather than showing prompts or relying on the console test app.

## Required scenarios

- Round-trip empty files, nested directories, large files, selected attributes/timestamps, and both snapshot purposes.
- Reject altered checksums, truncation, wrong versions, duplicate entries, unsafe paths, payload/entry limits, and malformed metadata.
- Cancel during capture and restore; verify the prior snapshot remains intact and no public partial mount remains.
- Inject disk-full, access-denied, writer-race, and atomic-replace failures; verify retry/discard/keep-mounted decisions are returned to the caller.
- Test chunk-rounded capacity (many small files can consume more allocated memory than payload bytes).
- Test excluded/included temp workspaces and selected metadata combinations.

## Requirements

- Tests use temporary directories and isolated free drive letters or a controlled WinFsp test fixture.
- Tests never download or install WinFsp; platform/runtime prerequisites must be detected and reported.
- Keep unit tests for manifest/path logic separate from mount-dependent integration tests.
- Do not require console UI or prompts; inject handlers and fault points.

## Acceptance

The test suite demonstrates that corrupted or cancelled restore never returns a partially populated session and that save failure never silently unmounts or discards data.
