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
using MediatR;
using Microsoft.Extensions.Options;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Application.Query;

public sealed class ChatQueryHandler : IRequestHandler<ChatQueryCommand, QueryResult>
{
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _vectorStore;
    private readonly IVcsProvider _vcsProvider;
    private readonly ILlmProvider _llmProvider;
    private readonly IOptions<SourceRagOptions> _options;

    public ChatQueryHandler(
        IEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        IVcsProvider vcsProvider,
        ILlmProvider llmProvider,
        IOptions<SourceRagOptions> options)
    {
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _vcsProvider = vcsProvider;
        _llmProvider = llmProvider;
        _options = options;
    }

    public async Task<QueryResult> Handle(ChatQueryCommand request, CancellationToken ct)
    {
        var repoPath = _options.Value.RepositoryPath;

        var queryVector = await _embeddingProvider.EmbedAsync(request.Query, ct);
        var results = await _vectorStore.SearchAsync(queryVector, request.TopK, ct);

        var contextBuilder = new StringBuilder();
        foreach (var scored in results)
        {
            var meta = scored.Chunk.Metadata;
            var content = await _vcsProvider.GetFileContentAsync(repoPath, meta.FilePath, meta.Revision, ct);
            var lines = content.Split('\n');
            var startIdx = meta.StartLine > 0 ? meta.StartLine - 1 : 0;
            var endIdx = meta.EndLine > 0 ? Math.Min(meta.EndLine - 1, lines.Length - 1) : lines.Length - 1;
            var trimmed = string.Join('\n', lines[startIdx..(endIdx + 1)]);

            contextBuilder.AppendLine($"// File: {meta.FilePath}");
            if (meta.SymbolName is not null)
                contextBuilder.AppendLine($"// Symbol: {meta.SymbolName}");
            contextBuilder.AppendLine(trimmed);
            contextBuilder.AppendLine();
        }

        const string systemPrompt = "You are a code assistant. Use the provided source code context to answer the user's question accurately and concisely.";
        var userMessage = $"Context:\n{contextBuilder}\nQuestion: {request.Query}";
        var answer = await _llmProvider.CompleteAsync(systemPrompt, userMessage, ct);

        return new QueryResult(answer, results);
    }
}
