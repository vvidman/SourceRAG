# FIX-010 — Trivial Fixes: ValidateOptions + Web Dev Bypass

## Scope
Two independent, low-risk fixes applied in a single pass.

---

## Fix A — ValidateOptions: Unknown EmbeddingProvider value

### Priority
🟡 P3 — Consistency gap. Unknown `EmbeddingProvider` values are caught at DI build time (by `AddEmbeddingProvider`), not at `ValidateOptions` time. The VcsProvider and LlmProvider both have explicit value guards in `ValidateOptions` — `EmbeddingProvider` should be consistent.

### File
`src/SourceRAG.Infrastructure/DependencyInjection/InfrastructureServiceExtensions.cs`

### Change

Add the missing guard inside `ValidateOptions`, immediately after the VcsProvider check:

```csharp
// BEFORE — EmbeddingProvider has no exhaustive check in ValidateOptions:
if (opts.EmbeddingProvider == "Local" && string.IsNullOrWhiteSpace(opts.LlamaSharp.ModelPath))
    throw new InvalidOperationException(...);

if (opts.EmbeddingProvider == "Api" && ...)
    throw new InvalidOperationException(...);

// AFTER — add exhaustive value check first, then the specific guards:
if (opts.EmbeddingProvider is not ("Local" or "Api"))
    throw new InvalidOperationException(
        $"SourceRAG:EmbeddingProvider '{opts.EmbeddingProvider}' is invalid. " +
        "Valid values: Local, Api.");

if (opts.EmbeddingProvider == "Local" && string.IsNullOrWhiteSpace(opts.LlamaSharp.ModelPath))
    throw new InvalidOperationException(
        "SourceRAG:LlamaSharp:ModelPath is required when EmbeddingProvider is 'Local'.");

if (opts.EmbeddingProvider == "Api" &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
    throw new InvalidOperationException(
        "Environment variable ANTHROPIC_API_KEY is required when EmbeddingProvider is 'Api'.");
```

### After this change, the full ValidateOptions guard set is symmetric:

| Config key | Value validation | Specific guards |
|---|---|---|
| `VcsProvider` | ✅ `"Git" or "Svn"` | ✅ Svn requires RepositoryUri |
| `EmbeddingProvider` | ✅ `"Local" or "Api"` (after fix) | ✅ Local requires ModelPath; Api requires API key |
| `LlmProvider` | ✅ `"Anthropic" or "OpenAiCompatible" or "Local"` | ✅ All three have specific guards |

---

## Fix B — Web Dev Bypass: Document and implement

### Priority
🟡 P3 — Developer experience. `IDownstreamApi` requires a valid Entra ID token even in Development. Without a dev Entra ID tenant or app registration, the Blazor web client cannot start up locally.

### Problem

```csharp
// SourceRAG.Web/Program.cs — IDownstreamApi requires token acquisition from Entra ID:
.AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
.EnableTokenAcquisitionToCallDownstreamApi()
.AddDownstreamApi("SourceRagApi", builder.Configuration.GetSection("SourceRagApi"))
```

`IDownstreamApi.PostForUserAsync` calls `ITokenAcquisition` internally, which hits the Azure AD token endpoint. If `TenantId` or `ClientId` in `AzureAd` config is empty, it throws at runtime even before the first user action.

### File
`src/SourceRAG.Web/Program.cs`

### Change

Add a conditional dev-mode `HttpClient`-based fallback for `SourceRagApiClient`:

```csharp
var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    // Dev mode: bypass Entra ID, use plain HttpClient (API has FallbackPolicy = AllowAll in dev)
    builder.Services.AddAuthentication();
    builder.Services.AddAuthorization();
    builder.Services.AddHttpClient<SourceRagApiClient>(client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration["SourceRagApi:BaseUrl"] ?? "https://localhost:7001");
    });
}
else
{
    // Production: full Entra ID OIDC with token forwarding
    builder.Services
        .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi()
        .AddDownstreamApi("SourceRagApi", builder.Configuration.GetSection("SourceRagApi"))
        .AddInMemoryTokenCaches();

    builder.Services.AddAuthorization();
    builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();
    builder.Services.AddScoped<SourceRagApiClient>();
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ... rest unchanged
```

### `SourceRagApiClient` — dual-constructor support

`SourceRagApiClient` currently accepts only `IDownstreamApi`. Add a second constructor for the `HttpClient` fallback:

```csharp
public sealed class SourceRagApiClient
{
    private const string ServiceName = "SourceRagApi";
    private readonly IDownstreamApi? _downstreamApi;
    private readonly HttpClient?     _httpClient;

    // Production constructor
    public SourceRagApiClient(IDownstreamApi downstreamApi)
    {
        _downstreamApi = downstreamApi;
    }

    // Dev constructor — plain HttpClient, no token
    public SourceRagApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ChatResponse?> ChatAsync(
        string query, int topK = 5, CancellationToken ct = default)
    {
        if (_downstreamApi is not null)
        {
            return await _downstreamApi.PostForUserAsync<ChatRequest, ChatResponse>(
                ServiceName,
                new ChatRequest(query, topK),
                options => options.RelativePath = "chat",
                cancellationToken: ct);
        }

        var response = await _httpClient!.PostAsJsonAsync("chat", new ChatRequest(query, topK), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ChatResponse>(ct);
    }

    public async Task<IndexJobResponse?> IndexAsync(
        string mode = "incremental", CancellationToken ct = default)
    {
        if (_downstreamApi is not null)
        {
            return await _downstreamApi.PostForUserAsync<IndexRequest, IndexJobResponse>(
                ServiceName,
                new IndexRequest(mode),
                options => options.RelativePath = "index",
                cancellationToken: ct);
        }

        var response = await _httpClient!.PostAsJsonAsync("index", new IndexRequest(mode), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IndexJobResponse>(ct);
    }

    public async Task<IndexStatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        if (_downstreamApi is not null)
        {
            return await _downstreamApi.GetForUserAsync<IndexStatusResponse>(
                ServiceName,
                options => options.RelativePath = "index/status",
                cancellationToken: ct);
        }

        return await _httpClient!.GetFromJsonAsync<IndexStatusResponse>("index/status", ct);
    }
}
```

### `src/SourceRAG.Web/appsettings.Development.json`

Add the API base URL for dev mode:

```json
{
  "SourceRagApi": {
    "BaseUrl": "https://localhost:7001"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

### `src/SourceRAG.Web/appsettings.json`

Ensure the production section has placeholder values:

```json
{
  "AzureAd": {
    "Instance":     "https://login.microsoftonline.com/",
    "TenantId":     "",
    "ClientId":     "",
    "ClientSecret": "",
    "CallbackPath": "/signin-oidc"
  },
  "SourceRagApi": {
    "BaseUrl": "",
    "Scopes":  "api://<server-client-id>/sourcerag.query"
  }
}
```

---

## Acceptance Criteria

### Fix A
- [ ] `ValidateOptions` contains `opts.EmbeddingProvider is not ("Local" or "Api")` guard
- [ ] Setting `EmbeddingProvider = "Unknown"` throws a descriptive error at startup
- [ ] All three config keys (VcsProvider, EmbeddingProvider, LlmProvider) have symmetric value validation

### Fix B
- [ ] In `Development`, `SourceRagApiClient` uses plain `HttpClient` — no Entra ID calls
- [ ] In `Production`, `SourceRagApiClient` uses `IDownstreamApi` with token forwarding
- [ ] Starting `SourceRAG.Web` in dev mode with empty `AzureAd` config does not throw
- [ ] `appsettings.Development.json` contains `SourceRagApi:BaseUrl`
- [ ] Solution builds without errors
