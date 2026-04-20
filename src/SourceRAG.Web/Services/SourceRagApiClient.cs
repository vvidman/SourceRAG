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
using Microsoft.Identity.Abstractions;
using SourceRAG.Web.Models;

namespace SourceRAG.Web.Services;

public sealed class SourceRagApiClient
{
    private const string ServiceName = "SourceRagApi";
    private readonly IDownstreamApi? _downstreamApi;
    private readonly HttpClient?     _httpClient;

    public SourceRagApiClient(IDownstreamApi downstreamApi)
    {
        _downstreamApi = downstreamApi;
    }

    public SourceRagApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ChatResponse?> ChatAsync(
        string query, int topK = 5, CancellationToken ct = default)
    {
        if (_downstreamApi is not null)
        {
            return await _downstreamApi.PostForUserAsync<ChatRequest, ChatResponse>(
                ServiceName,
                new ChatRequest(query, topK),
                options => options.RelativePath = "chat",
                cancellationToken: ct);
        }

        var response = await _httpClient!.PostAsJsonAsync("chat", new ChatRequest(query, topK), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ChatResponse>(ct);
    }

    public async Task<IndexJobResponse?> IndexAsync(
        string mode = "incremental", CancellationToken ct = default)
    {
        if (_downstreamApi is not null)
        {
            return await _downstreamApi.PostForUserAsync<IndexRequest, IndexJobResponse>(
                ServiceName,
                new IndexRequest(mode),
                options => options.RelativePath = "index",
                cancellationToken: ct);
        }

        var response = await _httpClient!.PostAsJsonAsync("index", new IndexRequest(mode), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IndexJobResponse>(ct);
    }

    public async Task<IndexStatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        if (_downstreamApi is not null)
        {
            return await _downstreamApi.GetForUserAsync<IndexStatusResponse>(
                ServiceName,
                options => options.RelativePath = "index/status",
                cancellationToken: ct);
        }

        return await _httpClient!.GetFromJsonAsync<IndexStatusResponse>("index/status", ct);
    }
}
