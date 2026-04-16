# FIX-006 — LLM Provider: Independently Configurable + OpenAI-Compatible + Auto Template Detection

## Overview

Three changes in one fix:
1. `LlmProvider` becomes an independent configuration key (decoupled from `EmbeddingProvider`)
2. Cloud: `OpenAiCompatible` generic provider added alongside existing `Anthropic` native provider
3. Local: GGUF prompt template auto-detected from model metadata — no manual template config required

---

## ADR-012

Create `docs/adr/ADR-012-llm-provider.md`:

```markdown
# ADR-012 — LLM Provider: Independently Configurable

## Status
Accepted

## Date
2026-04-16

## Context

The original implementation hardcoded `AnthropicLlmProvider` as `ILlmProvider` regardless of
`EmbeddingProvider`. This prevents air-gapped or cost-sensitive deployments from using a local
or alternative cloud LLM for chat completion.

Three provider types are needed:
- `Anthropic` — native Anthropic SDK, full feature support
- `OpenAiCompatible` — generic OpenAI-compatible endpoint; covers Groq, Together AI,
  Mistral, Azure OpenAI, OpenAI, and any compatible host with a single implementation
- `Local` — LlamaSharp with any GGUF model; prompt template auto-detected from GGUF metadata

## Decision

`LlmProvider` is a separate config key, independent of `EmbeddingProvider`.
Valid values: `"Anthropic"` (default) | `"OpenAiCompatible"` | `"Local"`

For `Local`, prompt template is auto-detected from the GGUF file's `tokenizer.chat_template`
metadata field via `LLamaTemplate`. No manual template configuration is required or exposed.
If the field is absent, falls back to ChatML with a logged warning.

For `OpenAiCompatible`, `BaseUrl`, `Model`, and API key (env var `SOURCERAG_LLM_API_KEY`)
are configured explicitly.

## Consequences

**Positive**
- Air-gapped deployments: Local embedding + Local LLM — zero outbound network calls
- Groq/Together/Mistral support via one implementation, not per-provider slices
- GGUF auto-detection removes a common misconfiguration failure mode
- Consistent vertical slice pattern with embedding provider (ADR-004)

**Negative**
- Auto-detection fails silently if the GGUF file lacks `tokenizer.chat_template` metadata
  (mitigated: log a warning, fall back to ChatML format which is widely supported)
- OpenAI-compatible provider cannot use Anthropic-specific features (extended thinking, etc.)
```

---

## Changes Required

### 1. Application — `SourceRagOptions.cs`

```csharp
public sealed class SourceRagOptions
{
    public const string SectionName = "SourceRAG";

    public required string VcsProvider        { get; init; }
    public required string EmbeddingProvider  { get; init; }
    public string LlmProvider                 { get; init; } = "Anthropic"; // NEW
    public required string RepositoryPath     { get; init; }
    public string Branch                      { get; init; } = "main";
    public QdrantOptions Qdrant               { get; init; } = new();
    public LlamaSharpOptions LlamaSharp       { get; init; } = new();
    public AnthropicOptions Anthropic         { get; init; } = new();
    public OpenAiCompatibleOptions OpenAiCompatible { get; init; } = new(); // NEW
    public AzureAdOptions AzureAd             { get; init; } = new();
    public ChunkingOptions Chunking           { get; init; } = new();
}

public sealed class LlamaSharpOptions
{
    public string ModelPath    { get; init; } = string.Empty; // embedding model
    public string LlmModelPath { get; init; } = string.Empty; // chat model (NEW)
}

// NEW
public sealed class OpenAiCompatibleOptions
{
    /// <summary>
    /// Base URL of the OpenAI-compatible API.
    /// Examples:
    ///   Groq:       https://api.groq.com/openai/v1
    ///   Together:   https://api.together.xyz/v1
    ///   Mistral:    https://api.mistral.ai/v1
    ///   Azure OAI:  https://{resource}.openai.azure.com/openai/deployments/{deployment}
    ///   OpenAI:     https://api.openai.com/v1
    /// </summary>
    public string BaseUrl { get; init; } = string.Empty;
    public string Model   { get; init; } = string.Empty;
    // API key read from env var SOURCERAG_LLM_API_KEY at runtime
}
```

---

### 2. Infrastructure folder restructure

```
src/SourceRAG.Infrastructure/Llm/
  Api/
    AnthropicLlmProvider.cs        ← moved from Llm/ (namespace update only)
    OpenAiCompatibleLlmProvider.cs ← NEW
  Local/
    LlamaSharpLlmProvider.cs       ← NEW
```

---

### 3. Infrastructure — `Llm/Local/LlamaSharpLlmProvider.cs` (new file)

Auto-detects prompt template from GGUF `tokenizer.chat_template` metadata via `LLamaTemplate`.
Falls back to ChatML if metadata is absent.

```csharp
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.Llm.Local;

public sealed class LlamaSharpLlmProvider : ILlmProvider, IAsyncDisposable
{
    private readonly LlamaSharpOptions _options;
    private readonly ILogger<LlamaSharpLlmProvider> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private LLamaWeights? _weights;
    private bool          _initialized;

    public LlamaSharpLlmProvider(
        IOptions<SourceRagOptions> options,
        ILogger<LlamaSharpLlmProvider> logger)
    {
        _options = options.Value.LlamaSharp;
        _logger  = logger;

        if (string.IsNullOrWhiteSpace(_options.LlmModelPath))
            throw new InvalidOperationException(
                "SourceRAG:LlamaSharp:LlmModelPath is required when LlmProvider is 'Local'.");
    }

    public async Task<string> CompleteAsync(
        string systemPrompt, string userMessage, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        var prompt   = BuildPrompt(systemPrompt, userMessage);
        var executor = new StatelessExecutor(_weights!, new ModelParams(_options.LlmModelPath));
        var inferParams = new InferenceParams
        {
            MaxTokens        = 2048,
            SamplingPipeline = new DefaultSamplingPipeline()
        };

        var sb = new System.Text.StringBuilder();
        await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
            sb.Append(token);

        return sb.ToString().Trim();
    }

    public async ValueTask DisposeAsync()
    {
        _weights?.Dispose();
        _initLock.Dispose();
        await ValueTask.CompletedTask;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            _logger.LogInformation(
                "Loading LlamaSharp LLM model from {Path}", _options.LlmModelPath);
            _weights     = LLamaWeights.LoadFromFile(new ModelParams(_options.LlmModelPath));
            _initialized = true;

            // Log whether auto-detected template is present
            var hasTemplate = _weights.Metadata.ContainsKey("tokenizer.chat_template");
            if (hasTemplate)
                _logger.LogInformation("GGUF tokenizer.chat_template detected — using model-native prompt format.");
            else
                _logger.LogWarning(
                    "No tokenizer.chat_template in GGUF metadata. " +
                    "Falling back to ChatML. If responses are malformed, " +
                    "verify the model supports ChatML (most instruction-tuned models do).");
        }
        finally { _initLock.Release(); }
    }

    private string BuildPrompt(string systemPrompt, string userMessage)
    {
        // LLamaTemplate reads tokenizer.chat_template from GGUF metadata automatically.
        // If not present, LlamaSharp falls back to ChatML.
        var template = new LLamaTemplate(_weights!.Metadata);
        template.Add("system", systemPrompt);
        template.Add("user",   userMessage);
        return template.Apply();
    }
}
```

> **LLamaTemplate API note:** Verify `LLamaTemplate` constructor signature against the pinned
> LlamaSharp version. The class reads `tokenizer.chat_template` (Jinja2) from GGUF metadata —
> this is a stable standard field present in all major instruction-tuned models since mid-2024
> (Llama 3, Mistral v0.3, Phi-3, Gemma 2, Qwen2, etc.).

---

### 4. Infrastructure — `Llm/Api/OpenAiCompatibleLlmProvider.cs` (new file)

Single implementation covering Groq, Together AI, Mistral, Azure OpenAI, OpenAI, and any
OpenAI-compatible `/chat/completions` endpoint.

```csharp
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.Llm.Api;

public sealed class OpenAiCompatibleLlmProvider : ILlmProvider
{
    private const string ApiKeyEnvVar = "SOURCERAG_LLM_API_KEY";

    private readonly OpenAiCompatibleOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiCompatibleLlmProvider> _logger;

    public OpenAiCompatibleLlmProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<SourceRagOptions> options,
        ILogger<OpenAiCompatibleLlmProvider> logger)
    {
        _options = options.Value.OpenAiCompatible;
        _logger  = logger;

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvVar)
            ?? throw new InvalidOperationException(
                $"Environment variable {ApiKeyEnvVar} is required when LlmProvider is 'OpenAiCompatible'.");

        _httpClient = httpClientFactory.CreateClient("OpenAiCompatibleLlm");
        _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public async Task<string> CompleteAsync(
        string systemPrompt, string userMessage, CancellationToken ct)
    {
        var requestBody = new
        {
            model    = _options.Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userMessage  }
            },
            max_tokens  = 4096,
            temperature = 0.2
        };

        using var response = await _httpClient.PostAsJsonAsync(
            "chat/completions", requestBody, ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(ct)
            ?? throw new InvalidOperationException("Empty response from LLM API.");

        return result.Choices[0].Message.Content;
    }

    // ── response DTOs ────────────────────────────────────────────────────────

    private sealed record OpenAiChatResponse(
        [property: JsonPropertyName("choices")] OpenAiChoice[] Choices);

    private sealed record OpenAiChoice(
        [property: JsonPropertyName("message")] OpenAiMessage Message);

    private sealed record OpenAiMessage(
        [property: JsonPropertyName("content")] string Content);
}
```

---

### 5. Infrastructure — `AnthropicLlmProvider.cs`

Move file to `Llm/Api/`. Update namespace:

```csharp
// BEFORE: namespace SourceRAG.Infrastructure.Llm;
// AFTER:
namespace SourceRAG.Infrastructure.Llm.Api;
```

No other changes to the file.

---

### 6. Infrastructure — `InfrastructureServiceExtensions.cs`

```csharp
// In AddInfrastructure, replace:
services.AddSingleton<ILlmProvider, AnthropicLlmProvider>();

// With:
services.AddLlmProvider(opts.LlmProvider);
```

```csharp
private static IServiceCollection AddLlmProvider(
    this IServiceCollection services, string providerType)
{
    switch (providerType)
    {
        case "Anthropic":
            services.AddSingleton<ILlmProvider, AnthropicLlmProvider>();
            break;

        case "OpenAiCompatible":
            services.AddHttpClient("OpenAiCompatibleLlm");
            services.AddSingleton<ILlmProvider, OpenAiCompatibleLlmProvider>();
            break;

        case "Local":
            services.AddSingleton<ILlmProvider, LlamaSharpLlmProvider>();
            break;

        default:
            throw new InvalidOperationException(
                $"Unknown LlmProvider '{providerType}'. " +
                "Valid values: Anthropic, OpenAiCompatible, Local.");
    }

    return services;
}
```

Update `using` directives to include the new namespaces:
```csharp
using SourceRAG.Infrastructure.Llm.Api;
using SourceRAG.Infrastructure.Llm.Local;
```

---

### 7. Validation — `ValidateOptions`

```csharp
if (opts.LlmProvider is not ("Anthropic" or "OpenAiCompatible" or "Local"))
    throw new InvalidOperationException(
        $"SourceRAG:LlmProvider '{opts.LlmProvider}' is invalid. " +
        "Valid values: Anthropic, OpenAiCompatible, Local.");

if (opts.LlmProvider == "Local" &&
    string.IsNullOrWhiteSpace(opts.LlamaSharp.LlmModelPath))
    throw new InvalidOperationException(
        "SourceRAG:LlamaSharp:LlmModelPath is required when LlmProvider is 'Local'.");

if (opts.LlmProvider == "Anthropic" &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
    throw new InvalidOperationException(
        "Environment variable ANTHROPIC_API_KEY is required when LlmProvider is 'Anthropic'.");

if (opts.LlmProvider == "OpenAiCompatible")
{
    if (string.IsNullOrWhiteSpace(opts.OpenAiCompatible.BaseUrl))
        throw new InvalidOperationException(
            "SourceRAG:OpenAiCompatible:BaseUrl is required when LlmProvider is 'OpenAiCompatible'.");

    if (string.IsNullOrWhiteSpace(opts.OpenAiCompatible.Model))
        throw new InvalidOperationException(
            "SourceRAG:OpenAiCompatible:Model is required when LlmProvider is 'OpenAiCompatible'.");

    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOURCERAG_LLM_API_KEY")))
        throw new InvalidOperationException(
            "Environment variable SOURCERAG_LLM_API_KEY is required when LlmProvider is 'OpenAiCompatible'.");
}
```

---

### 8. Configuration — `appsettings.json` (both hosts)

```json
{
  "SourceRAG": {
    "VcsProvider":       "Git",
    "EmbeddingProvider": "Local",
    "LlmProvider":       "Anthropic",

    "LlamaSharp": {
      "ModelPath":    "/models/nomic-embed-text.gguf",
      "LlmModelPath": ""
    },

    "OpenAiCompatible": {
      "BaseUrl": "",
      "Model":   ""
    }
  }
}
```

#### Provider combination examples

**Air-gapped (fully local):**
```json
{
  "EmbeddingProvider": "Local",
  "LlmProvider": "Local",
  "LlamaSharp": {
    "ModelPath":    "/models/nomic-embed-text.gguf",
    "LlmModelPath": "/models/llama-3.2-3b-instruct.Q4_K_M.gguf"
  }
}
```

**Groq:**
```json
{
  "LlmProvider": "OpenAiCompatible",
  "OpenAiCompatible": {
    "BaseUrl": "https://api.groq.com/openai/v1",
    "Model":   "llama-3.3-70b-versatile"
  }
}
// env: SOURCERAG_LLM_API_KEY=gsk_...
```

**Together AI:**
```json
{
  "LlmProvider": "OpenAiCompatible",
  "OpenAiCompatible": {
    "BaseUrl": "https://api.together.xyz/v1",
    "Model":   "meta-llama/Llama-3.3-70B-Instruct-Turbo"
  }
}
```

**Mistral:**
```json
{
  "LlmProvider": "OpenAiCompatible",
  "OpenAiCompatible": {
    "BaseUrl": "https://api.mistral.ai/v1",
    "Model":   "mistral-large-latest"
  }
}
```

**Azure OpenAI:**
```json
{
  "LlmProvider": "OpenAiCompatible",
  "OpenAiCompatible": {
    "BaseUrl": "https://{resource}.openai.azure.com/openai/deployments/{deployment}",
    "Model":   "gpt-4o"
  }
}
```

---

### 9. Environment variables — complete reference

| Variable | Used by |
|---|---|
| `ANTHROPIC_API_KEY` | `AnthropicEmbeddingProvider` + `AnthropicLlmProvider` |
| `SOURCERAG_LLM_API_KEY` | `OpenAiCompatibleLlmProvider` (Groq, Together, Mistral, OpenAI, Azure OAI) |
| `SOURCERAG_GIT_PAT` | `EnvironmentVcsCredentialProvider` |
| `SOURCERAG_GIT_SSH_KEY_PATH` | `EnvironmentVcsCredentialProvider` |
| `SOURCERAG_GIT_SSH_PASSPHRASE` | `EnvironmentVcsCredentialProvider` |
| `SOURCERAG_SVN_USERNAME` | `EnvironmentVcsCredentialProvider` |
| `SOURCERAG_SVN_PASSWORD` | `EnvironmentVcsCredentialProvider` |

---

## Acceptance Criteria

- [ ] `SourceRagOptions.LlmProvider` exists, defaults to `"Anthropic"`
- [ ] `LlamaSharpOptions.LlmModelPath` exists (separate from embedding `ModelPath`)
- [ ] `OpenAiCompatibleOptions` exists with `BaseUrl` and `Model`
- [ ] `LlamaSharpLlmProvider` reads `tokenizer.chat_template` from GGUF metadata via `LLamaTemplate`
- [ ] `LlamaSharpLlmProvider` logs a warning and continues when no template found in metadata
- [ ] `OpenAiCompatibleLlmProvider` works against Groq with `llama-3.3-70b-versatile`
- [ ] `AnthropicLlmProvider` moved to `Llm/Api/`, namespace updated, no logic changes
- [ ] `AddLlmProvider` switch covers all three providers
- [ ] Validation covers all three providers with descriptive error messages
- [ ] Fully local deployment (Local embedding + Local LLM) produces zero outbound HTTP calls
- [ ] ADR-012 created in `docs/adr/`
- [ ] Solution builds without errors
