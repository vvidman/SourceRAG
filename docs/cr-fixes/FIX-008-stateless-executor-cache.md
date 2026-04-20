# FIX-008 — LlamaSharpLlmProvider: Cache StatelessExecutor

## Priority
🔴 P1 — Performance bug. Every `CompleteAsync` call allocates a new KV-cache context, adding 200–800ms overhead before inference begins on typical hardware.

## Problem

```csharp
// Current — new executor on every call:
public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
{
    await EnsureInitializedAsync(ct);

    var prompt      = BuildPrompt(systemPrompt, userMessage);
    var executor    = new StatelessExecutor(_weights!, new ModelParams(_options.LlmModelPath));  // ← allocates context every call
    var inferParams = new InferenceParams { ... };

    var sb = new System.Text.StringBuilder();
    await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
        sb.Append(token);

    return sb.ToString().Trim();
}
```

`StatelessExecutor` internally creates a `LLamaContext` on construction. Context allocation involves:
- Memory mapping or copying model weights into the context window
- KV-cache allocation
- Sampling pipeline initialisation

For a 3B parameter model this can take 200–500ms. For 7B+ models it is routinely 500ms–2s. Since `LlamaSharpLlmProvider` is a singleton, the executor can and should be created once alongside `_weights`.

`StatelessExecutor` is designed for repeated stateless (single-turn) inference — it is safe to share across calls as long as calls are not concurrent. The existing `SemaphoreSlim _initLock` pattern already serialises initialisation; a similar guard is needed for inference.

---

## Change

**File:** `src/SourceRAG.Infrastructure/Llm/Local/LlamaSharpLlmProvider.cs`

```csharp
public sealed class LlamaSharpLlmProvider : ILlmProvider, IAsyncDisposable
{
    private readonly LlamaSharpOptions _options;
    private readonly ILogger<LlamaSharpLlmProvider> _logger;
    private readonly SemaphoreSlim _initLock    = new(1, 1);
    private readonly SemaphoreSlim _inferenceLock = new(1, 1); // NEW — serialise inference

    private LLamaWeights?      _weights;
    private StatelessExecutor? _executor;  // NEW — cached
    private bool               _initialized;

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

        // Serialise inference: StatelessExecutor is not thread-safe
        await _inferenceLock.WaitAsync(ct);
        try
        {
            var prompt      = BuildPrompt(systemPrompt, userMessage);
            var inferParams = new InferenceParams
            {
                MaxTokens        = 2048,
                SamplingPipeline = new DefaultSamplingPipeline()
            };

            var sb = new System.Text.StringBuilder();
            await foreach (var token in _executor!.InferAsync(prompt, inferParams, ct))
                sb.Append(token);

            return sb.ToString().Trim();
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // StatelessExecutor does not implement IDisposable in all LlamaSharp versions —
        // check the pinned version. If it does, dispose here.
        _weights?.Dispose();
        _initLock.Dispose();
        _inferenceLock.Dispose();
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

            var modelParams = new ModelParams(_options.LlmModelPath);
            _weights  = LLamaWeights.LoadFromFile(modelParams);
            _executor = new StatelessExecutor(_weights, modelParams);  // ← created once

            _initialized = true;

            var hasTemplate = _weights.Metadata.ContainsKey("tokenizer.chat_template");
            if (hasTemplate)
                _logger.LogInformation(
                    "GGUF tokenizer.chat_template detected — using model-native prompt format.");
            else
                _logger.LogWarning(
                    "No tokenizer.chat_template in GGUF metadata. " +
                    "Falling back to ChatML. If responses are malformed, " +
                    "verify the model supports ChatML.");
        }
        finally { _initLock.Release(); }
    }

    private string BuildPrompt(string systemPrompt, string userMessage)
    {
        var template = new LLamaTemplate(_weights!, strict: false);
        template.Add("system", systemPrompt);
        template.Add("user",   userMessage);
        return System.Text.Encoding.UTF8.GetString(template.Apply());
    }
}
```

### Key points

- `_executor` is cached alongside `_weights` — both created once in `EnsureInitializedAsync`
- `_inferenceLock` (separate from `_initLock`) serialises concurrent `CompleteAsync` calls
- The same `modelParams` instance is passed to both `LoadFromFile` and `StatelessExecutor` — this ensures the context parameters (context size, GPU layers etc.) are consistent
- `StatelessExecutor` resets its state between calls internally — it is designed for exactly this reuse pattern

---

## Concurrency note

`LlamaSharpLlmProvider` is registered as a singleton. SourceRAG is a single-user internal tool, so concurrent LLM calls are unlikely. The `_inferenceLock` is a safety net — in a high-concurrency scenario a pool of executors would be preferable, but that is out of scope for v1.

---

## Acceptance Criteria

- [ ] `_executor` is a field, created once in `EnsureInitializedAsync`
- [ ] `CompleteAsync` does not create a new `StatelessExecutor`
- [ ] `_inferenceLock` serialises concurrent inference calls
- [ ] `DisposeAsync` disposes `_inferenceLock`
- [ ] Solution builds without errors
