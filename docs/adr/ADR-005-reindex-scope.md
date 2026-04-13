# ADR-005 — Re-index Scope: main/trunk Branch Only

## Status
Accepted

## Date
2025-04-13

## Context

A source repository may contain many branches. Indexing all branches simultaneously would:
- Multiply index size proportionally to the number of branches
- Introduce ambiguity in search results (same file, different content, different branches)
- Complicate the proof metadata model (branch becomes a required disambiguation key)
- Significantly increase re-index time and Qdrant storage requirements

For the primary use case — understanding and querying a production or mainline codebase — the stable, long-lived branch contains the most relevant and authoritative version of the code.

In Git projects, this branch is conventionally `main` or `master`. In SVN projects, this is `trunk`. Both represent the integration branch to which all feature work is ultimately merged.

## Decision

SourceRAG indexes **exactly one branch per repository**, configured via the `Branch` setting in `appsettings.json`.

Default values:
- Git: `main`
- SVN: `trunk`

The `Branch` value is:
1. Validated at startup — if the branch does not exist in the repository, startup fails with a descriptive error
2. Stored in `ChunkMetadata.Branch` on every indexed chunk
3. Used as a filter in `IReindexStrategy` — only commits/revisions on this branch are considered

Cross-branch search, multi-branch indexing, and branch comparison are **out of scope for v1**.

## Consequences

**Positive**
- Index size is predictable and bounded
- Search results are unambiguous — all chunks come from the same branch
- Re-index logic is simple: one linear history to track
- `ChunkMetadata.Branch` is informational rather than a required query filter

**Negative**
- Feature branches, hotfix branches, and release branches are not searchable
- Teams working heavily in long-lived feature branches may find results stale if `main` has not yet received their changes
- Branch rename (e.g. `master` → `main`) requires a configuration update and full reindex

## Future Consideration (v2)

Multi-branch support could be implemented by:
- Treating each branch as a separate Qdrant collection, or
- Including `branch` as a required metadata filter on all queries

This decision is deferred to v2. The current architecture does not block this extension — `Branch` is already a first-class field in `ChunkMetadata`.
