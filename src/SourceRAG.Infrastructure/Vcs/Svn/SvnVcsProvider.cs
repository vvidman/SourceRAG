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

    public SvnVcsProvider(
        IVcsCredentialProvider credentialProvider,
        IOptions<SourceRagOptions> options,
        ILogger<SvnVcsProvider> logger)
    {
        _credentialProvider = credentialProvider;
        _options            = options.Value;
        _logger             = logger;
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
            new SvnUriTarget(repoPath),
            new SvnListArgs { Depth = SvnDepth.Infinity },
            out Collection<SvnListEventArgs> list);

        var files = list
            .Where(e => e.Entry.NodeKind == SvnNodeKind.File)
            .Select(e => new VcsFile(e.Path, currentRevision))
            .ToList();

        return Task.FromResult<IReadOnlyList<VcsFile>>(files);
    }

    public Task<string> GetFileContentAsync(string repoPath, string filePath, string revision, CancellationToken ct)
    {
        using var client = CreateClient();
        using var stream = new MemoryStream();
        client.Write(new SvnUriTarget(filePath, long.Parse(revision)), stream);
        return Task.FromResult(Encoding.UTF8.GetString(stream.ToArray()));
    }

    public Task<FileBlameInfo> GetBlameAsync(string repoPath, string filePath, string revision, CancellationToken ct)
    {
        using var client = CreateClient();
        var blameArgs = new SvnBlameArgs
        {
            Start = new SvnRevision(1),
            End   = new SvnRevision(long.Parse(revision))
        };

        SvnBlameEventArgs? firstLine = null;
        client.Blame(new SvnPathTarget(filePath), blameArgs, (_, args) =>
        {
            firstLine ??= args;
        });

        if (firstLine is null)
            throw new InvalidOperationException($"No blame information found for '{filePath}'.");

        return Task.FromResult(new FileBlameInfo
        {
            FilePath      = filePath,
            Revision      = firstLine.Revision.ToString(),
            Author        = firstLine.Author ?? string.Empty,
            CommitMessage = string.Empty, // SvnBlameEventArgs does not carry log messages
            Timestamp     = firstLine.Time
        });
    }

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesSinceAsync(
        string repoPath, string sinceRevision, CancellationToken ct)
    {
        using var client = CreateClient();
        var logArgs = new SvnLogArgs
        {
            Start          = new SvnRevision(long.Parse(sinceRevision) + 1),
            End            = SvnRevision.Head,
            RetrieveChangedPaths = true
        };

        var changedPaths = new Dictionary<string, Domain.Enums.ChangeType>();
        client.Log(repoPath, logArgs, (_, args) =>
        {
            if (args.ChangedPaths is null) return;
            foreach (var item in args.ChangedPaths)
            {
                var changeType = MapSvnAction(item.Action);
                // last writer wins for a path that appears in multiple revisions
                changedPaths[item.Path] = changeType;
            }
        });

        var result = changedPaths
            .Select(kv => new ChangedFile(kv.Key, kv.Value))
            .ToList();

        return Task.FromResult<IReadOnlyList<ChangedFile>>(result);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

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
