# FIX-007 — QdrantVectorStore: Match.Text → Match.Keyword in DeleteByFilePathAsync

## Priority
🔴 P1 — Silent data integrity bug. No exception is thrown, but deleted file chunks remain in Qdrant permanently.

## Problem

`DeleteByFilePathAsync` uses `Match { Text = filePath }` which triggers Qdrant's **full-text search** on the `file_path` payload field. Full-text search tokenises the value and matches against terms, not exact strings. A path like `src/Core/ImageProcessor.cs` would be tokenised into `src`, `Core`, `ImageProcessor`, `cs` — no point would match the full path exactly.

The correct Qdrant filter for exact value matching on a keyword field is `Match { Keyword = filePath }`.

The consequence: calling `DeleteByFilePathAsync` currently deletes **zero points** regardless of the file path. Incremental reindex accumulates orphaned vectors for every deleted file.

---

## Change

**File:** `src/SourceRAG.Infrastructure/VectorStore/QdrantVectorStore.cs`

```csharp
// BEFORE:
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
                    Match = new Match { Text = filePath }  // ← WRONG: full-text search
                }
            }
        }
    };

    await _client.DeleteAsync(_options.CollectionName, filter, cancellationToken: ct);
}

// AFTER:
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
                    Match = new Match { Keyword = filePath }  // ← CORRECT: exact value match
                }
            }
        }
    };

    await _client.DeleteAsync(_options.CollectionName, filter, cancellationToken: ct);
}
```

> **Qdrant.Client API note:** `Match.Keyword` is available in `Qdrant.Client` 1.x for exact string matching on payload fields. If the pinned version (1.17.0) exposes a different property name, check the `Match` protobuf definition. The gRPC `Match` message has a `string keyword` field for exact matches — always prefer this over `string text` (full-text) for path/identifier fields.

---

## Test

Add to `tests/SourceRAG.Infrastructure.Tests/VectorStore/QdrantVectorStoreTests.cs` (new file, integration test — requires running Qdrant):

```csharp
[Fact(Skip = "Integration — requires Qdrant on localhost:6333")]
public async Task DeleteByFilePathAsync_RemovesOnlyMatchingPoints()
{
    // Arrange: upsert two points with different file paths
    await _sut.EnsureCollectionAsync(dimensions: 3, CancellationToken.None);

    var metadata1 = BuildMetadata("src/Foo.cs");
    var metadata2 = BuildMetadata("src/Bar.cs");
    var vector    = new float[] { 0.1f, 0.2f, 0.3f };

    var id1 = Guid.NewGuid();
    var id2 = Guid.NewGuid();
    await _sut.UpsertAsync(id1, vector, metadata1, CancellationToken.None);
    await _sut.UpsertAsync(id2, vector, metadata2, CancellationToken.None);

    // Act
    await _sut.DeleteByFilePathAsync("src/Foo.cs", CancellationToken.None);

    // Assert: only Foo.cs deleted; Bar.cs remains
    var count = await _sut.CountAsync(CancellationToken.None);
    Assert.Equal(1, count);
}
```

---

## Acceptance Criteria

- [ ] `DeleteByFilePathAsync` uses `Match { Keyword = filePath }`
- [ ] Running the method against a live Qdrant instance removes all points with that exact `file_path`
- [ ] Points with different `file_path` values are unaffected
- [ ] Solution builds without errors
