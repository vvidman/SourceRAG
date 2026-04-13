# ADR-001 — VCS Abstraction: Provider + Strategy as Paired Vertical Slices

## Status
Accepted

## Date
2025-04-13

## Context

SourceRAG must support two fundamentally different version control systems: Git and SVN. These systems differ not only in their client libraries but also in their conceptual model for tracking change history:

- Git uses content-addressable commit hashes; history traversal is graph-based
- SVN uses monotonically increasing revision numbers; history traversal is linear

Beyond repository access, the re-indexing logic is tightly coupled to the VCS model. A Git re-index strategy (`HEAD..lastHash` diff) cannot be applied to an SVN repository, and vice versa. Treating these as independent, separately injectable concerns would allow invalid combinations at runtime (e.g. `GitVcsProvider` paired with `SvnReindexStrategy`).

A single `IVcsProvider` interface covering both systems is feasible, but the re-index strategy must be paired with its corresponding provider to remain consistent.

## Decision

The VCS concern is split into two interfaces in the Domain layer:

```
IVcsProvider       — repository access: file listing, content retrieval, blame
IReindexStrategy   — change detection: determines which files changed since last index
```

These two interfaces are **always registered as a pair** in the DI container. The pairing is enforced in `ProviderConfiguration.cs` at application startup — it is not possible to register a Git provider with an SVN strategy or vice versa.

Each pair forms a vertical slice in `SourceRAG.Infrastructure/Vcs/`:

```
Vcs/
  Git/
    GitVcsProvider.cs
    GitReindexStrategy.cs
  Svn/
    SvnVcsProvider.cs
    SvnReindexStrategy.cs
```

The active pair is selected by the `VcsProvider` configuration key (`"Git"` or `"Svn"`).

## Consequences

**Positive**
- Invalid provider/strategy combinations are impossible at runtime
- Adding a new VCS (e.g. Mercurial) requires only a new folder with two files — no existing code is modified (Open/Closed Principle)
- Each slice can be tested in isolation with a mock repository

**Negative**
- Slightly more DI registration boilerplate compared to a single interface
- Both interfaces must remain stable; changes to either require updating all implementations

## Alternatives Considered

**Single `IVcsProvider` with embedded re-index logic** — rejected because change detection logic is fundamentally different per VCS and would require branching inside the provider, violating Single Responsibility Principle.

**Strategy as a separate injectable independent of provider** — rejected because it allows misconfiguration at runtime.
