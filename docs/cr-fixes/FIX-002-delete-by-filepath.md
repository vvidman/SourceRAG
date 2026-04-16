# FIX-002 — Deleted File Chunk Removal via Qdrant Payload Filter

## Problem

`IndexRepositoryHandler.DeleteFileChunksAsync` re-fetches file content and blame from the VCS for a **deleted** file, then re-chunks it to recompute point IDs, then deletes those points from Qdrant.

This approach has two failure modes:
1. The deleted file no longer exists in HEAD — `GetFileContentAsync` at the old revision might work, but `GetBlameAsync` is unreliable for deleted paths in both Git and SVN.
2. If the chunking output changes between the time of indexing and the time of deletion (e.g. chunker logic was updated), the recomputed point IDs will not match the stored ones — orphaned points remain in Qdrant forever.

**Correct approach:** Query Qdrant directly for all points where `file_path == deletedFilePath` and delete them. No VCS access needed. The proof is already in the vector store.

---

## Changes Required

### 1. Domain — `IVectorStore.cs`

Add one method:

```csharp
/// <summary>
/// Deletes all points whose payload contains file_path == <paramref name="filePath"/>.
/// Used to clean up chunks when a file is deleted from the repository.
/// </summary>
Task DeleteByFilePathAsync(string filePath, CancellationToken ct);
```

Full updated interface:
```csharp
public interface IVectorStore
{
    Task EnsureCollectionAsync(int dimensions, CancellationToken ct);
    Task UpsertAsync(Guid pointId, float[] vector, ChunkMetadata metadata, CancellationToken ct);
    Task<IReadOnlyList<ScoredChunk>> SearchAsync(float[] queryVector, int topK, CancellationToken ct);
    Task DeleteAsync(Guid pointId, CancellationToken ct);
    Task DeleteByFilePathAsync(string filePath, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}
```

---

### 2. Infrastructure — `QdrantVectorStore.cs`

Implement `DeleteByFilePathAsync` using Qdrant's filter-based delete:

```csharp
public async Task DeleteByFilePathAsync(string filePath, CancellationToken ct)
{
    var filter = new Filter
    {
        Must =
        {
            new Condition
            {
                Field = new FieldCondition
                {
                    Key   = "file_path",
                    Match = new Match { Text = filePath }
                }
            }
        }
    };

    await _client.DeleteAsync(_options.CollectionName, filter, cancellationToken: ct);
}
```

> Note: The Qdrant.Client filter API may vary slightly depending on the SDK version. Verify the exact `Filter` + `FieldCondition` types from the installed `Qdrant.Client` package. The logical structure (match on `file_path` field) is correct regardless.

---

### 3. Application — `IndexRepositoryHandler.cs`

**Remove** the entire `DeleteFileChunksAsync` private method.

**Replace** the deletion call in `Handle`:

```csharp
// BEFORE — in the foreach over scope.ChangedFiles:
if (file.ChangeType == ChangeType.Deleted)
    await DeleteFileChunksAsync(repoPath, file.Path, scope.FromRevision, branch, context, ct);

// AFTER:
if (file.ChangeType == ChangeType.Deleted)
{
    await _vectorStore.DeleteByFilePathAsync(file.Path, ct);
    context.DeletedChunkCount++;
}
```

The `DeleteFileChunksAsync` method — approximately 25 lines — is now entirely removed. No VCS access for deleted files.

---

### 4. Tests — Update `IVectorStore` mocks

Any test that mocks `IVectorStore` via NSubstitute will automatically include the new method (NSubstitute mocks all interface members). No test changes required unless a test explicitly verifies `DeleteAsync` call count for the deleted-file scenario.

**Update** `Handle_DeletedFile_CallsVectorStoreDelete` in `IndexRepositoryHandlerTests.cs`:

```csharp
[Fact]
public async Task Handle_DeletedFile_CallsVectorStoreDelete()
{
    var deletedFile = new ChangedFile("src/Foo.cs", ChangeType.Deleted);

    _indexStateStore.GetLastIndexedRevisionAsync(RepoPath, Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<string?>("rev-001"));
    _reindexStrategy.DetermineChangedFilesAsync(RepoPath, "rev-001", Arg.Any<CancellationToken>())
        .Returns(Task.FromResult(new ReindexScope(new[] { deletedFile }, "rev-001", "rev-002")));
    _vectorStore.DeleteByFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(Task.CompletedTask);

    await _handler.Handle(new IndexRepositoryCommand(FullReindex: false), CancellationToken.None);

    // Verify filter-based delete — no VCS access needed
    await _vectorStore.Received(1)
        .DeleteByFilePathAsync("src/Foo.cs", Arg.Any<CancellationToken>());

    // Verify NO VCS calls for the deleted file
    await _vcsProvider.DidNotReceive()
        .GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
}
```

**Remove** the now-unnecessary mock setup lines from that test:
- `_vcsProvider.GetFileContentAsync(...)` stub
- `_vcsProvider.GetBlameAsync(...)` stub
- `_chunker.CanHandle(...)` for the deleted file
- `_chunker.Chunk(...)` for the deleted file
- `_vectorStore.DeleteAsync(...)` stub

---

## Acceptance Criteria

- [ ] `IVectorStore` has `DeleteByFilePathAsync`
- [ ] `QdrantVectorStore` implements it with a payload filter
- [ ] `IndexRepositoryHandler` no longer calls `GetFileContentAsync` or `GetBlameAsync` for deleted files
- [ ] `Handle_DeletedFile_CallsVectorStoreDelete` passes and asserts no VCS calls
- [ ] Solution builds without errors
