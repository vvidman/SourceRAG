# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run Commands

```bash
# Build entire solution
dotnet build SourceRAG.slnx

# Run individual hosts (each in its own terminal)
dotnet run --project src/SourceRAG.Api
dotnet run --project src/SourceRAG.McpHost
dotnet run --project src/SourceRAG.Web

# Run all tests
dotnet test SourceRAG.sln

# Run tests for a single project
dotnet test tests/SourceRAG.Domain.Tests
dotnet test tests/SourceRAG.Application.Tests
dotnet test tests/SourceRAG.Infrastructure.Tests

# Run a single test by name
dotnet test tests/SourceRAG.Application.Tests --filter "FullyQualifiedName~MyTestMethod"
```

## Prerequisites

- .NET 10 SDK
- Qdrant: `docker run -d -p 6333:6333 qdrant/qdrant`
- For `Local` embedding: a GGUF model (e.g. `nomic-embed-text`) — path set in `appsettings.json`
- For `Api` embedding: `ANTHROPIC_API_KEY` environment variable

## Architecture

SourceRAG is a RAG system where the VCS (Git or SVN) is the proof store — no source content is duplicated. The vector store (Qdrant) holds only embeddings and metadata. File content is reconstructed on-demand from the repository at query time using `revision + filePath`.

### Layer Rules (strict dependency inversion)

- **Domain** — entities, interfaces, enums. No NuGet dependencies except `System.*`. Entities are immutable records.
- **Application** — MediatR handlers for `IndexRepository`, `ReindexChangedFiles`, `ChatQuery`, `GetIndexStatus`. Depends on Domain only. All cross-cutting orchestration lives here. FluentValidation on commands.
- **Infrastructure** — implements Domain interfaces. Organized as vertical slices. Depends on Domain + Application.
- **Api / McpHost** — thin hosts that wire up DI and delegate immediately to MediatR. No business logic in endpoint handlers or MCP tool handlers.
- **Web** — Blazor client. Communicates exclusively via the REST API using a typed `SourceRagApiClient`. Has no dependency on Application or Infrastructure.

### Infrastructure Slices

**VCS (`IVcsProvider` + `IReindexStrategy` always registered as a pair):**
- `Vcs/Git/` — LibGit2Sharp; head = commit SHA; incremental diff via `HEAD..lastHash`
- `Vcs/Svn/` — SharpSvn; head = revision number; incremental via `GetLog(lastRev+1, HEAD)`
- Never register a provider from one VCS with the strategy from another — this is enforced in `ProviderConfiguration.cs`. Adding a new VCS means a new folder with two files only.

**Embedding (`IEmbeddingProvider`):**
- `Embedding/Local/` — LlamaSharpEmbeddingProvider (GGUF model)
- `Embedding/Api/` — AnthropicEmbeddingProvider
- Both expose `int Dimensions`; the Qdrant collection is created with the correct dimensionality at startup.

**Chunking — Chain of Responsibility (`IEnumerable<IChunker>`):**
- `RoslynChunker` — handles `*.cs`; produces method/class/property-boundary chunks
- `PlainTextChunker` — fallback wildcard; sliding window
- Pipeline takes the first `CanHandle == true`. Add new language chunkers without modifying existing ones.

**Vector Store:**
- `QdrantVectorStore` — point ID is `sha256(repoPath + filePath + symbolName + revision)`, making upserts idempotent during incremental reindex.

### Dual Hosting

`SourceRAG.Api` (REST) and `SourceRAG.McpHost` (MCP) are independent processes sharing the same Application and Infrastructure layers. They can run side by side or independently with no IPC between them.

MCP tools: `search_codebase` → `ChatQueryCommand`, `index_repository` → `IndexRepositoryCommand`, `get_index_status` → `GetIndexStatusQuery`.

REST endpoints: `POST /chat`, `POST /index`, `GET /index/status`.

### Indexing Pipeline

`GetFilesAtHeadAsync` → `GetFileContentAsync + GetBlameAsync` → `IChunker` → `IEmbeddingProvider.EmbedAsync` → `IVectorStore.UpsertAsync` → update `PipelineContext.LastIndexedRevision`

For incremental reindex, only step 1 is replaced by `IReindexStrategy.DetermineChangedFilesAsync()`. Deleted files trigger point removal from Qdrant.

### Query Pipeline

`EmbedAsync(query)` → `IVectorStore.SearchAsync` → `IVcsProvider.GetFileContentAsync(revision, filePath)` per chunk → LLM call with assembled context → `QueryResult { Answer, Chunks }`

## Key Configuration (`appsettings.json`)

```json
{
  "SourceRAG": {
    "VcsProvider": "Git",          // "Git" | "Svn"
    "EmbeddingProvider": "Local",  // "Local" | "Api"
    "RepositoryPath": "/path/to/repo",
    "Branch": "main",
    "Qdrant": { "Endpoint": "http://localhost:6333", "CollectionName": "sourcerag" },
    "LlamaSharp": { "ModelPath": "/models/nomic-embed-text.gguf" },
    "Anthropic": { "Model": "claude-3-5-haiku-20241022" }
  }
}
```

Secret override: `ANTHROPIC_API_KEY` environment variable.

## Observability

AiObservability spans are instrumented at both pipeline boundaries. Key spans: `sourcerag.index.file`, `sourcerag.index.embed`, `sourcerag.query.embed`, `sourcerag.query.search`, `sourcerag.query.reconstruct`, `sourcerag.query.llm`. Trace store is configurable: `InMemory` (dev), `JsonTraceStore` (file), `Postgres` (prod).

## ADRs

Architecture decisions are documented in `/docs/adr/`. Key decisions to read before making structural changes:
- ADR-001: VCS provider+strategy pairing
- ADR-002: VCS as proof store (no content duplication)
- ADR-003: Chunking chain of responsibility
- ADR-006: Qdrant point ID scheme (sha256-based, idempotent upserts)
- ADR-008: Dual hosting rationale
