# ADR-002 — Proof Store = VCS Repository, Not a Relational Database

## Status
Accepted

## Date
2025-04-13

## Context

Traditional RAG systems maintain a "proof store" — a relational database that holds the original chunk text alongside its metadata, so that retrieved vectors can be resolved back to their source content. This approach introduces content duplication: the same source text exists in the source files, the proof database, and (as a vector) in the vector store.

For a source code RAG system, this duplication is particularly problematic:
- Source code changes frequently; the proof store must be kept in sync with the repository
- The repository already contains the authoritative version of every file at every revision
- A relational proof store adds operational complexity (schema, migrations, backup) without providing information that the VCS does not already hold

In SourceRAG, the vector store (Qdrant) holds embeddings and metadata payloads. The metadata payload contains enough information to reconstruct the original chunk content from the VCS at query time: `revision + file_path + start_line + end_line`.

## Decision

There is no relational proof store in SourceRAG.

The Qdrant payload **is** the metadata record. At query time, chunk content is reconstructed on-demand via `IVcsProvider.GetFileContentAsync(repoPath, filePath, revision)` and trimmed to `start_line..end_line`.

The `ChunkMetadata` record stored as a Qdrant payload contains:

```
file_path, symbol_name, symbol_type,
revision, author, commit_message, timestamp, branch,
start_line, end_line
```

No additional persistence layer is required.

## Consequences

**Positive**
- No content duplication between repository and proof store
- No schema migrations; the "proof store schema" is the `ChunkMetadata` record
- Deleting a vector from Qdrant is the only cleanup needed when a file is removed
- The VCS history remains the single source of truth for all content
- Reduces operational dependencies: Qdrant + VCS is sufficient; no SQL database required

**Negative**
- Query time includes a VCS content fetch per chunk (mitigated by caching if needed)
- If the repository is unavailable, chunk content cannot be reconstructed (vector search still works, but proof rendering does not)
- Qdrant payload size is bounded; very large metadata strings (long commit messages) must be truncated

## Alternatives Considered

**SQLite as proof store** — rejected; adds a file-based DB dependency with no benefit over Qdrant payload storage for this use case.

**PostgreSQL as proof store** — rejected; significant operational overhead, and content is already in the VCS.

**Store full chunk text in Qdrant payload** — rejected; duplicates content, inflates payload size, and creates a staleness problem on reindex.
