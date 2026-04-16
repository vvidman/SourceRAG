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
using SourceRAG.Application.Indexing;
using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Enums;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Application.Tests.Indexing;

public class IndexRepositoryHandlerTests
{
    private const string RepoPath = "/test/repo";

    private readonly IVcsProvider _vcsProvider = Substitute.For<IVcsProvider>();
    private readonly IReindexStrategy _reindexStrategy = Substitute.For<IReindexStrategy>();
    private readonly IChunker _chunker = Substitute.For<IChunker>();
    private readonly IEmbeddingProvider _embeddingProvider = Substitute.For<IEmbeddingProvider>();
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IIndexStateStore _indexStateStore = Substitute.For<IIndexStateStore>();
    private readonly IndexRepositoryHandler _handler;

    public IndexRepositoryHandlerTests()
    {
        var options = Options.Create(new SourceRagOptions
        {
            VcsProvider = "Git",
            EmbeddingProvider = "Local",
            RepositoryPath = RepoPath
        });

        _handler = new IndexRepositoryHandler(
            _vcsProvider,
            _reindexStrategy,
            new[] { _chunker },
            _embeddingProvider,
            _vectorStore,
            _indexStateStore,
            options);

        _vcsProvider.GetCurrentRevision(RepoPath).Returns("rev-head");
        _embeddingProvider.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[] { 0.1f, 0.2f }));
        _indexStateStore.SetLastIndexedRevisionAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Handle_FullReindex_CallsGetFilesAtHead()
    {
        _vcsProvider.GetFilesAtHeadAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VcsFile>>(Array.Empty<VcsFile>()));

        await _handler.Handle(new IndexRepositoryCommand(FullReindex: true), CancellationToken.None);

        await _vcsProvider.Received(1).GetFilesAtHeadAsync(RepoPath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_IncrementalReindex_CallsDetermineChangedFiles()
    {
        _indexStateStore.GetLastIndexedRevisionAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("rev-001"));
        _reindexStrategy.DetermineChangedFilesAsync(RepoPath, "rev-001", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ReindexScope(Array.Empty<ChangedFile>(), "rev-001", "rev-002")));

        await _handler.Handle(new IndexRepositoryCommand(FullReindex: false), CancellationToken.None);

        await _reindexStrategy.Received(1)
            .DetermineChangedFilesAsync(RepoPath, "rev-001", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DeletedFile_CallsVectorStoreDelete()
    {
        var deletedFile = new ChangedFile("src/Foo.cs", ChangeType.Deleted);

        _indexStateStore.GetLastIndexedRevisionAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("rev-001"));
        _reindexStrategy.DetermineChangedFilesAsync(RepoPath, "rev-001", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ReindexScope(new[] { deletedFile }, "rev-001", "rev-002")));
        _vectorStore.DeleteByFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _handler.Handle(new IndexRepositoryCommand(FullReindex: false), CancellationToken.None);

        // Verify filter-based delete — no VCS access needed
        await _vectorStore.Received(1)
            .DeleteByFilePathAsync("src/Foo.cs", Arg.Any<CancellationToken>());

        // Verify NO VCS calls for the deleted file
        await _vcsProvider.DidNotReceive()
            .GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoChunkerForFile_SkipsFile()
    {
        var file = new VcsFile("src/Unknown.xyz", "rev-001");
        _vcsProvider.GetFilesAtHeadAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VcsFile>>(new[] { file }));
        _chunker.CanHandle("src/Unknown.xyz").Returns(false);

        await _handler.Handle(new IndexRepositoryCommand(FullReindex: true), CancellationToken.None);

        await _vectorStore.DidNotReceive().UpsertAsync(
            Arg.Any<Guid>(), Arg.Any<float[]>(), Arg.Any<ChunkMetadata>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ReturnsCorrectJobResult()
    {
        var file = new VcsFile("src/Bar.cs", "rev-002");
        var blame = new FileBlameInfo
        {
            FilePath = "src/Bar.cs",
            Revision = "rev-002",
            Author = "dev",
            CommitMessage = "add Bar",
            Timestamp = DateTimeOffset.UtcNow
        };
        var baseMetadata = new ChunkMetadata
        {
            FilePath = "src/Bar.cs",
            Revision = "rev-002",
            Author = "dev",
            CommitMessage = "add Bar",
            Timestamp = DateTimeOffset.UtcNow,
            Branch = "main"
        };
        var chunks = new List<CodeChunk>
        {
            new("chunk1", baseMetadata),
            new("chunk2", baseMetadata)
        };

        _vcsProvider.GetFilesAtHeadAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VcsFile>>(new[] { file }));
        _vcsProvider.GetFileContentAsync(RepoPath, "src/Bar.cs", "rev-002", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("class Bar {}"));
        _vcsProvider.GetBlameAsync(RepoPath, "src/Bar.cs", "rev-002", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(blame));
        _chunker.CanHandle("src/Bar.cs").Returns(true);
        _chunker.Chunk(Arg.Any<string>(), Arg.Any<ChunkMetadata>()).Returns(chunks);
        _vectorStore.UpsertAsync(Arg.Any<Guid>(), Arg.Any<float[]>(), Arg.Any<ChunkMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _handler.Handle(new IndexRepositoryCommand(FullReindex: true), CancellationToken.None);

        Assert.Equal(1, result.ProcessedFiles);
        Assert.Equal(2, result.UpsertedChunks);
        Assert.Equal(0, result.DeletedChunks);
    }
}
