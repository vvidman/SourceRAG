# ADR-011 — Authentication: Blazor Web UI + MCP Server via Azure AD / Entra ID (OAuth 2.0)

## Status
Accepted

## Date
2025-04-13

## Context

SourceRAG is deployed as an internal team tool on a private network. Two surfaces require authentication:

1. **Blazor Web UI** — human users accessing the chat interface via browser
2. **MCP Server** — AI agent clients (Claude Desktop, GitHub Copilot) accessing tools over HTTP/SSE

Both surfaces must ensure that only authorised users can query the indexed codebase or trigger reindex operations. The organisation already operates **Azure Active Directory / Microsoft Entra ID** as its identity platform.

Using a single identity provider for both surfaces means:
- One set of app registrations and role assignments to manage
- Tokens issued by the same authority, validated by the same middleware
- Consistent audit trail in Entra ID sign-in logs

### Why not API keys for these surfaces?

API key approaches were considered and rejected for Web UI and MCP:
- No identity — an API key identifies a credential, not a person
- No MFA enforcement
- No centralised revocation (key rotation is manual)
- No audit trail tied to individual users
- Entra ID is already available — introducing a parallel credential system adds complexity without benefit

VCS credentials remain env-var based (ADR-010) because they represent a service identity, not a human identity, and the repository's own access control enforces read-only scope.

## Decision

Both `SourceRAG.Api` (REST, consumed by Blazor Web) and `SourceRAG.McpHost` (MCP, consumed by AI agents) are protected with **Azure AD / Entra ID OAuth 2.0 / OIDC**.

---

### Entra ID App Registrations

Two app registrations are required:

**Registration 1: `SourceRAG-Server`**
- Exposes an API scope: `api://<client-id>/sourcerag.query`
- Exposes an API scope: `api://<client-id>/sourcerag.index`
- App roles (optional, for RBAC): `SourceRAG.Reader`, `SourceRAG.Indexer`

**Registration 2: `SourceRAG-WebClient`**
- Type: Single Page Application / Web (Blazor Server)
- Redirect URI: `https://<internal-host>/signin-oidc`
- Requests scopes: `openid`, `profile`, `api://<server-client-id>/sourcerag.query`

AI agent MCP clients (Claude Desktop) use the **device code flow** or **client credentials flow** against `SourceRAG-Server` directly — they do not go through the WebClient registration.

---

### Blazor Web UI authentication (`SourceRAG.Web` + `SourceRAG.Api`)

**`SourceRAG.Web`** uses `Microsoft.Identity.Web` with the OIDC sign-in flow:

```csharp
// SourceRAG.Web / Program.cs
builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi()
    .AddDownstreamApi("SourceRagApi", builder.Configuration.GetSection("SourceRagApi"))
    .AddInMemoryTokenCaches();

builder.Services.AddAuthorization();
```

```json
// appsettings.json (SourceRAG.Web)
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "<tenant-id>",
    "ClientId": "<webclient-client-id>",
    "ClientSecret": "",
    "CallbackPath": "/signin-oidc"
  },
  "SourceRagApi": {
    "BaseUrl": "https://<internal-host>/api",
    "Scopes": "api://<server-client-id>/sourcerag.query"
  }
}
```

The Blazor app acquires an access token on behalf of the signed-in user and forwards it as a `Bearer` token to `SourceRAG.Api`.

**`SourceRAG.Api`** validates the incoming JWT:

```csharp
// SourceRAG.Api / Program.cs
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CanQuery", policy =>
        policy.RequireClaim("scp", "sourcerag.query"));
    options.AddPolicy("CanIndex", policy =>
        policy.RequireClaim("scp", "sourcerag.index"));
});
```

```json
// appsettings.json (SourceRAG.Api)
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "<tenant-id>",
    "ClientId": "<server-client-id>",
    "Audience": "api://<server-client-id>"
  }
}
```

Endpoint protection:
```csharp
app.MapPost("/chat",   handler).RequireAuthorization("CanQuery");
app.MapPost("/index",  handler).RequireAuthorization("CanIndex");
app.MapGet("/index/status", handler).RequireAuthorization("CanQuery");
```

---

### MCP Server authentication (`SourceRAG.McpHost`)

`SourceRAG.McpHost` exposes MCP over **HTTP/SSE transport** (not stdio — stdio has no auth surface). It validates JWT Bearer tokens using the same Entra ID tenant and server app registration as `SourceRAG.Api`.

```csharp
// SourceRAG.McpHost / Program.cs
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("McpAccess", policy =>
        policy.RequireClaim("scp", "sourcerag.query"));
});

// Protect the MCP SSE endpoint
app.UseAuthentication();
app.UseAuthorization();
app.MapMcp("/mcp").RequireAuthorization("McpAccess");
```

AI agent clients obtain a token via **device code flow** (interactive, one-time setup per developer machine) or **client credentials flow** (for automated agents with their own service principal):

```json
// Claude Desktop mcp config example
{
  "mcpServers": {
    "sourcerag": {
      "url": "https://<internal-host>/mcp",
      "headers": {
        "Authorization": "Bearer <token>"
      }
    }
  }
}
```

Token acquisition for developers (device code, one-time):
```bash
az login --tenant <tenant-id>
az account get-access-token --resource api://<server-client-id>
```

---

### Scope summary

| Scope | Grants access to |
|---|---|
| `sourcerag.query` | `POST /chat`, `GET /index/status`, MCP `search_codebase`, `get_index_status` |
| `sourcerag.index` | `POST /index`, MCP `index_repository` |

By default, all authenticated users receive `sourcerag.query`. The `sourcerag.index` scope is assigned selectively (e.g. only team leads or CI service principals).

---

### Shared token validation

Both `SourceRAG.Api` and `SourceRAG.McpHost` validate tokens against the same Entra ID tenant and `server-client-id`. The same `appsettings.json` `AzureAd` section can be shared or kept in sync via a shared configuration source.

## Consequences

**Positive**
- Single IdP for all human-facing and agent-facing auth surfaces
- MFA, Conditional Access, and sign-in audit logs are handled by Entra ID — zero implementation cost
- Scope-based authorisation separates query access from index-trigger access
- Revoking access for a user or agent is a single Entra ID operation
- `Microsoft.Identity.Web` handles token refresh, caching, and validation — minimal boilerplate

**Negative**
- Requires two Entra ID app registrations to set up
- AI agent clients (Claude Desktop) need a one-time token acquisition step — slightly more friction than API keys
- `SourceRAG.McpHost` must run over HTTP/SSE (not stdio) for auth to apply — stdio transport bypasses auth entirely

## Local development

For local development, authentication can be bypassed by adding a development-only policy:

```csharp
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthorization(options =>
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAssertion(_ => true)
            .Build());
}
```

This is gated on `IsDevelopment()` — it cannot be activated in production.
