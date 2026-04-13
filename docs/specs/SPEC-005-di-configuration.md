# SPEC-005 — DI Configuration & Provider Registration

## Overview
Implement the shared DI bootstrap logic that wires up all providers based on configuration, and bootstrap both host projects (Api + McpHost).

## References
- ADR-001 (provider+strategy pairing enforcement)
- ADR-004 (embedding provider config-driven)
- ADR-008 (dual hosting)
- ADR-011 (authentication)

## Projects
- `src/SourceRAG.Infrastructure` (registration extensions)
- `src/SourceRAG.Api` (REST host bootstrap)
- `src/SourceRAG.McpHost` (MCP host bootstrap)

## NuGet packages to add to Infrastructure
```
Microsoft.Extensions.DependencyInjection.Abstractions
Qdrant.Client
```

## NuGet packages to add to Api
```
Microsoft.Identity.Web
Microsoft.AspNetCore.Authentication.JwtBearer
```

## NuGet packages to add to McpHost
```
ModelContextProtocol
Microsoft.Identity.Web
Microsoft.AspNetCore.Authentication.JwtBearer
```

---

## Infrastructure: Provider Registration

### `DependencyInjection/InfrastructureServiceExtensions.cs`

```csharp
public static IServiceCollection AddInfrastructure(
    this IServiceCollection services,
    IConfiguration configuration)
{
    var opts = configuration.GetSection(SourceRagOptions.SectionName).Get<SourceRagOptions>()
        ?? throw new InvalidOperationException("SourceRAG configuration section is missing.");

    services.Configure<SourceRagOptions>(configuration.GetSection(SourceRagOptions.SectionName));
    services.Configure<ChunkingOptions>(configuration.GetSection("SourceRAG:Chunking"));

    services.AddVcsProvider(opts.VcsProvider);
    services.AddEmbeddingProvider(opts.EmbeddingProvider);
    services.AddChunkers();
    services.AddVectorStore(configuration);

    services.AddSingleton<IVcsCredentialProvider, EnvironmentVcsCredentialProvider>();
    services.AddSingleton<IIndexStateStore, FileIndexStateStore>();

    return services;
}
```

### VCS provider registration (enforced pairing, ADR-001)

```csharp
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
```

### Embedding provider registration (ADR-004)

```csharp
private static IServiceCollection AddEmbeddingProvider(
    this IServiceCollection services, string providerType) =>
    providerType switch
    {
        "Local" => services.AddSingleton<IEmbeddingProvider, LlamaSharpEmbeddingProvider>(),
        "Api"   => services.AddSingleton<IEmbeddingProvider, AnthropicEmbeddingProvider>(),
        _ => throw new InvalidOperationException(
            $"Unknown EmbeddingProvider '{providerType}'. Valid values: Local, Api.")
    };
```

### Chunker registration (Chain of Responsibility — order matters, ADR-003)

```csharp
private static IServiceCollection AddChunkers(this IServiceCollection services)
{
    services.AddSingleton<IChunker, RoslynChunker>();      // first: handles *.cs
    services.AddSingleton<IChunker, PlainTextChunker>();   // last: fallback wildcard
    return services;
}
```

### Vector store registration

```csharp
private static IServiceCollection AddVectorStore(
    this IServiceCollection services, IConfiguration configuration)
{
    var endpoint = configuration["SourceRAG:Qdrant:Endpoint"] ?? "http://localhost:6333";
    services.AddSingleton(_ => new QdrantClient(new Uri(endpoint)));
    services.AddSingleton<IVectorStore, QdrantVectorStore>();
    return services;
}
```

---

## Api Host: `src/SourceRAG.Api/Program.cs`

Replace the default weather forecast template entirely.

```csharp
var builder = WebApplication.CreateBuilder(args);

// Options
builder.Services.Configure<SourceRagOptions>(
    builder.Configuration.GetSection(SourceRagOptions.SectionName));

// Application + Infrastructure
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Auth (ADR-011)
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CanQuery", p => p.RequireClaim("scp", "sourcerag.query"));
    options.AddPolicy("CanIndex", p => p.RequireClaim("scp", "sourcerag.index"));
});

// Dev bypass (never runs in Production)
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthorization(options =>
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAssertion(_ => true).Build());
}

builder.Services.AddOpenApi();

var app = builder.Build();

// Ensure Qdrant collection on startup
using (var scope = app.Services.CreateScope())
{
    var vectorStore      = scope.ServiceProvider.GetRequiredService<IVectorStore>();
    var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
    await vectorStore.EnsureCollectionAsync(embeddingProvider.Dimensions, CancellationToken.None);
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Endpoints (thin — delegate immediately to MediatR)
app.MapPost("/chat", async (ChatQueryCommand cmd, IMediator mediator, CancellationToken ct) =>
    Results.Ok(await mediator.Send(cmd, ct)))
    .RequireAuthorization("CanQuery");

app.MapPost("/index", async (IndexRepositoryCommand cmd, IMediator mediator, CancellationToken ct) =>
    Results.Ok(await mediator.Send(cmd, ct)))
    .RequireAuthorization("CanIndex");

app.MapGet("/index/status", async (IMediator mediator, CancellationToken ct) =>
    Results.Ok(await mediator.Send(new GetIndexStatusQuery(), ct)))
    .RequireAuthorization("CanQuery");

app.Run();
```

---

## McpHost: `src/SourceRAG.McpHost/Program.cs`

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SourceRagOptions>(
    builder.Configuration.GetSection(SourceRagOptions.SectionName));

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Auth (ADR-011) — same JWT validation as Api
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
    options.AddPolicy("McpAccess", p => p.RequireClaim("scp", "sourcerag.query")));

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthorization(options =>
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAssertion(_ => true).Build());
}

// MCP server (HTTP/SSE transport — not stdio, auth requires HTTP)
builder.Services.AddMcpServer()
    .WithTools<SearchCodebaseTool>()
    .WithTools<IndexRepositoryTool>()
    .WithTools<GetIndexStatusTool>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapMcp("/mcp").RequireAuthorization("McpAccess");

app.Run();
```

---

## MCP Tools

### `Tools/SearchCodebaseTool.cs`
```csharp
[McpServerToolType]
public sealed class SearchCodebaseTool(IMediator mediator)
{
    [McpServerTool, Description("Semantic search over the indexed source repository")]
    public async Task<QueryResult> SearchAsync(
        [Description("Natural language query")] string query,
        [Description("Max results")] int topK = 5,
        CancellationToken ct = default)
        => await mediator.Send(new ChatQueryCommand(query, topK), ct);
}
```

### `Tools/IndexRepositoryTool.cs`
```csharp
[McpServerToolType]
public sealed class IndexRepositoryTool(IMediator mediator)
{
    [McpServerTool, Description("Trigger full or incremental reindex of the repository")]
    public async Task<IndexJobResult> IndexAsync(
        [Description("'full' or 'incremental'")] string mode = "incremental",
        CancellationToken ct = default)
        => await mediator.Send(new IndexRepositoryCommand(mode == "full"), ct);
}
```

### `Tools/GetIndexStatusTool.cs`
```csharp
[McpServerToolType]
public sealed class GetIndexStatusTool(IMediator mediator)
{
    [McpServerTool, Description("Return current index state")]
    public async Task<IndexStatus> GetStatusAsync(CancellationToken ct = default)
        => await mediator.Send(new GetIndexStatusQuery(), ct);
}
```

---

## appsettings.json Templates

### `src/SourceRAG.Api/appsettings.json`
```json
{
  "SourceRAG": {
    "VcsProvider": "Git",
    "EmbeddingProvider": "Local",
    "RepositoryPath": "",
    "Branch": "main",
    "Qdrant": {
      "Endpoint": "http://localhost:6333",
      "CollectionName": "sourcerag"
    },
    "LlamaSharp": {
      "ModelPath": ""
    },
    "Anthropic": {
      "Model": "claude-3-5-haiku-20241022"
    }
  },
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "",
    "ClientId": "",
    "Audience": ""
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

Same structure for `src/SourceRAG.McpHost/appsettings.json`.

---

## Validation

Add a startup validation step to both hosts that throws `InvalidOperationException` with a descriptive message if:
- `RepositoryPath` is empty or does not exist on disk
- `VcsProvider` is not `Git` or `Svn`
- `EmbeddingProvider` is `Local` and `ModelPath` is empty
- `EmbeddingProvider` is `Api` and `ANTHROPIC_API_KEY` env var is not set
