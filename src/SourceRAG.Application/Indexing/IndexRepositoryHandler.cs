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

using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.Extensions.Options;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Enums;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Application.Indexing;

public sealed class IndexRepositoryHandler : IRequestHandler<IndexRepositoryCommand, IndexJobResult>
{
    private readonly IVcsProvider _vcsProvider;
    private readonly IReindexStrategy _reindexStrategy;
    private readonly IEnumerable<IChunker> _chunkers;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _vectorStore;
    private readonly IIndexStateStore _indexStateStore;
    private readonly IOptions<SourceRagOptions> _options;

    public IndexRepositoryHandler(
        IVcsProvider vcsProvider,
        IReindexStrategy reindexStrategy,
        IEnumerable<IChunker> chunkers,
        IEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        IIndexStateStore indexStateStore,
        IOptions<SourceRagOptions> options)
    {
        _vcsProvider = vcsProvider;
        _reindexStrategy = reindexStrategy;
        _chunkers = chunkers;
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _indexStateStore = indexStateStore;
        _options = options;
    }

    public async Task<IndexJobResult> Handle(IndexRepositoryCommand request, CancellationToken ct)
    {
        var opts = _options.Value;
        var repoPath = opts.RepositoryPath;
        var branch = opts.Branch;
        var context = new PipelineContext { RepositoryPath = repoPath, Branch = branch };

        string toRevision;

        if (!request.FullReindex)
        {
            var lastRevision = await _indexStateStore.GetLastIndexedRevisionAsync(repoPath, ct);
            if (lastRevision is not null)
            {
                var scope = await _reindexStrategy.DetermineChangedFilesAsync(repoPath, lastRevision, ct);
                toRevision = scope.ToRevision;

                foreach (var file in scope.ChangedFiles)
                {
                    if (file.ChangeType == ChangeType.Deleted)
                    {
                        await _vectorStore.DeleteByFilePathAsync(file.Path, ct);
                        context.DeletedChunkCount++;
                    }
                    else
                        await ProcessFileAsync(repoPath, file.Path, scope.ToRevision, branch, context, ct);
                }
            }
            else
            {
                toRevision = await FullReindexAsync(repoPath, branch, context, ct);
            }
        }
        else
        {
            toRevision = await FullReindexAsync(repoPath, branch, context, ct);
        }

        await _indexStateStore.SetLastIndexedRevisionAsync(repoPath, toRevision, DateTimeOffset.UtcNow, ct);

        return new IndexJobResult(
            context.ProcessedFileCount,
            context.UpsertedChunkCount,
            context.DeletedChunkCount,
            toRevision,
            DateTimeOffset.UtcNow - context.StartedAt);
    }

    private async Task<string> FullReindexAsync(string repoPath, string branch, PipelineContext context, CancellationToken ct)
    {
        var files = await _vcsProvider.GetFilesAtHeadAsync(repoPath, ct);
        foreach (var file in files)
            await ProcessFileAsync(repoPath, file.Path, file.Revision, branch, context, ct);
        return _vcsProvider.GetCurrentRevision(repoPath);
    }

    private async Task ProcessFileAsync(string repoPath, string filePath, string revision, string branch, PipelineContext context, CancellationToken ct)
    {
        var chunker = _chunkers.FirstOrDefault(c => c.CanHandle(filePath));
        if (chunker is null) return;

        var content = await _vcsProvider.GetFileContentAsync(repoPath, filePath, revision, ct);
        var blame = await _vcsProvider.GetBlameAsync(repoPath, filePath, revision, ct);

        var baseMetadata = new ChunkMetadata
        {
            FilePath = filePath,
            Revision = revision,
            Author = blame.Author,
            CommitMessage = blame.CommitMessage,
            Timestamp = blame.Timestamp,
            Branch = branch
        };

        var chunks = chunker.Chunk(content, baseMetadata);
        foreach (var chunk in chunks)
        {
            var vector = await _embeddingProvider.EmbedAsync(chunk.Text, ct);
            var pointId = ComputePointId(repoPath, filePath, chunk.Metadata.SymbolName ?? string.Empty, revision);
            await _vectorStore.UpsertAsync(pointId, vector, chunk.Metadata, ct);
            context.UpsertedChunkCount++;
        }

        context.ProcessedFileCount++;
    }

    private static Guid ComputePointId(string repoPath, string filePath, string symbolKey, string revision)
    {
        var input = $"{repoPath}|{filePath}|{symbolKey}|{revision}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash[..16]);
    }
}
