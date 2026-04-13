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
using SourceRAG.Application.Status;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Application.Tests.Status;

public class GetIndexStatusHandlerTests
{
    private const string RepoPath = "/test/repo";

    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IIndexStateStore _indexStateStore = Substitute.For<IIndexStateStore>();
    private readonly GetIndexStatusHandler _handler;

    public GetIndexStatusHandlerTests()
    {
        var options = Options.Create(new SourceRagOptions
        {
            VcsProvider = "Git",
            EmbeddingProvider = "Local",
            RepositoryPath = RepoPath
        });

        _handler = new GetIndexStatusHandler(_vectorStore, _indexStateStore, options);
    }

    [Fact]
    public async Task Handle_NeverIndexed_ReturnsNullRevision()
    {
        _indexStateStore.GetLastIndexedRevisionAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _vectorStore.CountAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var result = await _handler.Handle(new GetIndexStatusQuery(), CancellationToken.None);

        Assert.Null(result.LastIndexedRevision);
        Assert.Equal(0, result.ChunkCount);
    }

    [Fact]
    public async Task Handle_ReturnsChunkCountFromVectorStore()
    {
        _indexStateStore.GetLastIndexedRevisionAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("rev-005"));
        _indexStateStore.GetLastIndexedAtAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow));
        _vectorStore.CountAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(42));

        var result = await _handler.Handle(new GetIndexStatusQuery(), CancellationToken.None);

        Assert.Equal(42, result.ChunkCount);
        Assert.Equal("rev-005", result.LastIndexedRevision);
        await _vectorStore.Received(1).CountAsync(Arg.Any<CancellationToken>());
    }
}
