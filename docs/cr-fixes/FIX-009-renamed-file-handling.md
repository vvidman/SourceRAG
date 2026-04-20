# FIX-009 — Renamed File Handling: Clean Up Old Path Chunks

## Priority
🔴 P2 — Data correctness bug. Renamed files leave orphaned vectors for the old path in Qdrant, and queries against old names return stale results indefinitely.

## Problem

When a file is renamed (`ChangeType.Renamed`), `IndexRepositoryHandler` indexes the new path but never removes the old path's chunks:

```csharp
// Current — Renamed treated identically to Modified:
if (file.ChangeType == ChangeType.Deleted)
{
    await _vectorStore.DeleteByFilePathAsync(file.Path, ct);
    context.DeletedChunkCount++;
}
else
    await ProcessFileAsync(repoPath, file.Path, scope.ToRevision, branch, context, ct);
```

Result:
- Old path (`src/Core/Foo.cs`) → chunks remain in Qdrant forever
- New path (`src/Domain/Foo.cs`) → new chunks added correctly
- Queries for "Foo" now return **duplicate results** — old and new path

The fix requires two components:
1. `ChangedFile` must carry `OldPath` for rename operations
2. Both VCS providers must populate `OldPath` from their diff/log data
3. The handler must delete old path chunks before indexing the new path

---

## Changes Required

### 1. Domain — `Entities/ChangedFile.cs`

Add optional `OldPath`:

```csharp
// BEFORE:
public sealed record ChangedFile(string Path, ChangeType ChangeType);

// AFTER:
/// <param name="Path">The current (new) path of the file.</param>
/// <param name="ChangeType">The type of change.</param>
/// <param name="OldPath">
///   The previous path, populated only when <paramref name="ChangeType"/> is
///   <see cref="ChangeType.Renamed"/>. Null for all other change types.
/// </param>
public sealed record ChangedFile(
    string Path,
    ChangeType ChangeType,
    string? OldPath = null);
```

---

### 2. Infrastructure — `Vcs/Git/GitVcsProvider.cs`

Populate `OldPath` from `TreeChanges` rename entries:

```csharp
// BEFORE — in GetChangedFilesSinceAsync:
var changes = diff
    .Select(e => new ChangedFile(e.Path, MapChangeKind(e.Status)))
    .ToList();

// AFTER:
var changes = diff
    .Select(e => new ChangedFile(
        e.Path,
        MapChangeKind(e.Status),
        OldPath: e.Status == ChangeKind.Renamed ? e.OldPath : null))
    .ToList();
```

`TreeChanges` in LibGit2Sharp exposes `OldPath` on renamed entries — this is a direct read, no additional API calls needed.

---

### 3. Infrastructure — `Vcs/Svn/SvnVcsProvider.cs`

Populate `OldPath` from `SvnChangeItem.CopyFromPath` for `Replace` (rename) actions.

In `GetChangedFilesSinceAsync`, the existing dictionary merge (`changedPaths[relativePath] = changeType`) loses the `OldPath`. Replace with a list-based accumulation:

```csharp
// Replace Dictionary<string, ChangeType> with List<ChangedFile>:
var changedFiles = new List<ChangedFile>();
// Track seen paths to handle last-writer-wins for multi-revision changes:
var seenPaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

client.Log(repoPath, logArgs, (_, args) =>
{
    if (args.ChangedPaths is null) return;
    foreach (var item in args.ChangedPaths)
    {
        var relativePath = NormaliseToRelative(item.Path, trunkPrefix);
        if (string.IsNullOrWhiteSpace(relativePath)) continue;

        var changeType = MapSvnAction(item.Action);

        // Compute old path for renames (SVN Replace with CopyFromPath)
        string? oldPath = null;
        if (changeType == Domain.Enums.ChangeType.Renamed &&
            item.CopyFromPath is not null)
        {
            oldPath = NormaliseToRelative(item.CopyFromPath, trunkPrefix);
        }

        var entry = new ChangedFile(relativePath, changeType, oldPath);

        if (seenPaths.TryGetValue(relativePath, out var existingIdx))
            changedFiles[existingIdx] = entry; // last writer wins
        else
        {
            seenPaths[relativePath] = changedFiles.Count;
            changedFiles.Add(entry);
        }
    }
});

return Task.FromResult<IReadOnlyList<ChangedFile>>(changedFiles);
```

Extract path normalisation to a helper:

```csharp
private static string NormaliseToRelative(string absolutePath, string trunkPrefix)
{
    var relative = absolutePath;
    if (!string.IsNullOrEmpty(trunkPrefix) &&
        relative.StartsWith(trunkPrefix, StringComparison.OrdinalIgnoreCase))
    {
        relative = relative[trunkPrefix.Length..].TrimStart('/');
    }
    return relative;
}
```

---

### 4. Application — `Indexing/IndexRepositoryHandler.cs`

Handle `Renamed` explicitly:

```csharp
// BEFORE:
foreach (var file in scope.ChangedFiles)
{
    if (file.ChangeType == ChangeType.Deleted)
    {
        await _vectorStore.DeleteByFilePathAsync(file.Path, ct);
        context.DeletedChunkCount++;
    }
    else
        await ProcessFileAsync(repoPath, file.Path, scope.ToRevision, branch, context, ct);
}

// AFTER:
foreach (var file in scope.ChangedFiles)
{
    switch (file.ChangeType)
    {
        case ChangeType.Deleted:
            await _vectorStore.DeleteByFilePathAsync(file.Path, ct);
            context.DeletedChunkCount++;
            break;

        case ChangeType.Renamed:
            // Delete old path chunks, then index under the new path
            if (file.OldPath is not null)
            {
                await _vectorStore.DeleteByFilePathAsync(file.OldPath, ct);
                context.DeletedChunkCount++;
            }
            await ProcessFileAsync(repoPath, file.Path, scope.ToRevision, branch, context, ct);
            break;

        default:
            // Added or Modified
            await ProcessFileAsync(repoPath, file.Path, scope.ToRevision, branch, context, ct);
            break;
    }
}
```

---

### 5. Tests

#### `tests/SourceRAG.Application.Tests/Indexing/IndexRepositoryHandlerTests.cs`

Add:

```csharp
[Fact]
public async Task Handle_RenamedFile_DeletesOldPathAndIndexesNewPath()
{
    var renamedFile = new ChangedFile(
        Path:       "src/Domain/Foo.cs",
        ChangeType: ChangeType.Renamed,
        OldPath:    "src/Core/Foo.cs");

    var blame = new FileBlameInfo
    {
        FilePath      = "src/Domain/Foo.cs",
        Revision      = "rev-002",
        Author        = "dev",
        CommitMessage = "move Foo to Domain",
        Timestamp     = DateTimeOffset.UtcNow
    };

    var chunk = new CodeChunk("class Foo {}", new ChunkMetadata
    {
        FilePath      = "src/Domain/Foo.cs",
        Revision      = "rev-002",
        Author        = "dev",
        CommitMessage = "move Foo to Domain",
        Timestamp     = DateTimeOffset.UtcNow,
        Branch        = "main"
    });

    _indexStateStore.GetLastIndexedRevisionAsync(RepoPath, Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<string?>("rev-001"));
    _reindexStrategy.DetermineChangedFilesAsync(RepoPath, "rev-001", Arg.Any<CancellationToken>())
        .Returns(Task.FromResult(new ReindexScope(
            new[] { renamedFile }, "rev-001", "rev-002")));
    _vectorStore.DeleteByFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(Task.CompletedTask);
    _vcsProvider.GetFileContentAsync(RepoPath, "src/Domain/Foo.cs", "rev-002", Arg.Any<CancellationToken>())
        .Returns(Task.FromResult("class Foo {}"));
    _vcsProvider.GetBlameAsync(RepoPath, "src/Domain/Foo.cs", "rev-002", Arg.Any<CancellationToken>())
        .Returns(Task.FromResult(blame));
    _chunker.CanHandle("src/Domain/Foo.cs").Returns(true);
    _chunker.Chunk(Arg.Any<string>(), Arg.Any<ChunkMetadata>())
        .Returns(new List<CodeChunk> { chunk });
    _vectorStore.UpsertAsync(Arg.Any<Guid>(), Arg.Any<float[]>(), Arg.Any<ChunkMetadata>(), Arg.Any<CancellationToken>())
        .Returns(Task.CompletedTask);
    _embeddingProvider.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult(new float[] { 0.1f }));

    await _handler.Handle(new IndexRepositoryCommand(FullReindex: false), CancellationToken.None);

    // Old path deleted
    await _vectorStore.Received(1)
        .DeleteByFilePathAsync("src/Core/Foo.cs", Arg.Any<CancellationToken>());

    // New path indexed
    await _vectorStore.Received(1)
        .UpsertAsync(Arg.Any<Guid>(), Arg.Any<float[]>(), Arg.Any<ChunkMetadata>(), Arg.Any<CancellationToken>());

    // Old path NOT indexed
    await _vcsProvider.DidNotReceive()
        .GetFileContentAsync(RepoPath, "src/Core/Foo.cs", Arg.Any<string>(), Arg.Any<CancellationToken>());
}
```

#### `tests/SourceRAG.Infrastructure.Tests/Vcs/Git/GitVcsProviderTests.cs`

Add:

```csharp
[Fact]
public async Task GetChangedFilesSince_RenamedFile_IncludesOldPath()
{
    // Rename hello.txt to world.txt in a second commit
    using var repo = new Repository(_repoPath);
    File.Move(
        Path.Combine(_repoPath, "hello.txt"),
        Path.Combine(_repoPath, "world.txt"));
    Commands.Stage(repo, "*");
    repo.Commit("Rename hello.txt to world.txt", TestSig, TestSig);

    var changed = await _sut.GetChangedFilesSinceAsync(
        _repoPath, _initialSha, CancellationToken.None);

    var renamed = changed.FirstOrDefault(f => f.ChangeType == ChangeType.Renamed);
    Assert.NotNull(renamed);
    Assert.Equal("world.txt", renamed!.Path);
    Assert.Equal("hello.txt", renamed.OldPath);
}
```

---

## Edge Cases

**SVN `OldPath` null:** If `CopyFromPath` is null on a `Replace` action (can happen with some SVN operations), the handler skips the old-path deletion and just indexes the new path. This is safe — worst case is orphaned old-path chunks remain, which is better than an exception.

**Git rename detection threshold:** LibGit2Sharp's `Diff.Compare<TreeChanges>` uses Git's default rename similarity threshold (50%). If the threshold is not met, Git reports a `Deleted + Added` pair instead of `Renamed`. The handler correctly handles `Deleted` separately — no orphan issue in that case.

---

## Acceptance Criteria

- [ ] `ChangedFile` record has `string? OldPath = null`
- [ ] `GitVcsProvider` populates `OldPath` for `ChangeKind.Renamed` entries
- [ ] `SvnVcsProvider` populates `OldPath` from `CopyFromPath` for `Replace` actions
- [ ] `IndexRepositoryHandler` deletes old path and indexes new path for `Renamed`
- [ ] `Handle_RenamedFile_DeletesOldPathAndIndexesNewPath` test passes
- [ ] `GetChangedFilesSince_RenamedFile_IncludesOldPath` test passes
- [ ] Existing tests continue to pass (no `ChangedFile` constructor breaking change — default param)
