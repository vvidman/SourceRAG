/*
   Copyright 2026 Viktor Vidman (vvidman)

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/

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

        var prompt      = BuildPrompt(systemPrompt, userMessage);
        var executor    = new StatelessExecutor(_weights!, new ModelParams(_options.LlmModelPath));
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

            var hasTemplate = _weights.Metadata.ContainsKey("tokenizer.chat_template");
            if (hasTemplate)
                _logger.LogInformation(
                    "GGUF tokenizer.chat_template detected — using model-native prompt format.");
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
        // strict: false so it falls back to ChatML when no template metadata is found.
        var template = new LLamaTemplate(_weights!, strict: false);
        template.Add("system", systemPrompt);
        template.Add("user",   userMessage);
        return System.Text.Encoding.UTF8.GetString(template.Apply());
    }
}
