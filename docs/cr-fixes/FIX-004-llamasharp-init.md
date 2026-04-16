# FIX-004 — EnsureCollectionAsync: LlamaSharp Dimensions Before Model Load

## Problem

`SourceRAG.Api/Program.cs` calls `EnsureCollectionAsync(embeddingProvider.Dimensions)` at startup. For `LlamaSharpEmbeddingProvider`, the `Dimensions` property returns the hardcoded default `768` until the first `EmbedAsync` call triggers lazy initialisation. If the actual GGUF model has a different embedding size (e.g. 4096), the Qdrant collection is created with the wrong vector size. The first upsert then fails with a dimension mismatch error from Qdrant.

---

## Fix

Add an explicit `InitializeAsync()` method to `IEmbeddingProvider` so the host can force initialisation before creating the Qdrant collection.

### 1. Domain — `IEmbeddingProvider.cs`

```csharp
public interface IEmbeddingProvider
{
    int Dimensions { get; }

    /// <summary>
    /// Warms up the provider and ensures <see cref="Dimensions"/> reflects the
    /// actual model size. Implementations may be no-ops for API-based providers.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    Task InitializeAsync(CancellationToken ct);

    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}
```

### 2. Infrastructure — `LlamaSharpEmbeddingProvider.cs`

Expose `EnsureInitializedAsync` as the public `InitializeAsync` implementation:

```csharp
public Task InitializeAsync(CancellationToken ct) => EnsureInitializedAsync(ct);
```

The existing `EnsureInitializedAsync` private method already does the right thing — just make it accessible via the interface. No other changes needed.

### 3. Infrastructure — `AnthropicEmbeddingProvider.cs`

API-based provider is a no-op:

```csharp
public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
```

`Dimensions` for `AnthropicEmbeddingProvider` is a compile-time constant (`1536`) — no initialisation needed.

### 4. Host — `SourceRAG.Api/Program.cs`

Call `InitializeAsync` **before** `EnsureCollectionAsync`:

```csharp
// BEFORE:
using (var scope = app.Services.CreateScope())
{
    var vectorStore       = scope.ServiceProvider.GetRequiredService<IVectorStore>();
    var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
    await vectorStore.EnsureCollectionAsync(embeddingProvider.Dimensions, CancellationToken.None);
}

// AFTER:
using (var scope = app.Services.CreateScope())
{
    var vectorStore       = scope.ServiceProvider.GetRequiredService<IVectorStore>();
    var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();

    // Must initialise before reading Dimensions — LlamaSharp lazy-loads the model
    await embeddingProvider.InitializeAsync(CancellationToken.None);

    await vectorStore.EnsureCollectionAsync(embeddingProvider.Dimensions, CancellationToken.None);
}
```

### 5. Host — `SourceRAG.McpHost/Program.cs`

Apply the same pattern. The McpHost also creates an `IEmbeddingProvider` singleton via `AddInfrastructure`. Add the same startup block after `var app = builder.Build()`:

```csharp
// After app = builder.Build():
using (var scope = app.Services.CreateScope())
{
    var vectorStore       = scope.ServiceProvider.GetRequiredService<IVectorStore>();
    var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
    await embeddingProvider.InitializeAsync(CancellationToken.None);
    await vectorStore.EnsureCollectionAsync(embeddingProvider.Dimensions, CancellationToken.None);
}
```

---

## Test

Add to `tests/SourceRAG.Infrastructure.Tests/Embedding/` (new file):

### `LlamaSharpEmbeddingProviderInitTests.cs`

```csharp
// This test requires a real GGUF model file — skip in CI if model not present.
// Use [Fact(Skip = "Requires GGUF model")] or an environment variable guard.

public sealed class LlamaSharpEmbeddingProviderInitTests
{
    [Fact(Skip = "Requires local GGUF model — run manually")]
    public async Task InitializeAsync_SetsDimensionsFromModel()
    {
        var options = Options.Create(new SourceRagOptions
        {
            VcsProvider       = "Git",
            EmbeddingProvider = "Local",
            RepositoryPath    = "/tmp",
            LlamaSharp        = new LlamaSharpOptions { ModelPath = "/models/nomic-embed-text.gguf" }
        });

        await using var provider = new LlamaSharpEmbeddingProvider(options, NullLogger<LlamaSharpEmbeddingProvider>.Instance);

        Assert.Equal(768, provider.Dimensions); // default before init

        await provider.InitializeAsync(CancellationToken.None);

        Assert.True(provider.Dimensions > 0);
        Assert.NotEqual(0, provider.Dimensions);
    }
}
```

For CI: mock `IEmbeddingProvider` and verify `InitializeAsync` is called before `EnsureCollectionAsync`. This can be an integration test at the host level using `WebApplicationFactory`.

---

## Acceptance Criteria

- [ ] `IEmbeddingProvider` has `InitializeAsync(CancellationToken ct)`
- [ ] `LlamaSharpEmbeddingProvider.InitializeAsync` triggers model load
- [ ] `AnthropicEmbeddingProvider.InitializeAsync` is a no-op
- [ ] Both hosts call `InitializeAsync` before `EnsureCollectionAsync`
- [ ] `Dimensions` after `InitializeAsync` reflects the actual model embedding size
- [ ] Solution builds without errors
