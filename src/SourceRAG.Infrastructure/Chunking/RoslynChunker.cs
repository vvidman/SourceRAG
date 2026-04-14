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

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Enums;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.Chunking;

public sealed class RoslynChunker : IChunker
{
    public bool CanHandle(string filePath) =>
        Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<CodeChunk> Chunk(string content, ChunkMetadata baseMetadata)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetRoot();

        var chunks = new List<CodeChunk>();

        foreach (var node in root.DescendantNodes())
        {
            string? symbolName;
            SymbolType symbolType;

            switch (node)
            {
                case MethodDeclarationSyntax m:
                    symbolName = m.Identifier.Text;
                    symbolType = SymbolType.Method;
                    break;
                case ConstructorDeclarationSyntax c:
                    symbolName = c.Identifier.Text;
                    symbolType = SymbolType.Constructor;
                    break;
                case PropertyDeclarationSyntax p:
                    symbolName = p.Identifier.Text;
                    symbolType = SymbolType.Property;
                    break;
                case ClassDeclarationSyntax cls:
                    symbolName = cls.Identifier.Text;
                    symbolType = SymbolType.Class;
                    break;
                case InterfaceDeclarationSyntax iface:
                    symbolName = iface.Identifier.Text;
                    symbolType = SymbolType.Interface;
                    break;
                case StructDeclarationSyntax st:
                    symbolName = st.Identifier.Text;
                    symbolType = SymbolType.Struct;
                    break;
                case EnumDeclarationSyntax en:
                    symbolName = en.Identifier.Text;
                    symbolType = SymbolType.Enum;
                    break;
                default:
                    continue;
            }

            var text     = node.ToFullString();
            var lineSpan = node.GetLocation().GetLineSpan();
            var startLine = lineSpan.StartLinePosition.Line + 1;
            var endLine   = lineSpan.EndLinePosition.Line + 1;

            var metadata = baseMetadata with
            {
                SymbolName = symbolName,
                SymbolType = symbolType,
                StartLine  = startLine,
                EndLine    = endLine
            };

            chunks.Add(new CodeChunk(text, metadata));
        }

        return chunks;
    }
}
