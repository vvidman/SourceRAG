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

using System.Collections.ObjectModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpSvn;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Enums;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.Vcs.Svn;

public sealed class SvnVcsProvider : IVcsProvider
{
    private readonly IVcsCredentialProvider _credentialProvider;
    private readonly SourceRagOptions _options;
    private readonly ILogger<SvnVcsProvider> _logger;
    private readonly string _repositoryUri; // e.g. "https://svn.example.com/repos/proj/trunk"

    public SvnVcsProvider(
        IVcsCredentialProvider credentialProvider,
        IOptions<SourceRagOptions> options,
        ILogger<SvnVcsProvider> logger)
    {
        _credentialProvider = credentialProvider;
        _options            = options.Value;
        _logger             = logger;

        if (string.IsNullOrWhiteSpace(_options.RepositoryUri))
            throw new InvalidOperationException(
                "SourceRAG:RepositoryUri is required when VcsProvider is 'Svn'. " +
                "Set it to the full SVN trunk/branch URI, e.g. https://svn.example.com/repos/proj/trunk");

        // Normalise — strip trailing slash
        _repositoryUri = _options.RepositoryUri.TrimEnd('/');
    }

    public string GetCurrentRevision(string repoPath)
    {
        using var client = CreateClient();
        client.GetInfo(new SvnPathTarget(repoPath), out SvnInfoEventArgs info);
        return info.Revision.ToString();
    }

    public Task<IReadOnlyList<VcsFile>> GetFilesAtHeadAsync(string repoPath, CancellationToken ct)
    {
        using var client = CreateClient();
        var currentRevision = GetCurrentRevision(repoPath);

        client.GetList(
            new SvnUriTarget(new Uri(_repositoryUri)),
            new SvnListArgs { Depth = SvnDepth.Infinity },
            out Collection<SvnListEventArgs> list);

        var files = list
            .Where(e => e.Entry.NodeKind == SvnNodeKind.File)
            .Select(e => new VcsFile(e.Path, currentRevision))
            .ToList();

        return Task.FromResult<IReadOnlyList<VcsFile>>(files);
    }

    public Task<string> GetFileContentAsync(
        string repoPath, string filePath, string revision, CancellationToken ct)
    {
        using var client = CreateClient();
        using var stream = new MemoryStream();

        // Construct full URI: repositoryUri + "/" + relativeFilePath
        var fullUri = new Uri($"{_repositoryUri}/{filePath.TrimStart('/')}");
        client.Write(new SvnUriTarget(fullUri, long.Parse(revision)), stream);

        return Task.FromResult(Encoding.UTF8.GetString(stream.ToArray()));
    }

    public Task<FileBlameInfo> GetBlameAsync(
        string repoPath, string filePath, string revision, CancellationToken ct)
    {
        using var client = CreateClient();

        var fullUri   = new Uri($"{_repositoryUri}/{filePath.TrimStart('/')}");
        var blameArgs = new SvnBlameArgs
        {
            Start = new SvnRevision(1),
            End   = new SvnRevision(long.Parse(revision))
        };

        SvnBlameEventArgs? firstLine = null;
        client.Blame(new SvnUriTarget(fullUri), blameArgs, (_, args) =>
        {
            firstLine ??= args;
        });

        if (firstLine is null)
            throw new InvalidOperationException($"No blame information found for '{filePath}'.");

        var commitMessage = GetLogMessage(client, repoPath, firstLine.Revision);

        return Task.FromResult(new FileBlameInfo
        {
            FilePath      = filePath,
            Revision      = firstLine.Revision.ToString(),
            Author        = firstLine.Author ?? string.Empty,
            CommitMessage = commitMessage,
            Timestamp     = firstLine.Time
        });
    }

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesSinceAsync(
        string repoPath, string sinceRevision, CancellationToken ct)
    {
        using var client = CreateClient();

        var logArgs = new SvnLogArgs
        {
            Start                = new SvnRevision(long.Parse(sinceRevision) + 1),
            End                  = SvnRevision.Head,
            RetrieveChangedPaths = true
        };

        // Determine the repository root to strip it from absolute paths.
        client.GetInfo(new SvnPathTarget(repoPath), out SvnInfoEventArgs info);
        var repoRoot = info.RepositoryRoot?.ToString().TrimEnd('/') ?? string.Empty;

        // The "trunk" prefix within the repo: _repositoryUri relative to repoRoot
        // e.g. repoRoot = "https://svn.example.com/repos/proj"
        //      _repositoryUri = "https://svn.example.com/repos/proj/trunk"
        //      trunkPrefix = "/trunk"
        var trunkPrefix = _repositoryUri.Length > repoRoot.Length
            ? _repositoryUri[repoRoot.Length..]   // e.g. "/trunk"
            : string.Empty;

        var changedPaths = new Dictionary<string, Domain.Enums.ChangeType>(StringComparer.OrdinalIgnoreCase);

        client.Log(repoPath, logArgs, (_, args) =>
        {
            if (args.ChangedPaths is null) return;
            foreach (var item in args.ChangedPaths)
            {
                // item.Path is an absolute repo path, e.g. "/trunk/src/Foo.cs"
                // Strip trunkPrefix to get relative path "src/Foo.cs"
                var relativePath = item.Path;
                if (!string.IsNullOrEmpty(trunkPrefix) &&
                    relativePath.StartsWith(trunkPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = relativePath[trunkPrefix.Length..].TrimStart('/');
                }

                // Skip files outside our trunk/branch scope
                if (string.IsNullOrWhiteSpace(relativePath)) continue;

                // Last writer wins for a path that appears in multiple revisions
                changedPaths[relativePath] = MapSvnAction(item.Action);
            }
        });

        var result = changedPaths
            .Select(kv => new ChangedFile(kv.Key, kv.Value))
            .ToList();

        return Task.FromResult<IReadOnlyList<ChangedFile>>(result);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string GetLogMessage(SvnClient client, string repoPath, SvnRevision revision)
    {
        var logArgs = new SvnLogArgs
        {
            Start = revision,
            End   = revision,
            Limit = 1
        };

        string message = string.Empty;
        client.Log(repoPath, logArgs, (_, args) =>
        {
            message = args.LogMessage ?? string.Empty;
        });

        return message;
    }

    private SvnClient CreateClient()
    {
        var client     = new SvnClient();
        var credential = _credentialProvider.Resolve(VcsProviderType.Svn);

        if (credential is UserPasswordCredential up)
        {
            client.Authentication.UserNamePasswordHandlers +=
                (_, args) => { args.UserName = up.Username; args.Password = up.Password; };
        }

        return client;
    }

    private static Domain.Enums.ChangeType MapSvnAction(SvnChangeAction action) => action switch
    {
        SvnChangeAction.Add    => Domain.Enums.ChangeType.Added,
        SvnChangeAction.Delete => Domain.Enums.ChangeType.Deleted,
        SvnChangeAction.Replace => Domain.Enums.ChangeType.Renamed,
        _                       => Domain.Enums.ChangeType.Modified
    };
}
