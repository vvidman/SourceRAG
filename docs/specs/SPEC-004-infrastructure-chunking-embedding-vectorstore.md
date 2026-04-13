# SPEC-004 — Infrastructure: Chunking, Embedding, Vector Store

## Overview
Implement the three remaining Infrastructure slices: syntax-aware chunking (Roslyn + PlainText fallback), embedding providers (LlamaSharp + Anthropic), and the Qdrant vector store.

## References
- ADR-003 (chunking chain of responsibility)
- ADR-004 (embedding provider)
- ADR-006 (Qdrant point ID)

## Project
`src/SourceRAG.Infrastructure`

## NuGet packages to add
```
Microsoft.CodeAnalysis.CSharp
LlamaSharp
Qdrant.Client
Anthropic.SDK
Microsoft.Extensions.Http
```

---

## Chunking

### `Chunking/RoslynChunker.cs`
Implements `IChunker`.

**`CanHandle(filePath)`**
Returns `true` if `Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase)`.

**`Chunk(content, baseMetadata)`**
1. Parse with `CSharpSyntaxTree.ParseText(content)`
2. Walk the syntax tree; collect nodes of type:
   - `MethodDeclarationSyntax`
   - `ConstructorDeclarationSyntax`
   - `PropertyDeclarationSyntax`
   - `ClassDeclarationSyntax`
   - `InterfaceDeclarationSyntax`
   - `StructDeclarationSyntax`
   - `EnumDeclarationSyntax`
3. For each node:
   - `text = node.ToFullString()`
   - `symbolName = node.Identifier.Text` (or class/interface name)
   - `symbolType` = mapped from node type to `SymbolType` enum
   - `lineSpan = node.GetLocation().GetLineSpan()`
   - `startLine = lineSpan.StartLinePosition.Line + 1`
   - `endLine   = lineSpan.EndLinePosition.Line + 1`
4. Build `ChunkMetadata` from `baseMetadata` using `with` expression, overriding `SymbolName`, `SymbolType`, `StartLine`, `EndLine`
5. Return `CodeChunk(text, metadata)` for each

Nested types: collect all qualifying nodes regardless of nesting depth. Inner class members are chunked independently.

### `Chunking/PlainTextChunker.cs`
Implements `IChunker`. Fallback for all non-.cs files.

**`CanHandle(filePath)`**
Always returns `true`.

**`Chunk(content, baseMetadata)`**
Configuration (from `IOptions<ChunkingOptions>`):
- `ChunkSize`: 400 tokens (approximate — use word count / 0.75 as token estimate)
- `Overlap`: 80 tokens

Split content into overlapping windows. For each window:
- `chunkIndex` = window index (0-based)
- `startLine` / `endLine` = approximate line numbers derived from character offset
- `SymbolName = null`, `SymbolType = SymbolType.None`
- Return `CodeChunk(windowText, metadata)`

### `Chunking/ChunkingOptions.cs`
```csharp
public sealed class ChunkingOptions
{
    public int ChunkSize { get; init; } = 400;
    public int Overlap   { get; init; } = 80;
}
```

Add `ChunkingOptions Chunking { get; init; } = new();` to `SourceRagOptions`.

---

## Embedding

### `Embedding/Local/LlamaSharpEmbeddingProvider.cs`
Implements `IEmbeddingProvider`.

Constructor injects:
- `IOptions<SourceRagOptions>`
- `ILogger<LlamaSharpEmbeddingProvider>`

Initialise `LLamaWeights` and `LLamaEmbedder` lazily from `options.LlamaSharp.ModelPath` on first call to `EmbedAsync`.

**`Dimensions`**
Return embedding dimension from model metadata after lazy init. Default: 768.

**`EmbedAsync(text, ct)`**
Call `embedder.GetEmbeddingsAsync(text)`. Return as `float[]`.

Dispose `LLamaWeights` and `LLamaEmbedder` on service disposal — implement `IAsyncDisposable`.

### `Embedding/Api/AnthropicEmbeddingProvider.cs`
Implements `IEmbeddingProvider`.

Use `Anthropic.SDK` or direct `HttpClient` to the Anthropic embeddings endpoint. API key from `ANTHROPIC_API_KEY` environment variable.

**`Dimensions`** — return 1536 (Voyage-based models).

**`EmbedAsync(text, ct)`**
POST to embeddings endpoint. Parse response, return `float[]`.

---

## Vector Store

### `VectorStore/QdrantVectorStore.cs`
Implements `IVectorStore`.

Constructor injects:
- `QdrantClient` (registered by DI, see SPEC-005)
- `IOptions<SourceRagOptions>`

**`EnsureCollectionAsync(dimensions, ct)`**
Check if collection `options.Qdrant.CollectionName` exists. If not, create with `VectorParams { Size = (ulong)dimensions, Distance = Distance.Cosine }`.

**`UpsertAsync(pointId, vector, metadata, ct)`**
Build `PointStruct` with:
- `Id = new PointId { Uuid = pointId.ToString() }`
- `Vectors = new Vectors { Vector = new Vector { Data = { vector } } }`
- `Payload`: map all `ChunkMetadata` fields to Qdrant payload values

Call `client.UpsertAsync(collectionName, new[] { point }, ct)`.

**`SearchAsync(queryVector, topK, ct)`**
Call `client.SearchAsync(collectionName, queryVector, limit: (ulong)topK, ct: ct)`.
Map `ScoredPoint` results back to `ScoredChunk`:
- Reconstruct `ChunkMetadata` from payload
- `CodeChunk.Text = string.Empty` — content is not stored in Qdrant (ADR-002), will be fetched from VCS in the query handler

**`DeleteAsync(pointId, ct)`**
Call `client.DeleteAsync(collectionName, new[] { new PointId { Uuid = pointId.ToString() } }, ct)`.

**`CountAsync(ct)`**
Call `client.CountAsync(collectionName, ct)`. Return `(int)result.Count`.

#### Metadata payload mapping

All `ChunkMetadata` fields stored as Qdrant payload key-value pairs:

| ChunkMetadata field | Qdrant payload key |
|---|---|
| `FilePath` | `file_path` |
| `Revision` | `revision` |
| `Author` | `author` |
| `CommitMessage` | `commit_message` (truncate to 500 chars) |
| `Timestamp` | `timestamp` (ISO 8601 string) |
| `Branch` | `branch` |
| `SymbolName` | `symbol_name` (omit if null) |
| `SymbolType` | `symbol_type` (enum name as string) |
| `StartLine` | `start_line` |
| `EndLine` | `end_line` |

---

## LLM Provider

### `Llm/AnthropicLlmProvider.cs`
Implements `ILlmProvider` (defined in Domain, SPEC-002).

Use `Anthropic.SDK`. Model from `options.Anthropic.Model`.

**`CompleteAsync(systemPrompt, userMessage, ct)`**
Build a messages request with `system = systemPrompt`, `user = userMessage`. Return `response.Content[0].Text`.

---

## Tests — `tests/SourceRAG.Infrastructure.Tests`

### `Chunking/RoslynChunkerTests.cs`
- `CanHandle_CsFile_ReturnsTrue`
- `CanHandle_TxtFile_ReturnsFalse`
- `Chunk_SimpleClass_ReturnsClassChunk`
- `Chunk_MethodInsideClass_ReturnsMethodChunk`
- `Chunk_EmptyFile_ReturnsEmpty`
- `Chunk_MetadataSymbolNameMatchesSyntaxNodeIdentifier`

### `Chunking/PlainTextChunkerTests.cs`
- `CanHandle_AnyFile_ReturnsTrue`
- `Chunk_ShortContent_ReturnsSingleChunk`
- `Chunk_LongContent_ReturnsMultipleChunksWithOverlap`
