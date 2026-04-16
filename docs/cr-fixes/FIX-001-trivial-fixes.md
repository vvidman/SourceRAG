# FIX-001 — Trivial Fixes (3 items)

## Scope
Three independent, low-risk fixes that can be applied in a single pass.

---

## Fix A — PlainTextChunker: whitespace-only split

**File:** `src/SourceRAG.Infrastructure/Chunking/PlainTextChunker.cs`

**Problem:** `content.Split(' ')` splits only on space characters. Source code is newline-delimited, not space-delimited. A file with no spaces but many newlines produces a single enormous "word", defeating the sliding window entirely.

**Change:** Replace the split call.

```csharp
// BEFORE
var words = content.Split(' ', StringSplitOptions.None);

// AFTER
var words = content.Split(
    new[] { ' ', '\n', '\r', '\t' },
    StringSplitOptions.RemoveEmptyEntries);
```

Also update `CharOffsetOfWord` — the helper currently assumes a single space separator between words. After switching to multi-character split with `RemoveEmptyEntries`, the character offset reconstruction becomes incorrect. Replace the method with a scan-based approach:

```csharp
private static int CharOffsetOfWord(string content, int wordIndex, string[] words)
{
    if (wordIndex == 0) return 0;
    var pos = 0;
    var found = 0;
    while (pos < content.Length && found < wordIndex)
    {
        // skip non-whitespace (current word)
        while (pos < content.Length && !char.IsWhiteSpace(content[pos])) pos++;
        // skip whitespace (separator)
        while (pos < content.Length && char.IsWhiteSpace(content[pos])) pos++;
        found++;
    }
    return Math.Min(pos, content.Length);
}
```

**Test to add** in `tests/SourceRAG.Infrastructure.Tests/Chunking/PlainTextChunkerTests.cs`:
```csharp
[Fact]
public void Chunk_NewlineDelimitedContent_ProducesMultipleChunks()
{
    var content = string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"word{i}"));
    var chunks = _sut.Chunk(content, BaseMetadata());
    Assert.True(chunks.Count > 1, "Newline-delimited content should produce multiple chunks");
}
```

---

## Fix B — AnthropicClient and API key: cache at constructor level

**Files:**
- `src/SourceRAG.Infrastructure/Llm/AnthropicLlmProvider.cs`
- `src/SourceRAG.Infrastructure/Embedding/Api/AnthropicEmbeddingProvider.cs`

**Problem:** Both classes read `ANTHROPIC_API_KEY` from the environment and construct a new client instance on every method call. Both are registered as singletons — the client should be constructed once.

### AnthropicLlmProvider.cs

```csharp
// BEFORE — inside CompleteAsync:
var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");
var client = new AnthropicClient(new APIAuthentication(apiKey));

// AFTER — field + constructor:
private readonly AnthropicClient _client;

public AnthropicLlmProvider(IOptions<SourceRagOptions> options)
{
    _options = options.Value.Anthropic;

    var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
        ?? throw new InvalidOperationException(
            "ANTHROPIC_API_KEY environment variable is not set. " +
            "Set it before starting the application.");

    _client = new AnthropicClient(new APIAuthentication(apiKey));
}

// CompleteAsync uses _client directly — no per-call construction
```

### AnthropicEmbeddingProvider.cs

```csharp
// BEFORE — inside EmbedAsync:
var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ...
using var client = _httpClientFactory.CreateClient();
client.DefaultRequestHeaders.Add("x-api-key", apiKey);
client.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersionHeader);

// AFTER — create a named HttpClient at registration time.
// Change the constructor to accept a named HttpClient:
public AnthropicEmbeddingProvider(IHttpClientFactory httpClientFactory)
{
    _httpClient = httpClientFactory.CreateClient("AnthropicEmbedding");
}
private readonly HttpClient _httpClient;

// In InfrastructureServiceExtensions.AddEmbeddingProvider for "Api":
services.AddHttpClient("AnthropicEmbedding", client =>
{
    var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
        ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");
    client.DefaultRequestHeaders.Add("x-api-key", apiKey);
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
});
services.AddSingleton<IEmbeddingProvider, AnthropicEmbeddingProvider>();

// EmbedAsync uses _httpClient directly — no per-call header manipulation.
// Remove 'using var client' and the header-setting lines.
```

---

## Fix C — Pin NuGet package versions

**File:** `src/SourceRAG.Infrastructure/SourceRAG.Infrastructure.csproj`

**Problem:** Wildcard `Version="*"` prevents reproducible builds. `LlamaSharp` and `Qdrant.Client` ship breaking changes in minor versions.

**Action:** Replace all `Version="*"` with pinned versions. Use the currently restored versions as the baseline (run `dotnet restore` and check `.deps.json` or `project.assets.json` for the actual resolved versions).

Typical pinned versions as of the project's net10.0 target:

```xml
<ItemGroup>
  <PackageReference Include="Anthropic.SDK"                                   Version="3.9.1" />
  <PackageReference Include="LibGit2Sharp"                                    Version="0.31.0" />
  <PackageReference Include="LlamaSharp"                                      Version="0.17.2" />
  <PackageReference Include="Microsoft.CodeAnalysis.CSharp"                   Version="4.14.0" />
  <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
  <PackageReference Include="Microsoft.Extensions.Http"                       Version="10.0.0" />
  <PackageReference Include="Qdrant.Client"                                   Version="1.13.0" />
  <PackageReference Include="SharpSvn"                                        Version="1.14005.390" />
  <PackageReference Include="Microsoft.Extensions.Options"                    Version="10.0.0" />
  <PackageReference Include="Microsoft.Extensions.Logging.Abstractions"       Version="10.0.0" />
</ItemGroup>
```

**Verify:** Run `dotnet build` after pinning to confirm no version conflicts.
