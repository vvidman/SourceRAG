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

using MediatR;
using Microsoft.Extensions.Options;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Application.Status;

public sealed class GetIndexStatusHandler : IRequestHandler<GetIndexStatusQuery, IndexStatus>
{
    private readonly IVectorStore _vectorStore;
    private readonly IIndexStateStore _indexStateStore;
    private readonly IOptions<SourceRagOptions> _options;

    public GetIndexStatusHandler(
        IVectorStore vectorStore,
        IIndexStateStore indexStateStore,
        IOptions<SourceRagOptions> options)
    {
        _vectorStore = vectorStore;
        _indexStateStore = indexStateStore;
        _options = options;
    }

    public async Task<IndexStatus> Handle(GetIndexStatusQuery request, CancellationToken ct)
    {
        var repoPath = _options.Value.RepositoryPath;

        var lastRevision = await _indexStateStore.GetLastIndexedRevisionAsync(repoPath, ct);
        var lastIndexedAt = lastRevision is not null
            ? await _indexStateStore.GetLastIndexedAtAsync(repoPath, ct)
            : null;
        var chunkCount = await _vectorStore.CountAsync(ct);

        return new IndexStatus
        {
            LastIndexedRevision = lastRevision,
            ChunkCount = chunkCount,
            LastIndexedAt = lastIndexedAt
        };
    }
}
