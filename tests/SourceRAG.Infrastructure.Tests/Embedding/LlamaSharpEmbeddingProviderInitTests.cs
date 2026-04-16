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

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SourceRAG.Application.Common;
using SourceRAG.Infrastructure.Embedding.Local;

namespace SourceRAG.Infrastructure.Tests.Embedding;

public sealed class LlamaSharpEmbeddingProviderInitTests
{
    [Fact(Skip = "Requires local GGUF model — run manually")]
    public async Task InitializeAsync_SetsDimensionsFromModel()
    {
        var options = Options.Create(new SourceRagOptions
        {
            VcsProvider       = "Git",
            EmbeddingProvider = "Local",
            RepositoryPath    = "/tmp",
            LlamaSharp        = new LlamaSharpOptions { ModelPath = "/models/nomic-embed-text.gguf" }
        });

        await using var provider = new LlamaSharpEmbeddingProvider(
            options, NullLogger<LlamaSharpEmbeddingProvider>.Instance);

        Assert.Equal(768, provider.Dimensions); // default before init

        await provider.InitializeAsync(CancellationToken.None);

        Assert.True(provider.Dimensions > 0);
        Assert.NotEqual(0, provider.Dimensions);
    }
}
