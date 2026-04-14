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
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.Vcs.Auth;

public sealed class EnvironmentVcsCredentialProvider : IVcsCredentialProvider
{
    public VcsCredential Resolve(VcsProviderType providerType) => providerType switch
    {
        VcsProviderType.Git => ResolveGit(),
        VcsProviderType.Svn => ResolveSvn(),
        _                   => new NoCredential()
    };

    private static VcsCredential ResolveGit()
    {
        var pat = Environment.GetEnvironmentVariable("SOURCERAG_GIT_PAT");
        if (!string.IsNullOrWhiteSpace(pat))
            return new PatCredential(pat);

        var sshKeyPath = Environment.GetEnvironmentVariable("SOURCERAG_GIT_SSH_KEY_PATH");
        if (!string.IsNullOrWhiteSpace(sshKeyPath))
        {
            var passphrase = Environment.GetEnvironmentVariable("SOURCERAG_GIT_SSH_PASSPHRASE");
            return new SshCredential(sshKeyPath, passphrase);
        }

        return new NoCredential();
    }

    private static VcsCredential ResolveSvn()
    {
        var username = Environment.GetEnvironmentVariable("SOURCERAG_SVN_USERNAME");
        var password = Environment.GetEnvironmentVariable("SOURCERAG_SVN_PASSWORD");
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            return new UserPasswordCredential(username, password);

        return new NoCredential();
    }
}
