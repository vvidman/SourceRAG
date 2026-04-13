# ADR-006 — Qdrant Point ID = sha256(repoPath + filePath + symbolName + revision)

## Status
Accepted

## Date
2025-04-13

## Context

Qdrant requires a unique identifier for each stored vector point. The choice of ID strategy has direct implications for:

1. **Idempotency** — can the same chunk be indexed twice without creating duplicates?
2. **Incremental reindex** — can a changed file's chunks be updated in-place without a full collection rebuild?
3. **Deletion** — when a file is deleted from the repository, can its chunks be removed from Qdrant precisely?

Two broad approaches exist:

**Auto-generated UUID (random)** — simple, but re-indexing the same file creates duplicate points. Cleanup requires querying by metadata filter and deleting by payload match, which is slower and more complex.

**Deterministic ID derived from chunk identity** — the same logical chunk always produces the same ID. Upsert operations are naturally idempotent. Deletion of a specific file's chunks requires knowing the IDs, which can be recomputed from the file's path and revision.

A chunk's identity is fully determined by: which repository it came from, which file, which symbol within that file, and at which revision. These four components together uniquely address any chunk in any state of history.

## Decision

Qdrant point IDs are **deterministic**, computed as:

```
pointId = sha256(repoPath + "|" + filePath + "|" + symbolName + "|" + revision)
```

For plain-text chunks (where `symbolName` is null), the chunk index is substituted:

```
pointId = sha256(repoPath + "|" + filePath + "|" + chunkIndex + "|" + revision)
```

The SHA-256 output is truncated to a UUID-compatible format (first 128 bits as a `Guid`) for Qdrant's UUID point ID type.

Implementation in `QdrantVectorStore`:

```csharp
private static Guid ComputePointId(
    string repoPath, string filePath, string symbolKey, string revision)
{
    var input = $"{repoPath}|{filePath}|{symbolKey}|{revision}";
    var hash  = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return new Guid(hash[..16]);
}
```

All upsert operations use `Upsert` (not `Insert`) — if a point with the same ID already exists, it is overwritten.

## Consequences

**Positive**
- Incremental reindex is naturally idempotent — re-processing a file produces the same IDs, overwriting unchanged vectors
- File deletion cleanup: recompute IDs for all chunks of the deleted file, issue batch `Delete` by ID — no metadata scan required
- No separate ID tracking table needed
- Collision probability with SHA-256 truncated to 128 bits is negligible for any realistic codebase size

**Negative**
- If `repoPath`, `filePath`, or `symbolName` changes (e.g. file rename), the old point is orphaned — it must be detected and cleaned up during reindex via the changed-file diff
- The ID is opaque — it cannot be decoded back to its source components without the original inputs

## File Rename Handling

File renames are detected by `IReindexStrategy` as a pair: deleted path + added path. The pipeline:
1. Recomputes and deletes old point IDs for the deleted path at the old revision
2. Indexes the new path and upserts with new IDs

This is handled identically in both `GitReindexStrategy` (rename detection via `TreeChanges`) and `SvnReindexStrategy` (action type `Moved` in log entries).
