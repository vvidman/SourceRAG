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

namespace SourceRAG.Application.Common;

public sealed class SourceRagOptions
{
    public const string SectionName = "SourceRAG";

    public required string VcsProvider       { get; init; }   // "Git" | "Svn"
    public required string EmbeddingProvider { get; init; }   // "Local" | "Api"
    public required string RepositoryPath    { get; init; }
    public string Branch                     { get; init; } = "main";
    public QdrantOptions Qdrant              { get; init; } = new();
    public LlamaSharpOptions LlamaSharp      { get; init; } = new();
    public AnthropicOptions Anthropic        { get; init; } = new();
    public AzureAdOptions AzureAd            { get; init; } = new();
    public ChunkingOptions Chunking          { get; init; } = new();
}

public sealed class QdrantOptions
{
    public string Endpoint       { get; init; } = "http://localhost:6333";
    public string CollectionName { get; init; } = "sourcerag";
}

public sealed class LlamaSharpOptions
{
    public string ModelPath { get; init; } = string.Empty;
}

public sealed class AnthropicOptions
{
    public string Model { get; init; } = "claude-3-5-haiku-20241022";
}

public sealed class AzureAdOptions
{
    public string Instance { get; init; } = "https://login.microsoftonline.com/";
    public string TenantId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
}

public sealed class ChunkingOptions
{
    public int ChunkSize { get; init; } = 400;
    public int Overlap   { get; init; } = 80;
}
