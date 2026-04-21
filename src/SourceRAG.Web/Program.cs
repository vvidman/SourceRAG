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

using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using SourceRAG.Web.Components;
using SourceRAG.Web.Services;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    // Dev mode: bypass Entra ID, use plain HttpClient (API has FallbackPolicy = AllowAll in dev)
    builder.Services.AddAuthentication();
    builder.Services.AddAuthorization();
    builder.Services.AddControllers();
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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapControllers();

app.Run();
