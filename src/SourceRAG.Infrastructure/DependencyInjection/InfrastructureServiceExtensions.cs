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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Interfaces;
using SourceRAG.Infrastructure.Chunking;
using SourceRAG.Infrastructure.Embedding.Api;
using SourceRAG.Infrastructure.Embedding.Local;
using SourceRAG.Infrastructure.Llm.Api;
using SourceRAG.Infrastructure.Llm.Local;
using SourceRAG.Infrastructure.Vcs.Auth;
using SourceRAG.Infrastructure.Vcs.Git;
using SourceRAG.Infrastructure.Vcs.State;
using SourceRAG.Infrastructure.Vcs.Svn;
using SourceRAG.Infrastructure.VectorStore;

namespace SourceRAG.Infrastructure.DependencyInjection;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var opts = configuration.GetSection(SourceRagOptions.SectionName).Get<SourceRagOptions>()
            ?? throw new InvalidOperationException("SourceRAG configuration section is missing.");

        ValidateOptions(opts);

        services.Configure<SourceRagOptions>(configuration.GetSection(SourceRagOptions.SectionName));
        services.Configure<ChunkingOptions>(configuration.GetSection("SourceRAG:Chunking"));

        services.AddVcsProvider(opts.VcsProvider);
        services.AddEmbeddingProvider(opts.EmbeddingProvider);
        services.AddLlmProvider(opts.LlmProvider);
        services.AddChunkers();
        services.AddVectorStore(configuration);

        services.AddSingleton<IVcsCredentialProvider, EnvironmentVcsCredentialProvider>();
        services.AddSingleton<IIndexStateStore, FileIndexStateStore>();

        return services;
    }

    private static void ValidateOptions(SourceRagOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.RepositoryPath))
            throw new InvalidOperationException(
                "SourceRAG:RepositoryPath is required and must not be empty.");

        if (!Directory.Exists(opts.RepositoryPath))
            throw new InvalidOperationException(
                $"SourceRAG:RepositoryPath '{opts.RepositoryPath}' does not exist on disk.");

        if (opts.VcsProvider is not ("Git" or "Svn"))
            throw new InvalidOperationException(
                $"SourceRAG:VcsProvider '{opts.VcsProvider}' is invalid. Valid values: Git, Svn.");

        if (opts.VcsProvider == "Svn" && string.IsNullOrWhiteSpace(opts.RepositoryUri))
            throw new InvalidOperationException(
                "SourceRAG:RepositoryUri is required when VcsProvider is 'Svn'. " +
                "Example: https://svn.example.com/repos/myproject/trunk");

        if (opts.EmbeddingProvider == "Local" && string.IsNullOrWhiteSpace(opts.LlamaSharp.ModelPath))
            throw new InvalidOperationException(
                "SourceRAG:LlamaSharp:ModelPath is required when EmbeddingProvider is 'Local'.");

        if (opts.EmbeddingProvider == "Api" &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            throw new InvalidOperationException(
                "Environment variable ANTHROPIC_API_KEY is required when EmbeddingProvider is 'Api'.");

        if (opts.LlmProvider is not ("Anthropic" or "OpenAiCompatible" or "Local"))
            throw new InvalidOperationException(
                $"SourceRAG:LlmProvider '{opts.LlmProvider}' is invalid. " +
                "Valid values: Anthropic, OpenAiCompatible, Local.");

        if (opts.LlmProvider == "Local" &&
            string.IsNullOrWhiteSpace(opts.LlamaSharp.LlmModelPath))
            throw new InvalidOperationException(
                "SourceRAG:LlamaSharp:LlmModelPath is required when LlmProvider is 'Local'.");

        if (opts.LlmProvider == "Anthropic" &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            throw new InvalidOperationException(
                "Environment variable ANTHROPIC_API_KEY is required when LlmProvider is 'Anthropic'.");

        if (opts.LlmProvider == "OpenAiCompatible")
        {
            if (string.IsNullOrWhiteSpace(opts.OpenAiCompatible.BaseUrl))
                throw new InvalidOperationException(
                    "SourceRAG:OpenAiCompatible:BaseUrl is required when LlmProvider is 'OpenAiCompatible'.");

            if (string.IsNullOrWhiteSpace(opts.OpenAiCompatible.Model))
                throw new InvalidOperationException(
                    "SourceRAG:OpenAiCompatible:Model is required when LlmProvider is 'OpenAiCompatible'.");

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOURCERAG_LLM_API_KEY")))
                throw new InvalidOperationException(
                    "Environment variable SOURCERAG_LLM_API_KEY is required when LlmProvider is 'OpenAiCompatible'.");
        }
    }

    private static IServiceCollection AddVcsProvider(
        this IServiceCollection services, string providerType) =>
        providerType switch
        {
            "Git" => services
                .AddSingleton<IVcsProvider, GitVcsProvider>()
                .AddSingleton<IReindexStrategy, GitReindexStrategy>(),
            "Svn" => services
                .AddSingleton<IVcsProvider, SvnVcsProvider>()
                .AddSingleton<IReindexStrategy, SvnReindexStrategy>(),
            _ => throw new InvalidOperationException(
                $"Unknown VcsProvider '{providerType}'. Valid values: Git, Svn.")
        };

    private static IServiceCollection AddEmbeddingProvider(
        this IServiceCollection services, string providerType)
    {
        switch (providerType)
        {
            case "Local":
                services.AddSingleton<IEmbeddingProvider, LlamaSharpEmbeddingProvider>();
                break;
            case "Api":
                services.AddHttpClient("AnthropicEmbedding", client =>
                {
                    var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                        ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");
                    client.DefaultRequestHeaders.Add("x-api-key", apiKey);
                    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
                });
                services.AddSingleton<IEmbeddingProvider, AnthropicEmbeddingProvider>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown EmbeddingProvider '{providerType}'. Valid values: Local, Api.");
        }
        return services;
    }

    private static IServiceCollection AddLlmProvider(
        this IServiceCollection services, string providerType)
    {
        switch (providerType)
        {
            case "Anthropic":
                services.AddSingleton<ILlmProvider, AnthropicLlmProvider>();
                break;

            case "OpenAiCompatible":
                services.AddHttpClient("OpenAiCompatibleLlm");
                services.AddSingleton<ILlmProvider, OpenAiCompatibleLlmProvider>();
                break;

            case "Local":
                services.AddSingleton<ILlmProvider, LlamaSharpLlmProvider>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown LlmProvider '{providerType}'. " +
                    "Valid values: Anthropic, OpenAiCompatible, Local.");
        }

        return services;
    }

    private static IServiceCollection AddChunkers(this IServiceCollection services)
    {
        services.AddSingleton<IChunker, RoslynChunker>();      // first: handles *.cs
        services.AddSingleton<IChunker, PlainTextChunker>();   // last: fallback wildcard
        return services;
    }

    private static IServiceCollection AddVectorStore(
        this IServiceCollection services, IConfiguration configuration)
    {
        var endpoint = configuration["SourceRAG:Qdrant:Endpoint"] ?? "http://localhost:6333";
        services.AddSingleton(_ => new QdrantClient(new Uri(endpoint)));
        services.AddSingleton<IVectorStore, QdrantVectorStore>();
        return services;
    }
}
