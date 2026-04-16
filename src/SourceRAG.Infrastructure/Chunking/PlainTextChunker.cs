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
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.Chunking;

public sealed class PlainTextChunker : IChunker
{
    private readonly ChunkingOptions _options;

    public PlainTextChunker(IOptions<SourceRagOptions> options)
    {
        _options = options.Value.Chunking;
    }

    public bool CanHandle(string filePath) => true;

    public IReadOnlyList<CodeChunk> Chunk(string content, ChunkMetadata baseMetadata)
    {
        // Approximate: 1 token ≈ 0.75 words  →  window size in words = ChunkSize * 0.75
        var windowWords = (int)(_options.ChunkSize * 0.75);
        var stepWords   = windowWords - (int)(_options.Overlap * 0.75);
        if (stepWords <= 0) stepWords = 1;

        var words = content.Split(
            new[] { ' ', '\n', '\r', '\t' },
            StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
            return [];

        var chunks     = new List<CodeChunk>();
        var chunkIndex = 0;
        var wordIndex  = 0;

        while (wordIndex < words.Length)
        {
            var windowEnd   = Math.Min(wordIndex + windowWords, words.Length);
            var windowText  = string.Join(' ', words, wordIndex, windowEnd - wordIndex);

            var charOffset  = CharOffsetOfWord(content, wordIndex, words);
            var startLine   = CountLines(content, 0, charOffset) + 1;
            var endOffset   = CharOffsetOfWord(content, wordIndex, words) + windowText.Length;
            var endLine     = CountLines(content, 0, Math.Min(endOffset, content.Length)) + 1;

            var metadata = baseMetadata with
            {
                SymbolName = null,
                SymbolType = SymbolType.None,
                StartLine  = startLine,
                EndLine    = endLine
            };

            chunks.Add(new CodeChunk(windowText, metadata));

            wordIndex  += stepWords;
            chunkIndex++;
        }

        return chunks;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static int CharOffsetOfWord(string content, int wordIndex, string[] words)
    {
        if (wordIndex == 0) return 0;
        var pos   = 0;
        var found = 0;
        while (pos < content.Length && found < wordIndex)
        {
            // skip non-whitespace (current word)
            while (pos < content.Length && !char.IsWhiteSpace(content[pos])) pos++;
            // skip whitespace (separator)
            while (pos < content.Length && char.IsWhiteSpace(content[pos])) pos++;
            found++;
        }
        return Math.Min(pos, content.Length);
    }

    private static int CountLines(string content, int start, int end)
    {
        var count = 0;
        for (var i = start; i < end && i < content.Length; i++)
        {
            if (content[i] == '\n')
                count++;
        }
        return count;
    }
}
