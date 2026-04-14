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

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using SourceRAG.Application.DependencyInjection;
using SourceRAG.Application.Common;
using SourceRAG.Infrastructure.DependencyInjection;
using SourceRAG.McpHost.Tools;

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
