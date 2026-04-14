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

using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Enums;
using SourceRAG.Infrastructure.Vcs.Auth;
using SourceRAG.Infrastructure.Vcs.Git;

namespace SourceRAG.Infrastructure.Tests.Vcs.Git;

public sealed class GitVcsProviderTests : IDisposable
{
    private readonly string _repoPath;
    private readonly string _initialSha;
    private readonly GitVcsProvider _sut;

    private static readonly Signature TestSig =
        new("Test User", "test@example.com", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public GitVcsProviderTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"sourcerag-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);

        Repository.Init(_repoPath);
        using var repo = new Repository(_repoPath);

        File.WriteAllText(Path.Combine(_repoPath, "hello.txt"), "Hello, world!");
        Commands.Stage(repo, "hello.txt");
        var commit = repo.Commit("Initial commit", TestSig, TestSig);
        _initialSha = commit.Sha;

        var options = Options.Create(new SourceRagOptions
        {
            VcsProvider       = "Git",
            EmbeddingProvider = "Local",
            RepositoryPath    = _repoPath
        });

        _sut = new GitVcsProvider(
            new EnvironmentVcsCredentialProvider(),
            options,
            NullLogger<GitVcsProvider>.Instance);
    }

    public void Dispose()
    {
        // LibGit2Sharp keeps native handles; give GC a chance before deleting
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { Directory.Delete(_repoPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void GetCurrentRevision_ReturnsCommitHash()
    {
        var sha = _sut.GetCurrentRevision(_repoPath);

        Assert.Equal(_initialSha, sha);
    }

    [Fact]
    public async Task GetFilesAtHead_ReturnsAllFiles()
    {
        var files = await _sut.GetFilesAtHeadAsync(_repoPath, CancellationToken.None);

        Assert.Single(files);
        Assert.Equal("hello.txt", files[0].Path);
        Assert.Equal(_initialSha, files[0].Revision);
    }

    [Fact]
    public async Task GetFileContent_ReturnsCorrectContent()
    {
        var content = await _sut.GetFileContentAsync(
            _repoPath, "hello.txt", _initialSha, CancellationToken.None);

        Assert.Equal("Hello, world!", content);
    }

    [Fact]
    public async Task GetChangedFilesSince_ReturnsModifiedFile()
    {
        // Add a second commit that modifies hello.txt
        File.WriteAllText(Path.Combine(_repoPath, "hello.txt"), "Updated content");
        using var repo = new Repository(_repoPath);
        Commands.Stage(repo, "hello.txt");
        repo.Commit("Update hello.txt", TestSig, TestSig);

        var changed = await _sut.GetChangedFilesSinceAsync(
            _repoPath, _initialSha, CancellationToken.None);

        Assert.Single(changed);
        Assert.Equal("hello.txt", changed[0].Path);
        Assert.Equal(ChangeType.Modified, changed[0].ChangeType);
    }
}
