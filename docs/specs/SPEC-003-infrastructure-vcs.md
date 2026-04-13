# SPEC-003 — Infrastructure: VCS Providers (Git + SVN)

## Overview
Implement both VCS vertical slices: Git (LibGit2Sharp) and SVN (SharpSvn), each as a paired `IVcsProvider` + `IReindexStrategy`. Also implement `IVcsCredentialProvider` and `IIndexStateStore`.

## References
- ADR-001 (provider+strategy pairing)
- ADR-002 (VCS as proof store)
- ADR-005 (main/trunk scope only)
- ADR-010 (credential resolution)

## Project
`src/SourceRAG.Infrastructure`

## NuGet packages to add
```
LibGit2Sharp
SharpSvn
Microsoft.Extensions.Options
Microsoft.Extensions.Logging.Abstractions
```

## Rules
- Delete `Class1.cs`.
- `GitVcsProvider` and `GitReindexStrategy` are registered as a pair — never mixed with SVN counterparts.
- Both providers are scoped to `main`/`trunk` branch only (ADR-005).
- File content reconstruction must use the stored `revision`, not HEAD.

---

## VCS Credential Provider

### `Vcs/Auth/EnvironmentVcsCredentialProvider.cs`
Implements `IVcsCredentialProvider` (ADR-010).

Resolution order:

**Git:**
1. `SOURCERAG_GIT_PAT` → `PatCredential`
2. `SOURCERAG_GIT_SSH_KEY_PATH` (+ optional `SOURCERAG_GIT_SSH_PASSPHRASE`) → `SshCredential`
3. fallback → `NoCredential`

**SVN:**
1. `SOURCERAG_SVN_USERNAME` + `SOURCERAG_SVN_PASSWORD` → `UserPasswordCredential`
2. fallback → `NoCredential`

---

## Git

### `Vcs/Git/GitVcsProvider.cs`
Implements `IVcsProvider`.

Constructor injects:
- `IVcsCredentialProvider`
- `IOptions<SourceRagOptions>`
- `ILogger<GitVcsProvider>`

Methods:

**`GetCurrentRevision(repoPath)`**
Use `new Repository(repoPath).Head.Tip.Sha`.

**`GetFilesAtHeadAsync(repoPath, ct)`**
Enumerate `repo.Head.Tip.Tree` recursively. Return `VcsFile` for each blob (skip submodules). Set `Revision = repo.Head.Tip.Sha`.

**`GetFileContentAsync(repoPath, filePath, revision, ct)`**
Use `repo.Lookup<Blob>(repo.Lookup<Commit>(revision)[filePath].Target.Id)`. Return `blob.GetContentText()`.

**`GetBlameAsync(repoPath, filePath, revision, ct)`**
Use `repo.Blame(filePath, new BlameOptions { StartingAt = revision })`. Take the first hunk (line 1) for `Author` and `CommitMessage`. Return `FileBlameInfo`.

**`GetChangedFilesSinceAsync(repoPath, sinceRevision, ct)`**
Compare `repo.Lookup<Commit>(sinceRevision).Tree` with `repo.Head.Tip.Tree` using `repo.Diff.Compare<TreeChanges>`. Map `TreeEntryChanges` to `ChangedFile` with appropriate `ChangeType`.

Credential application: on `CloneOptions` / `FetchOptions`, apply credential from `IVcsCredentialProvider.Resolve(VcsProviderType.Git)` using `UsernamePasswordCredentials` for PAT, or `SshUserKeyCredentials` for SSH.

### `Vcs/Git/GitReindexStrategy.cs`
Implements `IReindexStrategy`.

Constructor injects:
- `IVcsProvider` (use `GitVcsProvider` — same assembly, but inject via interface)
- `IOptions<SourceRagOptions>`

**`DetermineChangedFilesAsync(repoPath, lastIndexedRevision, ct)`**
Call `IVcsProvider.GetChangedFilesSinceAsync(repoPath, lastIndexedRevision, ct)`. Wrap result in `ReindexScope(changedFiles, lastIndexedRevision, currentRevision)`.

---

## SVN

### `Vcs/Svn/SvnVcsProvider.cs`
Implements `IVcsProvider`.

Constructor injects:
- `IVcsCredentialProvider`
- `IOptions<SourceRagOptions>`
- `ILogger<SvnVcsProvider>`

Use `SharpSvn.SvnClient`. Apply `UserPasswordCredential` via `client.Authentication.UserNamePasswordHandlers`.

**`GetCurrentRevision(repoPath)`**
Use `client.GetInfo(repoPath, out SvnInfoEventArgs info)`. Return `info.Revision.ToString()`.

**`GetFilesAtHeadAsync(repoPath, ct)`**
Use `client.GetList(repoPath, new SvnListArgs { Depth = SvnDepth.Infinity }, out var list)`. Filter to files only. Return `VcsFile` per entry with current revision.

**`GetFileContentAsync(repoPath, filePath, revision, ct)`**
Use `client.Write(new SvnUriTarget(filePath, long.Parse(revision)), stream)`. Return stream content as string.

**`GetBlameAsync(repoPath, filePath, revision, ct)`**
Use `client.GetAnnotation(filePath, ...)`. Take the first annotated line for `Author` and `Revision`. Return `FileBlameInfo`.

**`GetChangedFilesSinceAsync(repoPath, sinceRevision, ct)`**
Use `client.GetLog(repoPath, new SvnLogArgs { Start = SvnRevision.Parse(sinceRevision), End = SvnRevision.Head }, ...)`. Extract `SvnChangeItem` entries. Map to `ChangedFile`.

### `Vcs/Svn/SvnReindexStrategy.cs`
Implements `IReindexStrategy`. Same pattern as `GitReindexStrategy`.

---

## Index State Store

### `Vcs/State/FileIndexStateStore.cs`
Implements `IIndexStateStore`.

Persists last-indexed state to a JSON file in the repo path (`.sourcerag-state.json`). This is the simplest durable store that requires no external dependency.

State file structure:
```json
{
  "LastIndexedRevision": "a3f9c12",
  "LastIndexedAt": "2025-04-13T10:00:00Z"
}
```

Methods read/write this file atomically (write to temp file, then rename).

---

## Tests — `tests/SourceRAG.Infrastructure.Tests`

Delete `UnitTest1.cs`. These tests require a local git repository fixture.

### `Vcs/Git/GitVcsProviderTests.cs`
Create a temporary git repo in `TestContext.CurrentContext.TestDirectory` using LibGit2Sharp in `[SetUp]`:
- `GetCurrentRevision_ReturnsCommitHash`
- `GetFilesAtHead_ReturnsAllFiles`
- `GetFileContent_ReturnsCorrectContent`
- `GetChangedFilesSince_ReturnsModifiedFile`

### `Vcs/Auth/EnvironmentVcsCredentialProviderTests.cs`
Set and clear env variables per test:
- `Git_WithPat_ReturnsPatCredential`
- `Git_WithSshPath_ReturnsSshCredential`
- `Git_NoEnvVars_ReturnsNoCredential`
- `Svn_WithUserPass_ReturnsUserPasswordCredential`
- `Svn_NoEnvVars_ReturnsNoCredential`

### `Vcs/State/FileIndexStateStoreTests.cs`
- `SetAndGet_LastIndexedRevision_RoundTrips`
- `Get_BeforeSet_ReturnsNull`
