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
