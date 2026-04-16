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

using Microsoft.Identity.Abstractions;
using SourceRAG.Web.Models;

namespace SourceRAG.Web.Services;

public sealed class SourceRagApiClient(IDownstreamApi downstreamApi)
{
    private const string ServiceName = "SourceRagApi";

    public async Task<ChatResponse?> ChatAsync(
        string query, int topK = 5, CancellationToken ct = default)
    {
        var request = new ChatRequest(query, topK);
        return await downstreamApi.PostForUserAsync<ChatRequest, ChatResponse>(
            ServiceName,
            request,
            options => options.RelativePath = "chat",
            cancellationToken: ct);
    }

    public async Task<IndexJobResponse?> IndexAsync(
        string mode = "incremental", CancellationToken ct = default)
    {
        var request = new IndexRequest(mode);
        return await downstreamApi.PostForUserAsync<IndexRequest, IndexJobResponse>(
            ServiceName,
            request,
            options => options.RelativePath = "index",
            cancellationToken: ct);
    }

    public async Task<IndexStatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        return await downstreamApi.GetForUserAsync<IndexStatusResponse>(
            ServiceName,
            options => options.RelativePath = "index/status",
            cancellationToken: ct);
    }
}
