# SPEC-002 — Application Layer

## Overview
Implement the Application layer: MediatR commands, queries, handlers, pipeline orchestration, and configuration options. No infrastructure concerns — all side effects via injected Domain interfaces.

## References
- ADR-002 (VCS as proof store)
- SPEC-001 (Domain layer — must be complete first)

## Project
`src/SourceRAG.Application`

## NuGet packages to add
```
MediatR
FluentValidation
FluentValidation.DependencyInjectionExtensions
Microsoft.Extensions.Options
Microsoft.Extensions.DependencyInjection.Abstractions
```

## Rules
- Delete `Class1.cs`.
- Handlers receive Domain interfaces via constructor injection only.
- No direct references to Infrastructure types.
- Commands and queries are `sealed record` types.
- Handlers are `sealed class` types.
- All handlers implement `IRequestHandler<TRequest, TResponse>`.

---

## Options

### `Common/SourceRagOptions.cs`
```csharp
public sealed class SourceRagOptions
{
    public const string SectionName = "SourceRAG";

    public required string VcsProvider        { get; init; }   // "Git" | "Svn"
    public required string EmbeddingProvider  { get; init; }   // "Local" | "Api"
    public required string RepositoryPath     { get; init; }
    public string Branch                      { get; init; } = "main";
    public QdrantOptions Qdrant               { get; init; } = new();
    public LlamaSharpOptions LlamaSharp       { get; init; } = new();
    public AnthropicOptions Anthropic         { get; init; } = new();
    public AzureAdOptions AzureAd             { get; init; } = new();
}

public sealed class QdrantOptions
{
    public string Endpoint       { get; init; } = "http://localhost:6333";
    public string CollectionName { get; init; } = "sourcerag";
}

public sealed class LlamaSharpOptions
{
    public string ModelPath { get; init; } = string.Empty;
}

public sealed class AnthropicOptions
{
    public string Model { get; init; } = "claude-3-5-haiku-20241022";
}

public sealed class AzureAdOptions
{
    public string Instance  { get; init; } = "https://login.microsoftonline.com/";
    public string TenantId  { get; init; } = string.Empty;
    public string ClientId  { get; init; } = string.Empty;
    public string Audience  { get; init; } = string.Empty;
}
```

---

## Pipeline Context

### `Common/PipelineContext.cs`
Holds mutable state across a single indexing run. Not a singleton — create a new instance per run.

```csharp
public sealed class PipelineContext
{
    public string RepositoryPath      { get; set; } = string.Empty;
    public string Branch              { get; set; } = string.Empty;
    public string? LastIndexedRevision { get; set; }
    public int ProcessedFileCount     { get; set; }
    public int UpsertedChunkCount     { get; set; }
    public int DeletedChunkCount      { get; set; }
    public DateTimeOffset StartedAt   { get; } = DateTimeOffset.UtcNow;
}
```

---

## Indexing

### `Indexing/IndexRepositoryCommand.cs`
```csharp
public sealed record IndexRepositoryCommand(bool FullReindex = false) : IRequest<IndexJobResult>;
```

### `Indexing/IndexJobResult.cs`
```csharp
public sealed record IndexJobResult(
    int ProcessedFiles,
    int UpsertedChunks,
    int DeletedChunks,
    string ToRevision,
    TimeSpan Duration);
```

### `Indexing/IndexRepositoryHandler.cs`
Implements `IRequestHandler<IndexRepositoryCommand, IndexJobResult>`.

Constructor injects:
- `IVcsProvider`
- `IReindexStrategy`
- `IEnumerable<IChunker>`
- `IEmbeddingProvider`
- `IVectorStore`
- `IOptions<SourceRagOptions>`

Logic:
1. If `FullReindex == false` AND `lastRevision` exists → call `IReindexStrategy.DetermineChangedFilesAsync`; process only changed files; for `Deleted` files, compute point IDs and call `IVectorStore.DeleteAsync`
2. If `FullReindex == true` → call `IVcsProvider.GetFilesAtHeadAsync`; process all files
3. For each file: get content → get blame → find first `IChunker` where `CanHandle == true` → chunk → embed each chunk → upsert with computed point ID
4. Point ID computation: `ComputePointId(repoPath, filePath, symbolKey, revision)` — `sha256` of concatenated string, first 16 bytes as `Guid` (ADR-006)
5. Return `IndexJobResult`

Point ID helper (private static):
```csharp
private static Guid ComputePointId(string repoPath, string filePath, string symbolKey, string revision)
{
    var input = $"{repoPath}|{filePath}|{symbolKey}|{revision}";
    var hash  = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return new Guid(hash[..16]);
}
```

---

## Query

### `Query/ChatQueryCommand.cs`
```csharp
public sealed record ChatQueryCommand(string Query, int TopK = 5) : IRequest<QueryResult>;
```

### `Query/ChatQueryHandler.cs`
Implements `IRequestHandler<ChatQueryCommand, QueryResult>`.

Constructor injects:
- `IEmbeddingProvider`
- `IVectorStore`
- `IVcsProvider`
- `IOptions<SourceRagOptions>`

Logic:
1. Embed the query string
2. Search vector store for `TopK` results
3. For each `ScoredChunk`: call `IVcsProvider.GetFileContentAsync(revision, filePath)` to reconstruct content; trim to `StartLine..EndLine`
4. Assemble context string: for each chunk include `filePath`, `symbolName`, trimmed content
5. Call LLM with assembled context + user query → answer string

Note: The LLM call in step 5 requires an `ILlmProvider` interface — add this to Domain and inject it here. Do not inline any HTTP calls in the handler.

**Add to Domain (`Interfaces/ILlmProvider.cs`):**
```csharp
public interface ILlmProvider
{
    Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct);
}
```

---

## Status

### `Status/GetIndexStatusQuery.cs`
```csharp
public sealed record GetIndexStatusQuery : IRequest<IndexStatus>;
```

### `Status/GetIndexStatusHandler.cs`
Implements `IRequestHandler<GetIndexStatusQuery, IndexStatus>`.

Constructor injects:
- `IVectorStore`
- `IIndexStateStore`

**Add to Domain (`Interfaces/IIndexStateStore.cs`):**
```csharp
public interface IIndexStateStore
{
    Task<string?> GetLastIndexedRevisionAsync(string repoPath, CancellationToken ct);
    Task SetLastIndexedRevisionAsync(string repoPath, string revision, DateTimeOffset indexedAt, CancellationToken ct);
    Task<DateTimeOffset?> GetLastIndexedAtAsync(string repoPath, CancellationToken ct);
}
```

Logic:
1. Get `LastIndexedRevision` from `IIndexStateStore`
2. Get `ChunkCount` from `IVectorStore.CountAsync`
3. Return `IndexStatus`

---

## DI Registration

### `DependencyInjection/ApplicationServiceExtensions.cs`
```csharp
public static IServiceCollection AddApplication(this IServiceCollection services)
{
    services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ApplicationServiceExtensions).Assembly));
    services.AddValidatorsFromAssembly(typeof(ApplicationServiceExtensions).Assembly);
    return services;
}
```

---

## Tests — `tests/SourceRAG.Application.Tests`

Delete `UnitTest1.cs`. Use `NSubstitute` for mocking. Add NuGet: `NSubstitute`.

### `Indexing/IndexRepositoryHandlerTests.cs`
- `Handle_FullReindex_CallsGetFilesAtHead`
- `Handle_IncrementalReindex_CallsDetermineChangedFiles`
- `Handle_DeletedFile_CallsVectorStoreDelete`
- `Handle_NoChunkerForFile_SkipsFile` (no `CanHandle` match)
- `Handle_ReturnsCorrectJobResult`

### `Query/ChatQueryHandlerTests.cs`
- `Handle_EmbedsQueryBeforeSearch`
- `Handle_ReconstructsContentFromVcs`
- `Handle_ReturnsQueryResultWithChunks`

### `Status/GetIndexStatusHandlerTests.cs`
- `Handle_NeverIndexed_ReturnsNullRevision`
- `Handle_ReturnsChunkCountFromVectorStore`
