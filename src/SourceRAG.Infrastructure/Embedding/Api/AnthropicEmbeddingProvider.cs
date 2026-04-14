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

using System.Text;
using System.Text.Json;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.Embedding.Api;

public sealed class AnthropicEmbeddingProvider : IEmbeddingProvider
{
    private const int    EmbeddingDimensions  = 1536;
    private const string EmbeddingModel       = "voyage-code-3";
    private const string EmbeddingsEndpoint   = "https://api.anthropic.com/v1/embeddings";
    private const string AnthropicVersionHeader = "2023-06-01";

    private readonly IHttpClientFactory _httpClientFactory;

    public AnthropicEmbeddingProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public int Dimensions => EmbeddingDimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersionHeader);

        var requestBody = JsonSerializer.Serialize(new
        {
            model      = EmbeddingModel,
            input      = new[] { text },
            input_type = "document"
        });

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        var response = await client.PostAsync(EmbeddingsEndpoint, content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc    = JsonDocument.Parse(responseJson);

        var embeddingArray = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding");

        return embeddingArray.EnumerateArray()
            .Select(e => e.GetSingle())
            .ToArray();
    }
}
