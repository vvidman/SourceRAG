# ADR-003 — Chunking: Roslyn Primary, PlainText Fallback (Chain of Responsibility)

## Status
Accepted

## Date
2025-04-13

## Context

Chunking strategy determines the semantic quality of retrieved results. Two broad approaches exist:

1. **Sliding window / fixed-size chunking** — language-agnostic, simple, but splits chunks at arbitrary positions that may bisect a function body or class definition
2. **Syntax-aware chunking** — uses a language parser to split at symbol boundaries (method, class, property), producing chunks that align with meaningful code units

SourceRAG's primary target is C# codebases. Roslyn (Microsoft.CodeAnalysis.CSharp) is the canonical C# parser, available as a NuGet package, and provides full syntax tree access without requiring an external process or language server.

For non-C# files (configuration, SQL scripts, XML, plain text documentation), Roslyn is not applicable. A fallback chunker is needed that handles arbitrary text.

Multiple chunkers must coexist. The selection logic should be extensible — new language-specific chunkers (e.g. Tree-sitter for C++ or Python) should be addable without modifying existing chunkers.

## Decision

Chunking is implemented using the **Chain of Responsibility** pattern.

All chunkers implement `IChunker`:

```csharp
public interface IChunker
{
    bool CanHandle(string filePath);
    IReadOnlyList<CodeChunk> Chunk(string content, ChunkMetadata baseMetadata);
}
```

Chunkers are registered as `IEnumerable<IChunker>` in DI. The pipeline selects the **first** chunker where `CanHandle(filePath) == true`. Registration order determines priority.

**Registration order:**
1. `RoslynChunker` — `CanHandle`: returns `true` for `*.cs` files
2. `PlainTextChunker` — `CanHandle`: returns `true` for all files (fallback)

**RoslynChunker behaviour:**
- Parses the file into a Roslyn `SyntaxTree`
- Extracts top-level type members: `MethodDeclaration`, `PropertyDeclaration`, `ConstructorDeclaration`, `ClassDeclaration`, `InterfaceDeclaration`
- Each symbol becomes one chunk
- `ChunkMetadata.SymbolName` and `SymbolType` are populated from the syntax node
- Line range is derived from `GetLineSpan()`

**PlainTextChunker behaviour:**
- Sliding window with configurable `ChunkSize` (default: 400 tokens) and `Overlap` (default: 80 tokens)
- `ChunkMetadata.SymbolName` is `null`; `SymbolType` is `None`

## Consequences

**Positive**
- C# code is chunked at semantically meaningful boundaries — retrieval quality is higher
- New language chunkers can be added by implementing `IChunker` and registering before `PlainTextChunker` — no existing code modified (Open/Closed Principle)
- Plain text fallback ensures no file type is silently skipped

**Negative**
- Roslyn parses the full syntax tree per file — memory overhead for very large files (mitigated by processing files sequentially, not in parallel by default)
- Nested types (classes within classes) produce overlapping symbol scopes; inner type members are chunked independently, which may lose outer context

## Alternatives Considered

**Single sliding window chunker for all files** — rejected; bisects method bodies, produces lower quality retrieval for C# code.

**Tree-sitter for all languages** — considered for v2; requires native bindings and adds complexity. Deferred.

**One chunker per language with explicit mapping** — rejected in favour of Chain of Responsibility; a mapping table is less extensible and requires modification when new languages are added.
