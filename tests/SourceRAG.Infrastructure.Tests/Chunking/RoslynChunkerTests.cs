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
using SourceRAG.Infrastructure.Chunking;

namespace SourceRAG.Infrastructure.Tests.Chunking;

public sealed class RoslynChunkerTests
{
    private readonly RoslynChunker _sut = new();

    private static ChunkMetadata BaseMetadata() => new()
    {
        FilePath      = "src/Foo.cs",
        Revision      = "abc123",
        Author        = "Test Author",
        CommitMessage = "Initial commit",
        Timestamp     = DateTimeOffset.UtcNow,
        Branch        = "main"
    };

    [Fact]
    public void CanHandle_CsFile_ReturnsTrue()
    {
        Assert.True(_sut.CanHandle("Foo.cs"));
        Assert.True(_sut.CanHandle("path/to/Bar.CS"));
    }

    [Fact]
    public void CanHandle_TxtFile_ReturnsFalse()
    {
        Assert.False(_sut.CanHandle("readme.txt"));
        Assert.False(_sut.CanHandle("file.md"));
    }

    [Fact]
    public void Chunk_SimpleClass_ReturnsClassChunk()
    {
        const string code = """
            public class MyClass
            {
            }
            """;

        var chunks = _sut.Chunk(code, BaseMetadata());

        var classChunk = chunks.FirstOrDefault(c => c.Metadata.SymbolType == SymbolType.Class);
        Assert.NotNull(classChunk);
        Assert.Equal("MyClass", classChunk.Metadata.SymbolName);
    }

    [Fact]
    public void Chunk_MethodInsideClass_ReturnsMethodChunk()
    {
        const string code = """
            public class MyClass
            {
                public void DoWork() { }
            }
            """;

        var chunks = _sut.Chunk(code, BaseMetadata());

        var methodChunk = chunks.FirstOrDefault(c => c.Metadata.SymbolType == SymbolType.Method);
        Assert.NotNull(methodChunk);
        Assert.Equal("DoWork", methodChunk.Metadata.SymbolName);
    }

    [Fact]
    public void Chunk_EmptyFile_ReturnsEmpty()
    {
        var chunks = _sut.Chunk(string.Empty, BaseMetadata());

        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_MetadataSymbolNameMatchesSyntaxNodeIdentifier()
    {
        const string code = """
            public interface IMyService
            {
                void Execute();
            }
            """;

        var chunks = _sut.Chunk(code, BaseMetadata());

        var interfaceChunk = chunks.FirstOrDefault(c => c.Metadata.SymbolType == SymbolType.Interface);
        Assert.NotNull(interfaceChunk);
        Assert.Equal("IMyService", interfaceChunk.Metadata.SymbolName);
    }
}
