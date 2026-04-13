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

namespace SourceRAG.Domain.Tests;

public class ChunkMetadataTests
{
    private static ChunkMetadata CreateDefault() => new()
    {
        FilePath      = "src/Foo.cs",
        Revision      = "abc123",
        Author        = "alice",
        CommitMessage = "initial",
        Timestamp     = DateTimeOffset.UtcNow,
        Branch        = "main"
    };

    [Fact]
    public void WithExpression_ProducesNewInstance_OriginalUnchanged()
    {
        var original = CreateDefault();
        var modified = original with { Author = "bob" };

        Assert.NotSame(original, modified);
        Assert.Equal("alice", original.Author);
        Assert.Equal("bob", modified.Author);
    }

    [Fact]
    public void SymbolType_DefaultsToNone()
    {
        var metadata = CreateDefault();
        Assert.Equal(SymbolType.None, metadata.SymbolType);
    }

    [Fact]
    public void SymbolName_DefaultsToNull()
    {
        var metadata = CreateDefault();
        Assert.Null(metadata.SymbolName);
    }
}
