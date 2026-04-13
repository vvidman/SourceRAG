# ADR-007 — AiObservability Integration

## Status
Accepted

## Date
2025-04-13

## Context

SourceRAG executes two multi-step pipelines — indexing and query — each involving external I/O (VCS access, embedding inference, vector store operations, LLM calls). Without structured instrumentation:

- It is difficult to identify which pipeline step is a performance bottleneck
- Errors are hard to correlate to a specific file, chunk, or query
- Embedding provider latency (local vs API) cannot be compared empirically
- There is no audit trail of what was indexed, when, and at which revision

The AiObservability library (developed as a companion project) provides a span/trace abstraction over AI pipeline steps, with pluggable trace stores (InMemory, JsonTraceStore, Postgres). It is already integrated into RagLab, ChaosForge, and Scaffold Protocol — using it in SourceRAG maintains consistency across the portfolio.

## Decision

SourceRAG integrates AiObservability via `AiObs.Abstractions` (referenced in Application layer) and `AiObs.Core` / `AiObs.Postgres` (registered in Infrastructure/host layers).

### Instrumented spans

**Indexing pipeline:**

| Span name | Key attributes |
|---|---|
| `sourcerag.index.file` | `file_path`, `chunk_count`, `vcs_provider`, `revision` |
| `sourcerag.index.chunk` | `symbol_name`, `symbol_type`, `start_line`, `end_line` |
| `sourcerag.index.embed` | `provider`, `dimensions`, `duration_ms` |
| `sourcerag.index.upsert` | `point_id`, `collection` |

**Query pipeline:**

| Span name | Key attributes |
|---|---|
| `sourcerag.query.embed` | `provider`, `query_length`, `duration_ms` |
| `sourcerag.query.search` | `top_k`, `result_count`, `duration_ms` |
| `sourcerag.query.reconstruct` | `file_path`, `revision`, `duration_ms` |
| `sourcerag.query.llm` | `model`, `context_tokens`, `duration_ms` |

### Trace store configuration

Inherits AiObservability configuration model:

```json
{
  "AiObservability": {
    "TraceStore": "Postgres"
  }
}
```

| Value | Use case |
|---|---|
| `InMemory` | Unit tests, development |
| `Json` | Local file-based trace dump |
| `Postgres` | Production, persistent trace history |

### Application layer boundary

`IAiObsTracer` is injected into Application layer handlers via `AiObs.Abstractions`. The Application layer does not reference `AiObs.Core` or `AiObs.Postgres` directly — this maintains the Clean Architecture dependency rule.

## Consequences

**Positive**
- Full pipeline visibility: per-file indexing duration, embedding latency per provider, query step breakdown
- Consistent observability model across all portfolio projects
- Trace store is swappable without modifying instrumented code
- Postgres trace store enables historical performance analysis across reindex runs

**Negative**
- Adds `AiObs.Abstractions` as a Domain/Application dependency — this is an internal package dependency, acceptable given portfolio cohesion
- Span instrumentation adds minor overhead per pipeline step (negligible for I/O-bound pipelines)

## Related

- AiObservability project: `AiObs.Abstractions`, `AiObs.Core`, `AiObs.Postgres`
- Referenced in: RagLab, ChaosForge, Scaffold Protocol
