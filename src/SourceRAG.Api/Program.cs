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

using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using SourceRAG.Application.DependencyInjection;
using SourceRAG.Application.Common;
using SourceRAG.Application.Indexing;
using SourceRAG.Application.Query;
using SourceRAG.Application.Status;
using SourceRAG.Domain.Interfaces;
using SourceRAG.Infrastructure.DependencyInjection;

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
    var vectorStore       = scope.ServiceProvider.GetRequiredService<IVectorStore>();
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
