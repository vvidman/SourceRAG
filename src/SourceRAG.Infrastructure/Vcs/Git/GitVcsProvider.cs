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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Enums;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.Vcs.Git;

public sealed class GitVcsProvider : IVcsProvider
{
    private readonly IVcsCredentialProvider _credentialProvider;
    private readonly SourceRagOptions _options;
    private readonly ILogger<GitVcsProvider> _logger;

    public GitVcsProvider(
        IVcsCredentialProvider credentialProvider,
        IOptions<SourceRagOptions> options,
        ILogger<GitVcsProvider> logger)
    {
        _credentialProvider = credentialProvider;
        _options = options.Value;
        _logger = logger;
    }

    public string GetCurrentRevision(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.Head.Tip.Sha;
    }

    public Task<IReadOnlyList<VcsFile>> GetFilesAtHeadAsync(string repoPath, CancellationToken ct)
    {
        using var repo = new Repository(repoPath);
        var sha = repo.Head.Tip.Sha;
        var files = new List<VcsFile>();
        CollectBlobs(repo.Head.Tip.Tree, string.Empty, sha, files);
        return Task.FromResult<IReadOnlyList<VcsFile>>(files);
    }

    public Task<string> GetFileContentAsync(string repoPath, string filePath, string revision, CancellationToken ct)
    {
        using var repo = new Repository(repoPath);
        var commit = repo.Lookup<Commit>(revision);
        var entry = commit[filePath];
        var blob = (Blob)entry.Target;
        return Task.FromResult(blob.GetContentText());
    }

    public Task<FileBlameInfo> GetBlameAsync(string repoPath, string filePath, string revision, CancellationToken ct)
    {
        using var repo = new Repository(repoPath);
        var blame = repo.Blame(filePath, new BlameOptions { StartingAt = revision });
        var hunk = blame[0];
        return Task.FromResult(new FileBlameInfo
        {
            FilePath      = filePath,
            Revision      = hunk.FinalCommit.Sha,
            Author        = hunk.FinalCommit.Author.Name,
            CommitMessage = hunk.FinalCommit.Message,
            Timestamp     = hunk.FinalCommit.Author.When
        });
    }

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesSinceAsync(
        string repoPath, string sinceRevision, CancellationToken ct)
    {
        using var repo = new Repository(repoPath);
        var oldTree = repo.Lookup<Commit>(sinceRevision).Tree;
        var newTree = repo.Head.Tip.Tree;
        var diff    = repo.Diff.Compare<TreeChanges>(oldTree, newTree);

        var changes = diff
            .Select(e => new ChangedFile(e.Path, MapChangeKind(e.Status)))
            .ToList();

        return Task.FromResult<IReadOnlyList<ChangedFile>>(changes);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void CollectBlobs(Tree tree, string prefix, string sha, List<VcsFile> files)
    {
        foreach (var entry in tree)
        {
            var path = string.IsNullOrEmpty(prefix) ? entry.Name : $"{prefix}/{entry.Name}";
            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Blob:
                    files.Add(new VcsFile(path, sha));
                    break;
                case TreeEntryTargetType.Tree:
                    CollectBlobs((Tree)entry.Target, path, sha, files);
                    break;
                // GitLink (submodule) — skip
            }
        }
    }

    private static Domain.Enums.ChangeType MapChangeKind(ChangeKind status) => status switch
    {
        ChangeKind.Added   => Domain.Enums.ChangeType.Added,
        ChangeKind.Deleted => Domain.Enums.ChangeType.Deleted,
        ChangeKind.Renamed => Domain.Enums.ChangeType.Renamed,
        _                  => Domain.Enums.ChangeType.Modified
    };
}
