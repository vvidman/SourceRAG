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

namespace SourceRAG.Domain.Tests;

public class VcsCredentialTests
{
    [Fact]
    public void PatternMatching_ExhaustsAllSubtypes()
    {
        VcsCredential[] credentials =
        [
            new NoCredential(),
            new PatCredential("token"),
            new UserPasswordCredential("user", "pass"),
            new SshCredential("/key", null)
        ];

        var results = credentials.Select(c => c switch
        {
            NoCredential              => "none",
            PatCredential             => "pat",
            UserPasswordCredential    => "userpass",
            SshCredential             => "ssh",
            _                         => throw new InvalidOperationException("unhandled")
        }).ToArray();

        Assert.Equal(["none", "pat", "userpass", "ssh"], results);
    }

    [Fact]
    public void NoCredential_IsSubtypeOfVcsCredential()
    {
        VcsCredential cred = new NoCredential();
        Assert.IsAssignableFrom<VcsCredential>(cred);
    }

    [Fact]
    public void PatCredential_ExposesPatProperty()
    {
        var cred = new PatCredential("my-token");
        Assert.Equal("my-token", cred.Pat);
    }

    [Fact]
    public void SshCredential_PassphraseIsNullable()
    {
        var withPassphrase    = new SshCredential("/key", "secret");
        var withoutPassphrase = new SshCredential("/key", null);

        Assert.Equal("secret", withPassphrase.Passphrase);
        Assert.Null(withoutPassphrase.Passphrase);
    }
}
