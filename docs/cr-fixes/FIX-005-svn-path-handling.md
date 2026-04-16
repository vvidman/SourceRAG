# FIX-005 — SvnVcsProvider: Path Handling (Relative vs Absolute vs URI)

## Problem

`SvnVcsProvider` has three related path-handling bugs:

**Bug A — `GetFileContentAsync`:** `SvnUriTarget(filePath, revision)` receives a relative path (`"src/Foo.cs"`) but `SvnUriTarget` expects a full SVN URL (`svn://host/trunk/src/Foo.cs` or `https://host/svn/trunk/src/Foo.cs`). This throws a `SvnException` at runtime.

**Bug B — `GetBlameAsync`:** Same problem — `SvnPathTarget(filePath)` receives a relative path, but the blame operation requires either the full working-copy absolute path or a full repository URI.

**Bug C — `GetChangedFilesSinceAsync`:** `client.Log(repoPath, ...)` returns `SvnChangeItem.Path` values as **absolute repository paths** (e.g. `/trunk/src/Foo.cs`), not relative paths (`src/Foo.cs`). The downstream handler uses these paths to call `GetFileContentAsync`, which would receive an absolute repo path instead of the relative path it expects.

---

## Root Cause

The provider does not know the repository root URI. Without it, it cannot construct full URIs from relative paths, nor strip the root prefix from absolute log paths.

---

## Fix

Add `RepositoryUri` to `SourceRagOptions` and use it in `SvnVcsProvider` to construct full URIs and to normalise log-returned absolute paths to relative paths.

### 1. Application — `SourceRagOptions.cs`

Add optional `RepositoryUri` property:

```csharp
public sealed class SourceRagOptions
{
    // ... existing properties ...

    /// <summary>
    /// Full SVN repository URI, e.g. "https://svn.example.com/repos/myproject/trunk".
    /// Required when VcsProvider is "Svn". Used to construct full file URIs and
    /// to normalise absolute repository paths returned by SVN log operations.
    /// </summary>
    public string RepositoryUri { get; init; } = string.Empty;
}
```

### 2. Infrastructure — `SvnVcsProvider.cs`

Full rewrite of the three affected methods.

#### Constructor

Cache the repository URI:

```csharp
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
```

#### `GetFileContentAsync`

```csharp
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
```

#### `GetBlameAsync`

```csharp
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

    // SVN blame does not carry the commit message — a separate log call is needed.
    // We fetch the log entry for the blamed revision to get the message.
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
```

#### `GetChangedFilesSinceAsync`

Strip the repository root prefix from absolute log paths to produce relative paths:

```csharp
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
    // GetInfo gives us the repository root URI.
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

            changedPaths[relativePath] = MapSvnAction(item.Action);
        }
    });

    var result = changedPaths
        .Select(kv => new ChangedFile(kv.Key, kv.Value))
        .ToList();

    return Task.FromResult<IReadOnlyList<ChangedFile>>(result);
}
```

### 3. Infrastructure validation — `InfrastructureServiceExtensions.cs`

Add SVN-specific validation to `ValidateOptions`:

```csharp
if (opts.VcsProvider == "Svn" && string.IsNullOrWhiteSpace(opts.RepositoryUri))
    throw new InvalidOperationException(
        "SourceRAG:RepositoryUri is required when VcsProvider is 'Svn'. " +
        "Example: https://svn.example.com/repos/myproject/trunk");
```

### 4. Configuration — `appsettings.json` (both Api and McpHost)

Add the new field under `SourceRAG`:

```json
{
  "SourceRAG": {
    "VcsProvider": "Svn",
    "RepositoryUri": "https://svn.example.com/repos/myproject/trunk",
    "RepositoryPath": "/path/to/svn/working-copy",
    "Branch": "trunk"
  }
}
```

`RepositoryPath` remains the local working-copy path (used for `GetInfo`, `Log`, `Blame` with local targets). `RepositoryUri` is the remote URI (used for `GetFileContentAsync` and `GetBlameAsync` URI targets).

---

## Note on `GetFilesAtHeadAsync`

The existing implementation uses `SvnUriTarget(repoPath)` where `repoPath` is the working-copy path — `SvnListArgs` with `SvnUriTarget` would fail. Fix:

```csharp
public Task<IReadOnlyList<VcsFile>> GetFilesAtHeadAsync(string repoPath, CancellationToken ct)
{
    using var client = CreateClient();
    var currentRevision = GetCurrentRevision(repoPath);

    // Use URI target for listing, not local path
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
```

---

## Acceptance Criteria

- [ ] `SourceRagOptions.RepositoryUri` exists and is validated for SVN provider
- [ ] `GetFileContentAsync` constructs full URI from `RepositoryUri + "/" + filePath`
- [ ] `GetBlameAsync` uses `SvnUriTarget` with full URI; fetches commit message via log call
- [ ] `GetChangedFilesSinceAsync` returns relative paths, not absolute repository paths
- [ ] `GetFilesAtHeadAsync` uses `SvnUriTarget` with `RepositoryUri`
- [ ] Startup validation fails with a descriptive message if `VcsProvider = "Svn"` and `RepositoryUri` is empty
