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

using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Enums;
using SourceRAG.Infrastructure.Vcs.Auth;

namespace SourceRAG.Infrastructure.Tests.Vcs.Auth;

public sealed class EnvironmentVcsCredentialProviderTests : IDisposable
{
    private readonly List<string> _setVars = new();

    private void SetVar(string key, string value)
    {
        Environment.SetEnvironmentVariable(key, value);
        _setVars.Add(key);
    }

    public void Dispose()
    {
        foreach (var key in _setVars)
            Environment.SetEnvironmentVariable(key, null);
    }

    private static EnvironmentVcsCredentialProvider CreateSut() => new();

    [Fact]
    public void Git_WithPat_ReturnsPatCredential()
    {
        SetVar("SOURCERAG_GIT_PAT", "ghp_testtoken");

        var result = CreateSut().Resolve(VcsProviderType.Git);

        var pat = Assert.IsType<PatCredential>(result);
        Assert.Equal("ghp_testtoken", pat.Pat);
    }

    [Fact]
    public void Git_WithSshPath_ReturnsSshCredential()
    {
        SetVar("SOURCERAG_GIT_SSH_KEY_PATH", "/home/user/.ssh/id_rsa");
        SetVar("SOURCERAG_GIT_SSH_PASSPHRASE", "secret");

        var result = CreateSut().Resolve(VcsProviderType.Git);

        var ssh = Assert.IsType<SshCredential>(result);
        Assert.Equal("/home/user/.ssh/id_rsa", ssh.KeyPath);
        Assert.Equal("secret", ssh.Passphrase);
    }

    [Fact]
    public void Git_NoEnvVars_ReturnsNoCredential()
    {
        var result = CreateSut().Resolve(VcsProviderType.Git);

        Assert.IsType<NoCredential>(result);
    }

    [Fact]
    public void Svn_WithUserPass_ReturnsUserPasswordCredential()
    {
        SetVar("SOURCERAG_SVN_USERNAME", "svnuser");
        SetVar("SOURCERAG_SVN_PASSWORD", "svnpass");

        var result = CreateSut().Resolve(VcsProviderType.Svn);

        var up = Assert.IsType<UserPasswordCredential>(result);
        Assert.Equal("svnuser", up.Username);
        Assert.Equal("svnpass", up.Password);
    }

    [Fact]
    public void Svn_NoEnvVars_ReturnsNoCredential()
    {
        var result = CreateSut().Resolve(VcsProviderType.Svn);

        Assert.IsType<NoCredential>(result);
    }
}
