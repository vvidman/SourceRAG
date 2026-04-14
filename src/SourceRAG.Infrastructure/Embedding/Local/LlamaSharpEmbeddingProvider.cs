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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.Embedding.Local;

public sealed class LlamaSharpEmbeddingProvider : IEmbeddingProvider, IAsyncDisposable
{
    private readonly SourceRagOptions _options;
    private readonly ILogger<LlamaSharpEmbeddingProvider> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private LLamaWeights?  _weights;
    private LLamaEmbedder? _embedder;
    private int            _dimensions = 768;

    public LlamaSharpEmbeddingProvider(
        IOptions<SourceRagOptions> options,
        ILogger<LlamaSharpEmbeddingProvider> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public int Dimensions => _dimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        var result = await _embedder!.GetEmbeddings(text, ct);
        return result[0];
    }

    public async ValueTask DisposeAsync()
    {
        _embedder?.Dispose();
        _weights?.Dispose();
        _initLock.Dispose();
        await ValueTask.CompletedTask;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_embedder is not null)
            return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_embedder is not null)
                return;

            _logger.LogInformation("Loading LlamaSharp model from {ModelPath}", _options.LlamaSharp.ModelPath);

            var modelParams = new ModelParams(_options.LlamaSharp.ModelPath)
            {
                Embeddings = true
            };

            _weights  = LLamaWeights.LoadFromFile(modelParams);
            _embedder = new LLamaEmbedder(_weights, modelParams, _logger);
            _dimensions = _embedder.EmbeddingSize;

            _logger.LogInformation("LlamaSharp model loaded. Embedding size: {Size}", _dimensions);
        }
        finally
        {
            _initLock.Release();
        }
    }
}
