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

using Microsoft.Extensions.Options;
using NSubstitute;
using SourceRAG.Application.Common;
using SourceRAG.Application.Query;
using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Application.Tests.Query;

public class ChatQueryHandlerTests
{
    private const string RepoPath = "/test/repo";

    private readonly IEmbeddingProvider _embeddingProvider = Substitute.For<IEmbeddingProvider>();
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IVcsProvider _vcsProvider = Substitute.For<IVcsProvider>();
    private readonly ILlmProvider _llmProvider = Substitute.For<ILlmProvider>();
    private readonly ChatQueryHandler _handler;

    private static readonly ChunkMetadata SampleMetadata = new()
    {
        FilePath = "src/Sample.cs",
        Revision = "rev-abc",
        Author = "dev",
        CommitMessage = "init",
        Timestamp = DateTimeOffset.UtcNow,
        Branch = "main",
        StartLine = 1,
        EndLine = 3
    };

    public ChatQueryHandlerTests()
    {
        var options = Options.Create(new SourceRagOptions
        {
            VcsProvider = "Git",
            EmbeddingProvider = "Local",
            RepositoryPath = RepoPath
        });

        _handler = new ChatQueryHandler(
            _embeddingProvider,
            _vectorStore,
            _vcsProvider,
            _llmProvider,
            options);

        _llmProvider.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("The answer."));
    }

    [Fact]
    public async Task Handle_EmbedsQueryBeforeSearch()
    {
        var queryVector = new float[] { 0.1f, 0.2f };
        _embeddingProvider.EmbedAsync("what does Foo do?", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(queryVector));
        _vectorStore.SearchAsync(queryVector, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ScoredChunk>>(Array.Empty<ScoredChunk>()));

        await _handler.Handle(new ChatQueryCommand("what does Foo do?"), CancellationToken.None);

        await _embeddingProvider.Received(1).EmbedAsync("what does Foo do?", Arg.Any<CancellationToken>());
        await _vectorStore.Received(1).SearchAsync(queryVector, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ReconstructsContentFromVcs()
    {
        var scoredChunk = new ScoredChunk(new CodeChunk("class Foo {}", SampleMetadata), 0.9f);
        var queryVector = new float[] { 0.5f };

        _embeddingProvider.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(queryVector));
        _vectorStore.SearchAsync(queryVector, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ScoredChunk>>(new[] { scoredChunk }));
        _vcsProvider.GetFileContentAsync(RepoPath, "src/Sample.cs", "rev-abc", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("line1\nline2\nline3\nline4"));

        await _handler.Handle(new ChatQueryCommand("explain Foo"), CancellationToken.None);

        await _vcsProvider.Received(1).GetFileContentAsync(
            RepoPath, "src/Sample.cs", "rev-abc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ReturnsQueryResultWithChunks()
    {
        var scoredChunk = new ScoredChunk(new CodeChunk("class Bar {}", SampleMetadata), 0.85f);
        var queryVector = new float[] { 0.3f };

        _embeddingProvider.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(queryVector));
        _vectorStore.SearchAsync(queryVector, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ScoredChunk>>(new[] { scoredChunk }));
        _vcsProvider.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("line1\nline2\nline3"));

        var result = await _handler.Handle(new ChatQueryCommand("describe Bar"), CancellationToken.None);

        Assert.Equal("The answer.", result.Answer);
        Assert.Single(result.Chunks);
        Assert.Equal(0.85f, result.Chunks[0].Score);
    }
}
