# ADR-008 — Dual Hosting: REST (Api) + MCP (McpHost) Over Shared Application Layer

## Status
Accepted

## Date
2025-04-13

## Context

SourceRAG serves two distinct consumer types with different integration needs:

1. **Human users** via a Blazor web chat interface — best served by a conventional REST API over HTTP, which Blazor's typed `HttpClient` can call directly
2. **AI agents** (Claude Desktop, GitHub Copilot, other MCP-compatible hosts) — best served by an MCP (Model Context Protocol) server, which exposes tools that AI agents can invoke as part of their reasoning loop

These two integration surfaces have different protocol requirements:
- REST: HTTP verbs, JSON request/response, OpenAPI documentation
- MCP: stdio or SSE transport, tool schema discovery, structured tool result format

A single host that attempts to serve both concerns would conflate two different protocol layers, complicating the codebase and making it harder to deploy each independently (e.g. MCP server on a developer machine, REST API on a server).

Clean Architecture already prescribes that hosting concerns belong in separate, outermost-layer projects. The Application layer — containing all use cases as MediatR handlers — is agnostic to how it is invoked. Both hosts can share it without modification.

## Decision

SourceRAG is deployed as **two independent host processes**:

**`SourceRAG.Api`** — ASP.NET Core Minimal API
- Exposes: `POST /chat`, `POST /index`, `GET /index/status`
- Consumed by: `SourceRAG.Web` (Blazor chat client) via typed `HttpClient`
- Transport: HTTP/JSON

**`SourceRAG.McpHost`** — MCP Server
- Exposes tools: `search_codebase`, `index_repository`, `get_index_status`
- Consumed by: Claude Desktop, Copilot, any MCP-compatible AI agent
- Transport: stdio (default) or SSE

Both hosts:
- Reference `SourceRAG.Application` and `SourceRAG.Infrastructure`
- Bootstrap the same DI registrations via shared `ProviderConfiguration` extension methods
- Dispatch all operations through MediatR — no business logic in endpoint or tool handlers
- Read from the same `appsettings.json` configuration

The two processes do **not** communicate with each other. They independently access the same Qdrant instance and the same repository path.

### MCP tool → MediatR mapping

| MCP Tool | MediatR Command/Query |
|---|---|
| `search_codebase(query, topK)` | `ChatQueryCommand` |
| `index_repository(mode)` | `IndexRepositoryCommand` |
| `get_index_status()` | `GetIndexStatusQuery` |

## Consequences

**Positive**
- Each host can be deployed, updated, and restarted independently
- The Application layer has zero awareness of hosting — pure CQRS handlers
- MCP tooling is available to AI agents without exposing a public REST API
- REST API is available to the Blazor client without requiring MCP infrastructure
- New hosting surfaces (CLI, gRPC) can be added as new projects with no Application changes

**Negative**
- Two processes to start in development (mitigated by a `docker-compose.yml` or `.NET Aspire` manifest)
- Shared configuration must be kept in sync between both hosts (same `appsettings.json` or shared config source)
- Long-running operations (full reindex) via MCP require async job tracking — the MCP tool returns a job ID, status is polled via `get_index_status`

## Deployment Topology

```
[Developer machine]
  SourceRAG.McpHost  (stdio)  ←→  Claude Desktop / Copilot
  SourceRAG.Api      (HTTP)   ←→  SourceRAG.Web (Blazor)
  Qdrant             (Docker)
  Git/SVN repository (local path)
```
