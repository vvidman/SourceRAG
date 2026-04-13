# ADR-009 — Chat Client: Blazor Web with Typed HttpClient Targeting REST API

## Status
Accepted

## Date
2025-04-13

## Context

SourceRAG requires a chat interface for human users — a UI where a developer can ask natural language questions about a codebase and receive answers alongside traceable source evidence (file, author, commit).

Technology options considered:

**React / TypeScript SPA**
- Strong ecosystem for chat UI components
- Requires a separate build pipeline (Node, npm/yarn, bundler)
- Introduces a JavaScript/TypeScript layer in an otherwise pure .NET solution
- Type definitions for API contracts must be maintained separately from C# models

**Blazor Server**
- Runs .NET on the server, renders UI via SignalR
- C# models shared directly — no separate type definitions
- Requires persistent server connection per user
- Not suitable for offline/local-only deployment without a server process

**Blazor WebAssembly (WASM)**
- Runs .NET in the browser via WebAssembly
- No persistent server connection required; static file hosting possible
- Larger initial download than Blazor Server; slower first load
- HttpClient calls go directly to the REST API — clean separation

**Blazor Web App (unified, .NET 8+)**
- Combines Server and WASM rendering modes with per-component granularity
- Interactive Server for components that benefit from low latency
- Interactive WASM for components that can tolerate initial load time

The primary deployment scenario is a developer running SourceRAG locally. Network latency to a Blazor Server is not a concern in this topology. However, the solution should also be deployable as a static WASM app against a remote API.

## Decision

The chat client is implemented as `SourceRAG.Web` — a **Blazor Web App** (.NET 9, unified rendering mode).

Default render mode: **Interactive Server** for all components in v1. This minimises initial complexity while keeping the WASM upgrade path open (changing render mode requires no component logic changes).

All API communication goes through `SourceRagApiClient` — a typed `HttpClient` wrapper:

```csharp
public sealed class SourceRagApiClient(HttpClient http)
{
    public Task<QueryResult> ChatAsync(string query, int topK = 5, CancellationToken ct = default);
    public Task<IndexJobResult> IndexAsync(string mode = "incremental", CancellationToken ct = default);
    public Task<IndexStatus> GetStatusAsync(CancellationToken ct = default);
}
```

`SourceRAG.Web` has **no direct reference** to `SourceRAG.Application` or `SourceRAG.Infrastructure`. It is a pure HTTP client — the REST API is the only integration point.

### Key UI components

| Component | Responsibility |
|---|---|
| `Chat.razor` | Page: message history, input box, submit |
| `MessageBubble.razor` | Renders one user or assistant message |
| `ChunkProofCard.razor` | Renders VCS proof per retrieved chunk |
| `IndexStatusPanel.razor` | Shows index state, last revision, triggers reindex |

`ChunkProofCard` is the primary differentiating UI element — it surfaces `author`, `commit_message`, `revision` (first 8 chars), `file_path`, `symbol_name`, and `start_line..end_line` for every chunk returned with an answer.

## Consequences

**Positive**
- Full .NET stack — C# models, no JavaScript/TypeScript duplication
- No separate build pipeline for the frontend
- `SourceRagApiClient` typed client provides compile-time safety for API calls
- Interactive Server mode gives fast UI response with minimal initial download
- Upgrade to WASM or hybrid rendering is a per-component attribute change

**Negative**
- Blazor Server requires a persistent SignalR connection — not suitable for very high concurrency (not a concern for a single-developer local tool)
- WASM upgrade in v2 requires the REST API to allow CORS if hosted on a different origin

## Future (v2)

- Streaming LLM responses via `IAsyncEnumerable` / SSE rendered progressively in `MessageBubble`
- WASM render mode for offline-capable deployment
- Syntax-highlighted code preview within `ChunkProofCard`
