# SPEC-001 — Domain Layer

## Overview
Implement the complete Domain layer: all entities, interfaces, and enums. This is the foundation — no other layer may be touched until this spec is complete and tests pass.

## References
- ADR-001 (VCS provider+strategy pairing)
- ADR-002 (VCS as proof store)
- ADR-003 (chunking chain of responsibility)
- ADR-004 (embedding provider)
- ADR-006 (Qdrant point ID)
- ADR-010 (VCS credential resolution)

## Project
`src/SourceRAG.Domain`

## Rules
- No NuGet package references. `System.*` only.
- All entities are immutable `record` types.
- All interfaces use `CancellationToken ct` on async methods.
- Delete `Class1.cs`.

---

## Enums

### `Enums/SymbolType.cs`
```csharp
public enum SymbolType { None, Class, Interface, Struct, Method, Constructor, Property, Field, Enum }
```

### `Enums/VcsProviderType.cs`
```csharp
public enum VcsProviderType { Git, Svn }
```

### `Enums/EmbeddingProviderType.cs`
```csharp
public enum EmbeddingProviderType { Local, Api }
```

### `Enums/ChangeType.cs`
```csharp
public enum ChangeType { Added, Modified, Deleted, Renamed }
```

---

## Entities

### `Entities/ChunkMetadata.cs`
```csharp
public sealed record ChunkMetadata
{
    public required string FilePath        { get; init; }
    public required string Revision        { get; init; }
    public required string Author          { get; init; }
    public required string CommitMessage   { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Branch          { get; init; }
    public string? SymbolName              { get; init; }
    public SymbolType SymbolType           { get; init; } = SymbolType.None;
    public int StartLine                   { get; init; }
    public int EndLine                     { get; init; }
}
```

### `Entities/CodeChunk.cs`
```csharp
public sealed record CodeChunk(string Text, ChunkMetadata Metadata);
```

### `Entities/ScoredChunk.cs`
```csharp
public sealed record ScoredChunk(CodeChunk Chunk, float Score);
```

### `Entities/QueryResult.cs`
```csharp
public sealed record QueryResult(string Answer, IReadOnlyList<ScoredChunk> Chunks);
```

### `Entities/VcsFile.cs`
```csharp
public sealed record VcsFile(string Path, string Revision);
```

### `Entities/ChangedFile.cs`
```csharp
public sealed record ChangedFile(string Path, ChangeType ChangeType);
```

### `Entities/FileBlameInfo.cs`
```csharp
public sealed record FileBlameInfo
{
    public required string FilePath      { get; init; }
    public required string Revision      { get; init; }
    public required string Author        { get; init; }
    public required string CommitMessage { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
```

### `Entities/ReindexScope.cs`
```csharp
public sealed record ReindexScope(
    IReadOnlyList<ChangedFile> ChangedFiles,
    string FromRevision,
    string ToRevision);
```

### `Entities/IndexStatus.cs`
```csharp
public sealed record IndexStatus
{
    public string? LastIndexedRevision { get; init; }
    public int ChunkCount              { get; init; }
    public DateTimeOffset? LastIndexedAt { get; init; }
    public bool IsIndexing             { get; init; }
}
```

### `Entities/VcsCredential.cs`
Discriminated union hierarchy (ADR-010):
```csharp
public abstract record VcsCredential;
public sealed record NoCredential                                              : VcsCredential;
public sealed record PatCredential(string Pat)                                 : VcsCredential;
public sealed record UserPasswordCredential(string Username, string Password)  : VcsCredential;
public sealed record SshCredential(string KeyPath, string? Passphrase)        : VcsCredential;
```

---

## Interfaces

### `Interfaces/IVcsProvider.cs`
```csharp
public interface IVcsProvider
{
    string GetCurrentRevision(string repoPath);
    Task<IReadOnlyList<VcsFile>> GetFilesAtHeadAsync(string repoPath, CancellationToken ct);
    Task<string> GetFileContentAsync(string repoPath, string filePath, string revision, CancellationToken ct);
    Task<FileBlameInfo> GetBlameAsync(string repoPath, string filePath, string revision, CancellationToken ct);
    Task<IReadOnlyList<ChangedFile>> GetChangedFilesSinceAsync(string repoPath, string sinceRevision, CancellationToken ct);
}
```

### `Interfaces/IReindexStrategy.cs`
```csharp
public interface IReindexStrategy
{
    Task<ReindexScope> DetermineChangedFilesAsync(string repoPath, string lastIndexedRevision, CancellationToken ct);
}
```

### `Interfaces/IEmbeddingProvider.cs`
```csharp
public interface IEmbeddingProvider
{
    int Dimensions { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}
```

### `Interfaces/IChunker.cs`
```csharp
public interface IChunker
{
    bool CanHandle(string filePath);
    IReadOnlyList<CodeChunk> Chunk(string content, ChunkMetadata baseMetadata);
}
```

### `Interfaces/IVectorStore.cs`
```csharp
public interface IVectorStore
{
    Task EnsureCollectionAsync(int dimensions, CancellationToken ct);
    Task UpsertAsync(Guid pointId, float[] vector, ChunkMetadata metadata, CancellationToken ct);
    Task<IReadOnlyList<ScoredChunk>> SearchAsync(float[] queryVector, int topK, CancellationToken ct);
    Task DeleteAsync(Guid pointId, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}
```

### `Interfaces/IVcsCredentialProvider.cs`
```csharp
public interface IVcsCredentialProvider
{
    VcsCredential Resolve(VcsProviderType providerType);
}
```

---

## Tests — `tests/SourceRAG.Domain.Tests`

Delete `UnitTest1.cs`. Create:

### `ChunkMetadataTests.cs`
- `WithExpression_ProducesNewInstance_OriginalUnchanged`
- `SymbolType_DefaultsToNone`
- `SymbolName_DefaultsToNull`

### `VcsCredentialTests.cs`
- Pattern matching exhaustiveness: switch expression over all four subtypes compiles and returns expected string
- `NoCredential_IsSubtypeOfVcsCredential`
- `PatCredential_ExposesPatProperty`
- `SshCredential_PassphraseIsNullable`

### `IndexStatusTests.cs`
- `NeverIndexed_LastIndexedRevisionIsNull`
- `NeverIndexed_LastIndexedAtIsNull`
- `IsIndexing_DefaultsFalse`
