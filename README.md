# SourceRAG

> A chat-based knowledge search engine over source repositories, powered by semantic vector search and grounded in version control history.

## Overview

SourceRAG is a .NET-based RAG (Retrieval-Augmented Generation) system that treats a **source control repository as its proof database**. Instead of duplicating source content into a relational database, SourceRAG uses the VCS itself (Git or SVN) as the ground truth — the vector store holds only embeddings and metadata, while the actual content is always reconstructed on-demand from the repository.

This means every answer is traceable to a specific file, symbol, author, commit, and timestamp — not just a chunk of text.

---

## Key Design Principles

- **VCS as proof store** — no content duplication; git/svn is the source of truth
- **Syntax-aware chunking** — Roslyn-based chunking for C# (method/class/property boundaries), plain-text fallback for all other files
- **Provider model** — Git vs SVN and Local LLM vs API embedding are runtime-configurable vertical slices
- **Clean Architecture** — Domain → Application → Infrastructure; hosting concerns are isolated to separate projects
- **Dual hosting** — REST API for the Blazor web client, MCP server for AI agent integration (Claude Desktop, Copilot, etc.)
- **Observability-first** — AiObservability instrumentation across indexing and query pipelines

---

## Architecture Summary

```
┌─────────────────────────────────────────────────────────┐
│  Clients                                                │
│  ┌──────────────────┐   ┌──────────────────────────┐   │
│  │  Blazor Web UI   │   │  AI Agent (MCP client)   │   │
│  └────────┬─────────┘   └────────────┬─────────────┘   │
└───────────┼────────────────────────── ┼ ────────────────┘
            │ REST                      │ MCP
┌───────────▼───────────┐  ┌────────────▼──────────────┐
│   SourceRAG.Api       │  │   SourceRAG.McpHost        │
└───────────┬───────────┘  └────────────┬──────────────┘
            └──────────────┬────────────┘
                           │ MediatR
              ┌────────────▼────────────┐
              │  SourceRAG.Application  │
              └────────────┬────────────┘
                           │
         ┌─────────────────┼──────────────────┐
         │                 │                  │
┌────────▼──────┐ ┌────────▼──────┐  ┌────────▼──────┐
│  VCS          │ │  Embedding    │  │  Vector Store │
│  Git / SVN    │ │  Local / API  │  │  Qdrant       │
└───────────────┘ └───────────────┘  └───────────────┘
```

---

## Solution Structure

```
SourceRAG.sln
├── src/
│   ├── SourceRAG.Domain/          # Entities, interfaces, enums — no dependencies
│   ├── SourceRAG.Application/     # Use cases, MediatR handlers — depends on Domain
│   ├── SourceRAG.Infrastructure/  # VCS, Embedding, Chunking, Qdrant — implements Domain
│   ├── SourceRAG.Api/             # ASP.NET Core minimal API host (REST)
│   ├── SourceRAG.McpHost/         # MCP server host (AI agent integration)
│   └── SourceRAG.Web/             # Blazor Web UI chat client
└── tests/
    ├── SourceRAG.Domain.Tests/
    ├── SourceRAG.Application.Tests/
    └── SourceRAG.Infrastructure.Tests/
```

---

## Provider Configuration

All provider choices are runtime-configurable via `appsettings.json`:

```json
{
  "SourceRAG": {
    "VcsProvider": "Git",
    "EmbeddingProvider": "Local",
    "RepositoryPath": "/path/to/repo",
    "Branch": "main"
  }
}
```

| Setting | Options |
|---|---|
| `VcsProvider` | `Git` \| `Svn` |
| `EmbeddingProvider` | `Local` (LlamaSharp) \| `Api` (Anthropic) |
| `Branch` | `main` \| `trunk` \| any branch name |

---

## VCS Provider Behaviour

| | Git | SVN |
|---|---|---|
| Library | LibGit2Sharp | SharpSvn |
| Head revision | Commit hash (SHA) | Revision number |
| Re-index detection | `HEAD..lastIndexed` diff | `GetLog(fromRev, HEAD)` |
| Blame info | `git blame` | `svn blame` |
| Branch scope | `main` only | `trunk` only |

---

## Chunk Proof Structure

Every vector stored in Qdrant carries the following metadata payload — this is the "proof" that replaces a traditional SQL proof store:

```json
{
  "file_path": "src/Core/ImageProcessor.cs",
  "symbol_name": "ProcessTile",
  "symbol_type": "Method",
  "revision": "a3f9c12e",
  "author": "vvidman",
  "commit_message": "Fix OOM on large WSI files",
  "timestamp": "2024-11-03T14:22:00Z",
  "branch": "main",
  "start_line": 42,
  "end_line": 78
}
```

At query time, file content is reconstructed from the VCS using `revision + file_path`. Nothing is stored twice.

---

## MCP Tools

When running as an MCP server (`SourceRAG.McpHost`), the following tools are exposed to AI agents:

| Tool | Description |
|---|---|
| `search_codebase` | Semantic search over the indexed repository |
| `index_repository` | Trigger full or incremental reindex |
| `get_index_status` | Return current index state, last revision, chunk count |

---

## Prerequisites

- .NET 9 SDK
- Qdrant (Docker recommended: `docker run -p 6333:6333 qdrant/qdrant`)
- For `Local` embedding: a compatible GGUF model (e.g. `nomic-embed-text`)
- For `Api` embedding: Anthropic API key in environment (`ANTHROPIC_API_KEY`)
- Git or SVN CLI available on PATH (for blame operations)

---

## Getting Started

```bash
# Clone the repository
git clone https://github.com/vvidman/SourceRAG.git
cd SourceRAG

# Start Qdrant
docker run -d -p 6333:6333 qdrant/qdrant

# Configure your repository path and provider in appsettings.json

# Run the REST API
dotnet run --project src/SourceRAG.Api

# Run the MCP server (separate terminal)
dotnet run --project src/SourceRAG.McpHost

# Run the Blazor web client
dotnet run --project src/SourceRAG.Web
```

---

## ADR Index

Architecture Decision Records are located in `/docs/adr/`.

| ADR | Decision |
|---|---|
| ADR-001 | VCS abstraction: provider + strategy as paired vertical slices |
| ADR-002 | Proof store = VCS repository, not a relational database |
| ADR-003 | Chunking: Roslyn primary, PlainText fallback (Chain of Responsibility) |
| ADR-004 | Embedding: Local (LlamaSharp) vs API — config-driven, no code change |
| ADR-005 | Re-index scope: main/trunk branch only |
| ADR-006 | Qdrant point ID = `sha256(repoPath + filePath + symbolName + revision)` |
| ADR-007 | AiObservability integration — spans across indexing and query pipelines |
| ADR-008 | Dual hosting: REST (Api) + MCP (McpHost) over shared Application layer |
| ADR-009 | Chat client: Blazor Web with typed HttpClient targeting REST API |
| ADR-010 | VCS credential resolution: env variables + IVcsCredentialProvider, read-only service role |
| ADR-011 | Authentication: Blazor Web UI + MCP server via Azure AD / Entra ID (OAuth 2.0) |

---

## Related Projects

- [RagLab](https://github.com/vvidman/RagLab) — hand-built RAG pipeline in .NET/C#, LlamaSharp + Claude API, dual vector store
- [AiObservability](https://github.com/vvidman/AiObservability) — .NET observability library, integrated across all AI pipeline projects
- [Scaffold Protocol](https://github.com/vvidman/ScaffoldProtocol) — human-in-the-loop AI pipeline with structured output validation

---

## License

Apache License 2.0, see [License](LICENSE)
