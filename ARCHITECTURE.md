# SourceRAG — Architecture Definition

## Architectural Style

SourceRAG follows **Clean Architecture** with **Vertical Slice providers** at the infrastructure boundary.
The core principle is strict dependency inversion: all infrastructure concerns implement Domain-defined interfaces.
Hosting concerns (REST, MCP) are isolated to their own projects and share nothing except the Application layer via MediatR.

---

## Layer Definitions

### SourceRAG.Domain
**No external dependencies.**

Contains:
- Entities: `CodeChunk`, `ChunkMetadata`, `QueryResult`, `VcsFile`, `ChangedFile`, `FileBlameInfo`, `ReindexScope`
- Interfaces: `IVcsProvider`, `IReindexStrategy`, `IEmbeddingProvider`, `IChunker`, `IVectorStore`
- Enums: `SymbolType`, `VcsProviderType`, `EmbeddingProviderType`

Rules:
- No NuGet package references except `System.*`
- No `async` infrastructure concerns — interfaces use `Task<T>` but implementations live in Infrastructure
- Entities are immutable records where possible

---

### SourceRAG.Application
**Depends on: Domain only.**

Contains:
- MediatR commands and handlers for: `IndexRepository`, `ReindexChangedFiles`, `ChatQuery`, `GetIndexStatus`
- `IPipelineOrchestrator` — coordinates chunking → embedding → vector upsert
- `PipelineContext` — carries state through an indexing pipeline run

Rules:
- No direct infrastructure references
- All side effects go through Domain interfaces injected via constructor
- Handlers are the only place where cross-cutting orchestration logic lives
- Validation via FluentValidation on commands

---

### SourceRAG.Infrastructure
**Depends on: Domain + Application.**

Organized as vertical slices, each slice is independently replaceable:

#### Slice 1 — VCS (paired provider + strategy)
```
Vcs/
  Git/
    GitVcsProvider.cs         implements IVcsProvider
    GitReindexStrategy.cs     implements IReindexStrategy
  Svn/
    SvnVcsProvider.cs         implements IVcsProvider
    SvnReindexStrategy.cs     implements IReindexStrategy
```

`IVcsProvider` and `IReindexStrategy` are **always registered as a pair** — a Git provider with an SVN strategy is not a valid configuration. This pairing is enforced at DI registration time in `ProviderConfiguration.cs`.

#### Slice 2 — Embedding
```
Embedding/
  Local/
    LlamaSharpEmbeddingProvider.cs    implements IEmbeddingProvider
  Api/
    AnthropicEmbeddingProvider.cs     implements IEmbeddingProvider
```

Both providers expose `int Dimensions` — the Qdrant collection is created with the correct dimensionality at startup based on the active provider.

#### Chunking — Chain of Responsibility
```
Chunking/
  RoslynChunker.cs       CanHandle: *.cs — symbol-boundary chunks via Roslyn
  PlainTextChunker.cs    CanHandle: * (fallback) — sliding window
```

Chunkers are registered as `IEnumerable<IChunker>`. The pipeline always takes the first `CanHandle == true`. New language chunkers can be added without modifying existing code (Open/Closed Principle).

#### VectorStore
```
VectorStore/
  QdrantVectorStore.cs   implements IVectorStore
```

Point ID strategy: `sha256(repoPath + filePath + symbolName + revision)` — deterministic, collision-resistant, enables idempotent upserts during incremental reindex.

---

### SourceRAG.Api
**Depends on: Application + Infrastructure (via DI).**

ASP.NET Core Minimal API. Exposes:

| Endpoint | Method | Description |
|---|---|---|
| `/chat` | POST | Submit a natural language query, returns answer + chunk proofs |
| `/index` | POST | Trigger full or incremental reindex |
| `/index/status` | GET | Current index state, last revision, chunk count |

All endpoints delegate immediately to MediatR — no business logic in endpoint handlers.

---

### SourceRAG.McpHost
**Depends on: Application + Infrastructure (via DI).**

MCP server using the `ModelContextProtocol` .NET SDK. Exposes three tools to AI agent clients:

| Tool name | Maps to |
|---|---|
| `search_codebase` | `ChatQueryCommand` |
| `index_repository` | `IndexRepositoryCommand` |
| `get_index_status` | `GetIndexStatusQuery` |

The MCP host and the REST API host are **independent processes** sharing the same Application and Infrastructure layers. They can run side by side or independently. There is no inter-process communication between them.

---

### SourceRAG.Web
**Depends on: nothing except HTTP (typed HttpClient).**

Blazor Web App (Server or WASM — TBD in ADR-009). Communicates exclusively via the REST API — it has no direct dependency on Application or Infrastructure.

Key components:

| Component | Purpose |
|---|---|
| `Chat.razor` | Main chat page — message history, input |
| `MessageBubble.razor` | Renders a single user or assistant message |
| `ChunkProofCard.razor` | Renders VCS proof metadata per retrieved chunk |
| `IndexStatusPanel.razor` | Shows current index state, triggers reindex |
| `SourceRagApiClient.cs` | Typed HttpClient wrapping all REST endpoints |

`ChunkProofCard` is the primary UI differentiator — it surfaces `author`, `commit_message`, `revision`, `file_path`, `start_line..end_line`, and `timestamp` for every chunk returned alongside an answer.

---

## Dependency Graph

```
SourceRAG.Web
    └── HTTP → SourceRAG.Api
                    └── Application
                            └── Domain
                    └── Infrastructure
                            └── Domain

SourceRAG.McpHost
    └── Application
            └── Domain
    └── Infrastructure
            └── Domain
```

No circular dependencies. Infrastructure never references Api, McpHost, or Web.

---

## Indexing Pipeline

```
1. IVcsProvider.GetFilesAtHeadAsync()
        │
        ▼
2. For each file: IVcsProvider.GetFileContentAsync() + GetBlameAsync()
        │
        ▼
3. IChunker (first CanHandle match) → List<CodeChunk>
        │
        ▼
4. IEmbeddingProvider.EmbedAsync(chunk.Text) → float[]
        │
        ▼
5. IVectorStore.UpsertAsync(pointId, vector, ChunkMetadata payload)
        │
        ▼
6. PipelineContext.LastIndexedRevision = currentRevision
```

For incremental reindex, step 1 is replaced by `IReindexStrategy.DetermineChangedFilesAsync()`, which returns only the affected file set. Steps 2–6 are identical.

---

## Query Pipeline

```
1. ChatQueryCommand(query, topK)
        │
        ▼
2. IEmbeddingProvider.EmbedAsync(query)
        │
        ▼
3. IVectorStore.SearchAsync(queryVector, topK) → List<ScoredChunk>
        │
        ▼
4. For each ScoredChunk: IVcsProvider.GetFileContentAsync(revision, filePath)
        │
        ▼
5. Context assembly: chunk content + ChunkMetadata
        │
        ▼
6. LLM call (prompt + context) → answer text
        │
        ▼
7. QueryResult { Answer, Chunks: List<CodeChunk with Metadata> }
```

---

## Re-index Strategy Behaviour

### Git (GitReindexStrategy)
- Uses LibGit2Sharp to compute `diff` between `lastIndexedCommitHash` and `HEAD`
- Returns the set of changed, added, and deleted file paths
- Deleted files trigger point removal from Qdrant

### SVN (SvnReindexStrategy)
- Uses SharpSvn `GetLog(startRevision: lastIndexed + 1, endRevision: HEAD)`
- Extracts affected paths from each `SvnLogEventArgs`
- Same downstream pipeline as Git

Both strategies are scoped to `main`/`trunk` — cross-branch indexing is out of scope (ADR-005).

---

## Observability

AiObservability is integrated at two pipeline boundaries:

| Span | Attributes |
|---|---|
| `sourcerag.index.file` | `file_path`, `chunk_count`, `revision` |
| `sourcerag.index.embed` | `symbol_name`, `dimensions`, `provider` |
| `sourcerag.query.embed` | `query_length`, `provider` |
| `sourcerag.query.search` | `top_k`, `result_count` |
| `sourcerag.query.reconstruct` | `file_path`, `revision` |
| `sourcerag.query.llm` | `model`, `context_tokens` |

Trace store is configurable: `InMemory` (dev), `JsonTraceStore` (file), `Postgres` (prod) — inherited from AiObservability architecture.

---

## Configuration Reference

```json
{
  "SourceRAG": {
    "VcsProvider": "Git",
    "EmbeddingProvider": "Local",
    "RepositoryPath": "/path/to/repo",
    "Branch": "main",
    "Qdrant": {
      "Endpoint": "http://localhost:6333",
      "CollectionName": "sourcerag"
    },
    "LlamaSharp": {
      "ModelPath": "/models/nomic-embed-text.gguf"
    },
    "Anthropic": {
      "Model": "claude-3-5-haiku-20241022"
    }
  }
}
```

Environment variable override for secrets:
```
ANTHROPIC_API_KEY=sk-ant-...
```

---

## NuGet Dependencies

| Package | Layer | Purpose |
|---|---|---|
| `MediatR` | Application | CQRS command/query dispatch |
| `FluentValidation` | Application | Command validation |
| `LibGit2Sharp` | Infrastructure/Vcs/Git | Git operations |
| `SharpSvn` | Infrastructure/Vcs/Svn | SVN operations |
| `Microsoft.CodeAnalysis.CSharp` | Infrastructure/Chunking | Roslyn syntax tree |
| `LlamaSharp` | Infrastructure/Embedding/Local | Local GGUF model inference |
| `Qdrant.Client` | Infrastructure/VectorStore | Qdrant gRPC client |
| `ModelContextProtocol` | McpHost | MCP server protocol |
| `Microsoft.Identity.Web` | Api + McpHost + Web | Azure AD / Entra ID OAuth 2.0 |
| `Microsoft.AspNetCore.Components.Web` | Web | Blazor components |

---

## Out of Scope (v1)

- Multi-branch indexing
- Streaming LLM responses (planned for v2)
- Non-C# syntax-aware chunking (Tree-sitter integration planned for v2)
- SVN externals handling
- OS credential store for VCS credentials (planned for v2)
