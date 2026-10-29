using System.Diagnostics;
using System.Text.Json.Serialization;
using KnowledgeBase.Api.Endpoints;
using KnowledgeBase.Api.Infrastructure;
using KnowledgeBase.Core;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Application.Workflow;
using KnowledgeBase.Core.Infrastructure.Persistence.Seed;
using KnowledgeBase.Core.Infrastructure.Storage;
using Microsoft.AspNetCore.OpenApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Core: persistence, storage, security and every module. A future MCP host calls the same method.
builder.Services.AddKnowledgeBaseCore(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddKnowledgeBaseWorkers();

// API host: who is calling, how errors look, JSON, OpenAPI, CORS, health.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IIdentitySource, HttpIdentitySource>();
builder.Services.AddKbAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier));
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // Numbers are numbers: the web default also reads "12", which makes OpenAPI type every integer as integer|string.
    o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddOpenApi(o =>
{
    o.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
    // Library and Workflow both define PersonRef and VersionRef with different shapes; one schema
    // name each would make clients (the SPA's generated types) read the wrong fields.
    o.CreateSchemaReferenceId = type =>
    {
        var id = OpenApiOptions.CreateDefaultSchemaReferenceId(type);
        return id is not null && type.Type.Namespace == typeof(SubmissionDetail).Namespace
            && id is "PersonRef" or "VersionRef"
            ? $"Workflow{id}"
            : id;
    };
});
builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("ETag", "Location", Idempotency.ReplayedHeader)));
builder.Services.AddKbRateLimits();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseIdempotencyBuffering();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(o => o.WithTitle("Knowledge Base API"));
}
app.MapHealthChecks("/health");

var api = app.MapGroup("/api").RequireAuthorization();
api.MapAuthEndpoints(app.Environment);
api.MapDevEndpoints(app.Environment);
api.MapOrgEndpoints();
api.MapSettingsEndpoints();
api.MapRequirementsEndpoints();
api.MapLibraryEndpoints();
api.MapAccessEndpoints();
api.MapStorageEndpoints();
api.MapWorkflowEndpoints();
api.MapAdminEndpoints();
api.MapKnowledgeEndpoints();

// Storage first: uploads need the bucket, and the seed writes files (Storage:S3:CreateBucket).
await app.Services.GetRequiredService<IObjectStorage>().EnsureReadyAsync();

// Development: create the schema and the demo data (see Database:* in appsettings.Development.json).
var migrate = app.Configuration.GetValue<bool>("Database:MigrateOnStartup");
var seed = app.Configuration.GetValue<bool>("Database:Seed");
if (migrate || seed)
{
    await using var scope = app.Services.CreateAsyncScope();
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    if (migrate) await initializer.MigrateAsync();
    if (seed) await initializer.SeedAsync();
}

app.Run();
