# ADR-004 — Embedding: Local (LlamaSharp) vs API — Config-Driven, No Code Change

## Status
Accepted

## Date
2025-04-13

## Context

Embedding generation is a core pipeline step, executed for every chunk during indexing and for every query at search time. Two deployment models are relevant:

**Local inference (LlamaSharp)**
- Runs a GGUF embedding model (e.g. `nomic-embed-text`) in-process
- No network dependency, no API cost, no data leaves the machine
- Required for air-gapped environments or sensitive codebases (e.g. proprietary medical device software)
- Inference speed depends on local hardware (CPU/GPU)

**API-based inference (Anthropic)**
- Delegates embedding generation to an external API endpoint
- No local model management; consistent quality
- Requires network access and API key
- Incurs per-token cost; unsuitable for large initial indexing runs without cost controls

Both providers must produce embeddings of a consistent dimensionality for a given Qdrant collection. Switching providers requires re-indexing the entire collection (vectors are not cross-provider compatible).

## Decision

Embedding provider is a **vertical slice** in `SourceRAG.Infrastructure/Embedding/`:

```
Embedding/
  Local/
    LlamaSharpEmbeddingProvider.cs    implements IEmbeddingProvider
  Api/
    AnthropicEmbeddingProvider.cs     implements IEmbeddingProvider
```

`IEmbeddingProvider` is defined in Domain:

```csharp
public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
    int Dimensions { get; }
}
```

The active provider is selected by the `EmbeddingProvider` configuration key (`"Local"` or `"Api"`). **No code change is required to switch providers** — only `appsettings.json` and a re-index run.

At application startup, `QdrantVectorStore` reads `IEmbeddingProvider.Dimensions` to create or validate the Qdrant collection with the correct vector size. A mismatch between the configured provider's dimensions and an existing collection is a startup error.

## Consequences

**Positive**
- Air-gapped / sensitive deployments use `Local` with zero data leaving the environment
- Development and cloud deployments can use `Api` without any code modification
- `Dimensions` property on the interface prevents silent vector size mismatches
- Both providers are independently testable with mock embeddings

**Negative**
- Switching providers invalidates the existing Qdrant collection — full reindex required
- `Local` provider requires manual GGUF model file management
- API provider requires secret management (`ANTHROPIC_API_KEY` environment variable)

## Model Recommendations

| Provider | Recommended Model | Dimensions |
|---|---|---|
| Local | `nomic-embed-text-v1.5.Q4_K_M.gguf` | 768 |
| Api | `voyage-code-2` (via Anthropic/Voyage) | 1536 |

## Alternatives Considered

**OpenAI embedding API** — viable alternative to Anthropic API; can be added as a third slice (`Api/OpenAiEmbeddingProvider.cs`) without architectural change. Out of scope for v1.

**Single provider hardcoded** — rejected; the Local vs API decision is deployment-context dependent and must not require code changes.

**Abstract factory pattern** — considered; rejected in favour of simple DI registration switch. The factory pattern adds indirection without benefit given that only one provider is active at a time.
