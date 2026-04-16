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
