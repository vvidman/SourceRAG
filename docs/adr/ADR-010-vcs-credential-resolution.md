# ADR-010 — VCS Credential Resolution: Environment Variables + IVcsCredentialProvider

## Status
Accepted

## Date
2025-04-13

## Context

SourceRAG must access source control repositories that may require authentication. Three authentication patterns are relevant:

- **No auth** — local filesystem path, OS-level access control is sufficient
- **HTTPS + PAT** — Personal Access Token for GitHub, Azure DevOps, Gitea
- **SVN username/password** — HTTP/HTTPS Basic auth against an SVN server
- **SSH key** — key-based Git auth, passphrase optional

The VCS credential is a backend-only concern — it is invisible to end users of the Blazor UI or MCP clients. It represents a **service identity**: the SourceRAG backend accessing the repository as a read-only service account.

Credentials must never appear in `appsettings.json` or any file that could be committed to source control. The credential resolution must be replaceable without modifying the VCS provider implementations.

### Read-only service role

The VCS credential should correspond to a **read-only repository role**. SourceRAG never writes to the repository. Granting write access to the service identity is unnecessary and violates the principle of least privilege. In practice:

- GitHub / Azure DevOps: PAT with `repo:read` or `Code (Read)` scope only
- SVN: user with read-only path ACL in `svnserve.conf` or Apache authz
- SSH: deploy key with read-only flag (GitHub supports this natively)

## Decision

VCS credential resolution is encapsulated behind `IVcsCredentialProvider`, defined in the Domain layer:

```csharp
public interface IVcsCredentialProvider
{
    VcsCredential Resolve(VcsProviderType providerType);
}

public abstract record VcsCredential;
public record NoCredential                                    : VcsCredential;
public record PatCredential(string Pat)                      : VcsCredential;
public record UserPasswordCredential(string User, string Password) : VcsCredential;
public record SshCredential(string KeyPath, string? Passphrase)   : VcsCredential;
```

The default implementation resolves credentials from **environment variables** in the following order:

### Git resolution order
1. `SOURCERAG_GIT_PAT` → `PatCredential`
2. `SOURCERAG_GIT_SSH_KEY_PATH` (+ optional `SOURCERAG_GIT_SSH_PASSPHRASE`) → `SshCredential`
3. _(no env vars set)_ → `NoCredential` (local path access)

### SVN resolution order
1. `SOURCERAG_SVN_USERNAME` + `SOURCERAG_SVN_PASSWORD` → `UserPasswordCredential`
2. _(no env vars set)_ → `NoCredential`

```csharp
// Infrastructure/Auth/EnvironmentVcsCredentialProvider.cs
public sealed class EnvironmentVcsCredentialProvider : IVcsCredentialProvider
{
    public VcsCredential Resolve(VcsProviderType type) => type switch
    {
        VcsProviderType.Git => ResolveGit(),
        VcsProviderType.Svn => ResolveSvn(),
        _                   => new NoCredential()
    };

    private static VcsCredential ResolveGit()
    {
        var pat     = Env("SOURCERAG_GIT_PAT");
        var sshPath = Env("SOURCERAG_GIT_SSH_KEY_PATH");
        var sshPass = Env("SOURCERAG_GIT_SSH_PASSPHRASE");

        if (pat     is not null) return new PatCredential(pat);
        if (sshPath is not null) return new SshCredential(sshPath, sshPass);
        return new NoCredential();
    }

    private static VcsCredential ResolveSvn()
    {
        var user = Env("SOURCERAG_SVN_USERNAME");
        var pass = Env("SOURCERAG_SVN_PASSWORD");

        return user is not null && pass is not null
            ? new UserPasswordCredential(user, pass)
            : new NoCredential();
    }

    private static string? Env(string key) =>
        Environment.GetEnvironmentVariable(key);
}
```

`GitVcsProvider` and `SvnVcsProvider` receive `IVcsCredentialProvider` via constructor injection and call `Resolve()` before each repository operation.

## Required Repository Permissions

| Scenario | Minimum permission |
|---|---|
| GitHub PAT | `Contents: Read` |
| Azure DevOps PAT | `Code: Read` |
| Gitea PAT | `repository: read` |
| SVN user | Read ACL on indexed paths |
| SSH deploy key | Read-only deploy key |

## Consequences

**Positive**
- Credentials never appear in any committed file
- VCS providers are decoupled from credential resolution — switching from PAT to SSH requires only env var changes
- `NoCredential` path covers local development with no configuration overhead
- `IVcsCredentialProvider` can be replaced with an OS credential store or secret manager implementation in v2 without touching provider code
- Read-only enforcement is a configuration/permission concern, not a code concern

**Negative**
- Environment variables are process-scoped — in containerised deployments, they must be injected via Docker secrets or Kubernetes secrets
- SSH passphrase in an env variable is less secure than OS keychain storage (acceptable for v1)

## Future (v2)

Replace `EnvironmentVcsCredentialProvider` with a `ChainedVcsCredentialProvider` that tries:
1. Environment variables (current)
2. OS credential store (`Windows Credential Manager` / `libsecret`)
3. Azure Key Vault (for production deployments)

No provider code changes required — only a new `IVcsCredentialProvider` implementation.
