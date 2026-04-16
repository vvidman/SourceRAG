# FIX-003 — Blazor Web: Auth Token Forwarding to REST API

## Problem

`SourceRAG.Web/Program.cs` configures `EnableTokenAcquisitionToCallDownstreamApi()` and `AddDownstreamApi("SourceRagApi", ...)`, but `SourceRagApiClient` uses a plain `HttpClient` that never attaches a Bearer token. Every API call from the Blazor client reaches `SourceRAG.Api` without an `Authorization` header and is rejected with HTTP 401.

---

## Root Cause

```csharp
// Program.cs — token acquisition is wired up:
.EnableTokenAcquisitionToCallDownstreamApi()
.AddDownstreamApi("SourceRagApi", builder.Configuration.GetSection("SourceRagApi"))

// BUT SourceRagApiClient uses HttpClient, not IDownstreamApi:
public sealed class SourceRagApiClient(HttpClient http) { ... }
// → http never gets a Bearer token
```

---

## Fix

Replace the `HttpClient`-based `SourceRagApiClient` with `IDownstreamApi`, which handles token acquisition, caching, and header injection automatically via `Microsoft.Identity.Web`.

### `src/SourceRAG.Web/Services/SourceRagApiClient.cs`

```csharp
using Microsoft.Identity.Web;
using SourceRAG.Web.Models;

namespace SourceRAG.Web.Services;

public sealed class SourceRagApiClient(IDownstreamApi downstreamApi)
{
    private const string ServiceName = "SourceRagApi";

    public async Task<ChatResponse?> ChatAsync(
        string query, int topK = 5, CancellationToken ct = default)
    {
        var request = new ChatRequest(query, topK);
        return await downstreamApi.PostForUserAsync<ChatRequest, ChatResponse>(
            ServiceName,
            request,
            options => options.RelativePath = "chat",
            cancellationToken: ct);
    }

    public async Task<IndexJobResponse?> IndexAsync(
        string mode = "incremental", CancellationToken ct = default)
    {
        var request = new IndexRequest(mode);
        return await downstreamApi.PostForUserAsync<IndexRequest, IndexJobResponse>(
            ServiceName,
            request,
            options => options.RelativePath = "index",
            cancellationToken: ct);
    }

    public async Task<IndexStatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        return await downstreamApi.GetForUserAsync<IndexStatusResponse>(
            ServiceName,
            options => options.RelativePath = "index/status",
            cancellationToken: ct);
    }
}
```

### `src/SourceRAG.Web/Program.cs`

Remove the `AddHttpClient<SourceRagApiClient>` registration. Register `SourceRagApiClient` as a scoped service instead:

```csharp
// REMOVE:
builder.Services.AddHttpClient<SourceRagApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["SourceRagApi:BaseUrl"]
        ?? throw new InvalidOperationException("SourceRagApi:BaseUrl not configured"));
});

// ADD:
builder.Services.AddScoped<SourceRagApiClient>();
```

The `IDownstreamApi` is already available in DI via `AddDownstreamApi(...)` — no additional registration needed.

### `src/SourceRAG.Web/appsettings.json`

The `SourceRagApi` section must include `BaseUrl` and `Scopes`:

```json
{
  "SourceRagApi": {
    "BaseUrl": "https://localhost:7001",
    "Scopes": "api://<server-client-id>/sourcerag.query"
  }
}
```

`IDownstreamApi` uses `BaseUrl` + `RelativePath` to construct the full URL, and `Scopes` to acquire the correct token automatically.

---

## Development Bypass

In `Development` mode the API has a fallback policy that accepts all requests. The Web client in development can use an unauthenticated `HttpClient` to simplify local testing without Entra ID.

Add this to `Program.cs` **before** the `SourceRagApiClient` registration:

```csharp
if (builder.Environment.IsDevelopment())
{
    // Dev-only: plain HttpClient for SourceRagApiClient, no token required
    builder.Services.AddHttpClient<SourceRagApiClient>(client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["SourceRagApi:BaseUrl"]
            ?? "https://localhost:7001");
    });
}
else
{
    builder.Services.AddScoped<SourceRagApiClient>();
}
```

This requires `SourceRagApiClient` to have two constructors — or better, use a factory approach:

```csharp
// Keep both constructors internal:
internal SourceRagApiClient(HttpClient http)        { _impl = new HttpImpl(http); }
internal SourceRagApiClient(IDownstreamApi api)     { _impl = new DownstreamImpl(api); }
```

**Simpler alternative for v1:** Accept that dev mode uses `IDownstreamApi` with a dev Entra ID app registration. This removes the dual-constructor complexity. Recommended if the team has access to a dev Entra ID tenant.

---

## Acceptance Criteria

- [ ] `SourceRagApiClient` uses `IDownstreamApi` in production
- [ ] Bearer token appears in `Authorization` header on all API calls (verify with browser DevTools / Fiddler)
- [ ] `/chat`, `/index`, `/index/status` return HTTP 200 from a signed-in Blazor session
- [ ] Dev environment still functions (either via dev Entra ID or dev bypass)
