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

using Microsoft.Extensions.Options;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Enums;
using SourceRAG.Infrastructure.Chunking;

namespace SourceRAG.Infrastructure.Tests.Chunking;

public sealed class PlainTextChunkerTests
{
    private static PlainTextChunker CreateSut(int chunkSize = 400, int overlap = 80)
    {
        var options = Options.Create(new SourceRagOptions
        {
            VcsProvider       = "Git",
            EmbeddingProvider = "Local",
            RepositoryPath    = "/repo",
            Chunking          = new ChunkingOptions { ChunkSize = chunkSize, Overlap = overlap }
        });
        return new PlainTextChunker(options);
    }

    private static ChunkMetadata BaseMetadata() => new()
    {
        FilePath      = "docs/README.md",
        Revision      = "abc123",
        Author        = "Test Author",
        CommitMessage = "Initial commit",
        Timestamp     = DateTimeOffset.UtcNow,
        Branch        = "main"
    };

    [Fact]
    public void CanHandle_AnyFile_ReturnsTrue()
    {
        var sut = CreateSut();
        Assert.True(sut.CanHandle("readme.txt"));
        Assert.True(sut.CanHandle("file.cs"));
        Assert.True(sut.CanHandle("image.png"));
    }

    [Fact]
    public void Chunk_ShortContent_ReturnsSingleChunk()
    {
        var sut     = CreateSut();
        var content = "Hello world this is a short document.";

        var chunks = sut.Chunk(content, BaseMetadata());

        Assert.Single(chunks);
        Assert.Equal(SymbolType.None, chunks[0].Metadata.SymbolType);
        Assert.Null(chunks[0].Metadata.SymbolName);
    }

    [Fact]
    public void Chunk_NewlineDelimitedContent_ProducesMultipleChunks()
    {
        var sut     = CreateSut(chunkSize: 10, overlap: 4);
        var content = string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"word{i}"));
        var chunks  = sut.Chunk(content, BaseMetadata());
        Assert.True(chunks.Count > 1, "Newline-delimited content should produce multiple chunks");
    }

    [Fact]
    public void Chunk_LongContent_ReturnsMultipleChunksWithOverlap()
    {
        // Use tiny chunk size so we get multiple chunks easily
        var sut = CreateSut(chunkSize: 10, overlap: 4);

        // Generate enough words to force multiple chunks
        // window words ≈ 10 * 0.75 = 7, step ≈ 7 - 4*0.75 ≈ 4
        var words   = Enumerable.Range(1, 40).Select(i => $"word{i}");
        var content = string.Join(' ', words);

        var chunks = sut.Chunk(content, BaseMetadata());

        Assert.True(chunks.Count > 1, $"Expected multiple chunks but got {chunks.Count}");

        // Verify overlap: last word of chunk N should appear in chunk N+1
        var firstChunkWords  = chunks[0].Text.Split(' ');
        var secondChunkWords = chunks[1].Text.Split(' ');
        var lastOfFirst      = firstChunkWords[^1];

        Assert.Contains(lastOfFirst, secondChunkWords);
    }
}
