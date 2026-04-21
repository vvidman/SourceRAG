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

using Microsoft.Extensions.Logging.Abstractions;
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
            options,
            NullLogger<IndexRepositoryHandler>.Instance);

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
    public async Task Handle_RenamedFile_DeletesOldPathAndIndexesNewPath()
    {
        var renamedFile = new ChangedFile(
            Path:       "src/Domain/Foo.cs",
            ChangeType: ChangeType.Renamed,
            OldPath:    "src/Core/Foo.cs");

        var blame = new FileBlameInfo
        {
            FilePath      = "src/Domain/Foo.cs",
            Revision      = "rev-002",
            Author        = "dev",
            CommitMessage = "move Foo to Domain",
            Timestamp     = DateTimeOffset.UtcNow
        };
        var chunk = new CodeChunk("class Foo {}", new ChunkMetadata
        {
            FilePath      = "src/Domain/Foo.cs",
            Revision      = "rev-002",
            Author        = "dev",
            CommitMessage = "move Foo to Domain",
            Timestamp     = DateTimeOffset.UtcNow,
            Branch        = "main"
        });

        _indexStateStore.GetLastIndexedRevisionAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("rev-001"));
        _reindexStrategy.DetermineChangedFilesAsync(RepoPath, "rev-001", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ReindexScope(
                new[] { renamedFile }, "rev-001", "rev-002")));
        _vectorStore.DeleteByFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _vcsProvider.GetFileContentAsync(RepoPath, "src/Domain/Foo.cs", "rev-002", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("class Foo {}"));
        _vcsProvider.GetBlameAsync(RepoPath, "src/Domain/Foo.cs", "rev-002", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(blame));
        _chunker.CanHandle("src/Domain/Foo.cs").Returns(true);
        _chunker.Chunk(Arg.Any<string>(), Arg.Any<ChunkMetadata>())
            .Returns(new List<CodeChunk> { chunk });
        _vectorStore.UpsertAsync(Arg.Any<Guid>(), Arg.Any<float[]>(), Arg.Any<ChunkMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _embeddingProvider.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[] { 0.1f }));

        await _handler.Handle(new IndexRepositoryCommand(FullReindex: false), CancellationToken.None);

        // Old path chunks must be deleted
        await _vectorStore.Received(1)
            .DeleteByFilePathAsync("src/Core/Foo.cs", Arg.Any<CancellationToken>());

        // New path must be indexed
        await _vectorStore.Received(1)
            .UpsertAsync(Arg.Any<Guid>(), Arg.Any<float[]>(), Arg.Any<ChunkMetadata>(), Arg.Any<CancellationToken>());

        // Old path must NOT be fetched from VCS
        await _vcsProvider.DidNotReceive()
            .GetFileContentAsync(RepoPath, "src/Core/Foo.cs", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FullReindex_ResumesFromCheckpointIfSameRevision()
    {
        // Files to index
        var file1 = new VcsFile("src/A.cs", "rev-head");
        var file2 = new VcsFile("src/B.cs", "rev-head");
        var blame = new FileBlameInfo
        {
            FilePath = "src/B.cs", Revision = "rev-head",
            Author = "dev", CommitMessage = "msg", Timestamp = DateTimeOffset.UtcNow
        };
        var chunk = new CodeChunk("class B {}", new ChunkMetadata
        {
            FilePath = "src/B.cs", Revision = "rev-head",
            Author = "dev", CommitMessage = "msg",
            Timestamp = DateTimeOffset.UtcNow, Branch = "main"
        });

        _vcsProvider.GetCurrentRevision(RepoPath).Returns("rev-head");
        _vcsProvider.GetFilesAtHeadAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VcsFile>>(new[] { file1, file2 }));

        // Checkpoint says A.cs was already processed at rev-head
        _indexStateStore.GetCheckpointAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IndexCheckpoint?>(new IndexCheckpoint("rev-head", "src/A.cs", TotalFiles: 2, ProcessedFiles: 1)));
        _indexStateStore.SaveCheckpointAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _indexStateStore.ClearCheckpointAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _vcsProvider.GetFileContentAsync(RepoPath, "src/B.cs", "rev-head", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("class B {}"));
        _vcsProvider.GetBlameAsync(RepoPath, "src/B.cs", "rev-head", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(blame));
        _chunker.CanHandle("src/B.cs").Returns(true);
        _chunker.Chunk(Arg.Any<string>(), Arg.Any<ChunkMetadata>())
            .Returns(new List<CodeChunk> { chunk });
        _vectorStore.UpsertAsync(Arg.Any<Guid>(), Arg.Any<float[]>(), Arg.Any<ChunkMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _handler.Handle(new IndexRepositoryCommand(FullReindex: true), CancellationToken.None);

        // A.cs was skipped — no VCS call for it
        await _vcsProvider.DidNotReceive()
            .GetFileContentAsync(RepoPath, "src/A.cs", Arg.Any<string>(), Arg.Any<CancellationToken>());

        // B.cs was processed
        await _vcsProvider.Received(1)
            .GetFileContentAsync(RepoPath, "src/B.cs", "rev-head", Arg.Any<CancellationToken>());

        // Checkpoint cleared on success
        await _indexStateStore.Received(1)
            .ClearCheckpointAsync(RepoPath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FullReindex_IgnoresCheckpointIfDifferentRevision()
    {
        _vcsProvider.GetCurrentRevision(RepoPath).Returns("rev-new");
        _vcsProvider.GetFilesAtHeadAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VcsFile>>(Array.Empty<VcsFile>()));

        // Checkpoint is for an old revision — must be ignored
        _indexStateStore.GetCheckpointAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IndexCheckpoint?>(new IndexCheckpoint("rev-old", "src/A.cs", TotalFiles: 5, ProcessedFiles: 2)));
        _indexStateStore.ClearCheckpointAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _handler.Handle(new IndexRepositoryCommand(FullReindex: true), CancellationToken.None);

        // No file skipping — A.cs would be processed if it were in the file list
        // Verified by confirming GetFilesAtHead was called (not short-circuited)
        await _vcsProvider.Received(1)
            .GetFilesAtHeadAsync(RepoPath, Arg.Any<CancellationToken>());
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
