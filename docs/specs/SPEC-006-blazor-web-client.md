# SPEC-006 — Blazor Web Client

## Overview
Implement the Blazor Server chat client. Communicates exclusively with `SourceRAG.Api` via a typed `HttpClient`. No direct dependency on Application or Infrastructure.

## References
- ADR-009 (Blazor Web, Interactive Server, typed HttpClient)
- ADR-011 (authentication — Entra ID OIDC sign-in)

## Project
`src/SourceRAG.Web`

## NuGet packages to add
```
Microsoft.Identity.Web
Microsoft.Identity.Web.UI
Microsoft.Identity.Web.DownstreamApi
```

## Rules
- `SourceRAG.Web` has no `ProjectReference` to Application or Infrastructure.
- All API calls go through `SourceRagApiClient` (typed HttpClient).
- No business logic in components — components call `SourceRagApiClient` and render results.
- Remove placeholder pages: `Counter.razor`, `Weather.razor`.

---

## API Client Models (DTOs)

Duplicate only the response shapes the Web project needs — do not reference Domain assemblies.

### `Models/ChatRequest.cs`
```csharp
public sealed record ChatRequest(string Query, int TopK = 5);
```

### `Models/ChatResponse.cs`
```csharp
public sealed record ChatResponse(string Answer, IReadOnlyList<ChunkProof> Chunks);
```

### `Models/ChunkProof.cs`
```csharp
public sealed record ChunkProof
{
    public required string FilePath      { get; init; }
    public required string Revision      { get; init; }
    public required string Author        { get; init; }
    public required string CommitMessage { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Branch        { get; init; }
    public string? SymbolName            { get; init; }
    public string? SymbolType            { get; init; }
    public int StartLine                 { get; init; }
    public int EndLine                   { get; init; }
    public float Score                   { get; init; }
}
```

### `Models/IndexRequest.cs`
```csharp
public sealed record IndexRequest(string Mode = "incremental");
```

### `Models/IndexJobResponse.cs`
```csharp
public sealed record IndexJobResponse(
    int ProcessedFiles,
    int UpsertedChunks,
    int DeletedChunks,
    string ToRevision,
    TimeSpan Duration);
```

### `Models/IndexStatusResponse.cs`
```csharp
public sealed record IndexStatusResponse(
    string? LastIndexedRevision,
    int ChunkCount,
    DateTimeOffset? LastIndexedAt,
    bool IsIndexing);
```

---

## API Client

### `Services/SourceRagApiClient.cs`
```csharp
public sealed class SourceRagApiClient(HttpClient http)
{
    public Task<ChatResponse?> ChatAsync(string query, int topK = 5, CancellationToken ct = default)
        => http.PostAsJsonAsync("/chat", new ChatRequest(query, topK), ct)
               .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<ChatResponse>(ct), ct)
               .Unwrap();

    public Task<IndexJobResponse?> IndexAsync(string mode = "incremental", CancellationToken ct = default)
        => http.PostAsJsonAsync("/index", new IndexRequest(mode), ct)
               .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<IndexJobResponse>(ct), ct)
               .Unwrap();

    public Task<IndexStatusResponse?> GetStatusAsync(CancellationToken ct = default)
        => http.GetFromJsonAsync<IndexStatusResponse>("/index/status", ct);
}
```

Use `HttpClientJsonExtensions` (`System.Net.Http.Json`). Handle non-success status codes with `EnsureSuccessStatusCode` or explicit error mapping.

---

## Components

### `Components/Pages/Chat.razor`
Route: `@page "/"`

State:
- `List<ChatMessage> _messages` — alternating user/assistant messages
- `string _inputText`
- `bool _isLoading`

UI layout:
- Scrollable message history area (CSS: `overflow-y: auto; flex: 1`)
- Each message rendered via `<MessageBubble>`
- Assistant messages followed by a `<div class="proof-list">` containing one `<ChunkProofCard>` per chunk
- Fixed-bottom input bar: textarea + Send button
- Send button disabled when `_isLoading`

On send:
1. Append user message to `_messages`
2. Set `_isLoading = true`
3. Call `SourceRagApiClient.ChatAsync(_inputText)`
4. Append assistant message + chunks to `_messages`
5. Set `_isLoading = false`
6. Clear `_inputText`

```csharp
private sealed record ChatMessage(string Role, string Text, IReadOnlyList<ChunkProof>? Chunks = null);
```

### `Components/MessageBubble.razor`
Parameters:
- `[Parameter] string Role` — `"user"` or `"assistant"`
- `[Parameter] string Text`

Renders differently based on `Role` (CSS class `message-user` vs `message-assistant`).

### `Components/ChunkProofCard.razor`
**This is the primary UI differentiator of SourceRAG.**

Parameter: `[Parameter] ChunkProof Chunk`

Renders:
- Symbol name + type badge (if `SymbolName` is not null)
- File path (monospace)
- Line range `L{StartLine}–{EndLine}`
- Author name
- Timestamp formatted as `yyyy-MM-dd HH:mm`
- First line of commit message (truncate to 80 chars, full in `title` attribute)
- Revision short hash (first 8 chars) in a code element
- Similarity score as percentage (e.g. `94%`)

### `Components/IndexStatusPanel.razor`
Displayed in the sidebar or header area.

Shows:
- Last indexed revision (short hash or "Never")
- Chunk count
- Last indexed at timestamp
- `[Reindex]` button → calls `IndexAsync("incremental")`
- `[Full Reindex]` button → calls `IndexAsync("full")` (with confirmation)

Auto-refreshes status every 30 seconds using `System.Threading.Timer`.

---

## Layout and Navigation

### `Components/Layout/NavMenu.razor`
Replace default nav with:
- SourceRAG logo/title
- Link: Chat (`/`)
- Link: Index Status (`/status`)
- User info (display name from claims) + Sign out link

### `Components/Pages/Status.razor`
Route: `@page "/status"`

Full-page version of `IndexStatusPanel` with more detail and reindex controls.

---

## Authentication (`Program.cs`)

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi()
    .AddDownstreamApi("SourceRagApi", builder.Configuration.GetSection("SourceRagApi"))
    .AddInMemoryTokenCaches();

builder.Services.AddAuthorization();
builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient<SourceRagApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["SourceRagApi:BaseUrl"]
        ?? throw new InvalidOperationException("SourceRagApi:BaseUrl not configured"));
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapControllers(); // for Microsoft Identity UI sign-in/out

app.Run();
```

### `appsettings.json`
```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "",
    "ClientId": "",
    "ClientSecret": "",
    "CallbackPath": "/signin-oidc"
  },
  "SourceRagApi": {
    "BaseUrl": "https://localhost:7001",
    "Scopes": "api://<server-client-id>/sourcerag.query"
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

---

## CSS

Add to `wwwroot/app.css`:

```css
/* Chat layout */
.chat-container { display: flex; flex-direction: column; height: 100vh; }
.message-history { flex: 1; overflow-y: auto; padding: 1rem; }
.chat-input-bar   { padding: 1rem; border-top: 1px solid #dee2e6; }

/* Message bubbles */
.message-user      { background: #0d6efd; color: white; border-radius: 1rem; padding: .75rem 1rem; max-width: 75%; margin-left: auto; }
.message-assistant { background: #f8f9fa; border-radius: 1rem; padding: .75rem 1rem; max-width: 85%; }

/* Proof card */
.proof-card        { border: 1px solid #dee2e6; border-radius: .5rem; padding: .75rem; margin-top: .5rem; font-size: .875rem; }
.proof-card .file  { font-family: monospace; color: #6c757d; }
.proof-card .revision { background: #f8f9fa; padding: .1rem .3rem; border-radius: .25rem; font-family: monospace; }
.proof-card .score { color: #198754; font-weight: 600; }
.proof-list        { margin-top: .5rem; display: flex; flex-direction: column; gap: .5rem; }
```
