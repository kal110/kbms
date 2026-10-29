using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Access;
using KnowledgeBase.Core.Application.Admin;
using KnowledgeBase.Core.Application.Ai;
using KnowledgeBase.Core.Application.Library;
using KnowledgeBase.Core.Application.Org;
using KnowledgeBase.Core.Application.Requirements;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Application.Settings;
using KnowledgeBase.Core.Application.Workflow;
using KnowledgeBase.Core.Infrastructure.Ai;
using KnowledgeBase.Core.Infrastructure.Persistence;
using KnowledgeBase.Core.Infrastructure.Persistence.Seed;
using KnowledgeBase.Core.Infrastructure.Processing;
using KnowledgeBase.Core.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Everything a host needs to run the knowledge base: persistence, storage, security and every
    /// module's services. The API calls this today; an MCP server can call it the same way and
    /// supply its own <see cref="IIdentitySource"/>.
    /// </summary>
    public static IServiceCollection AddKnowledgeBaseCore(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        var connectionString = config.GetConnectionString("Kbms")
            ?? throw new InvalidOperationException("ConnectionStrings:Kbms is not configured.");

        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<KbDbContext>(o => o
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention());

        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        services.Configure<AiOptions>(config.GetSection(AiOptions.Section));
        services.Configure<ProcessingOptions>(config.GetSection(ProcessingOptions.Section));
        services.Configure<AdminSeedOptions>(config.GetSection(AdminSeedOptions.Section));
        services.AddSingleton<StorageUrlSigner>();
        if (string.Equals(config["Storage:Provider"], "S3", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        else
            services.AddSingleton<IObjectStorage>(sp => new LocalObjectStorage(
                sp.GetRequiredService<IOptions<StorageOptions>>(),
                sp.GetRequiredService<StorageUrlSigner>(),
                sp.GetRequiredService<TimeProvider>(),
                contentRoot));

        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IOutbox, Outbox>();
        services.AddScoped<IProcessingPipeline, ProcessingPipeline>();
        services.AddScoped<DatabaseInitializer>();

        // One registration call per module; each module owns its own file.
        services.AddOrgModule();
        services.AddSettingsModule();
        services.AddRequirementsModule();
        services.AddLibraryModule();
        services.AddAccessModule();
        services.AddWorkflowModule();
        services.AddAdminModule();
        services.AddAiModule();
        return services;
    }

    /// <summary>
    /// The background workers (processing pipeline, outbox dispatcher, housekeeping, search index). The API runs
    /// them in-process for now; a dedicated Worker host can call this instead, and an MCP host
    /// simply doesn't.
    /// </summary>
    public static IServiceCollection AddKnowledgeBaseWorkers(this IServiceCollection services)
    {
        services.AddHostedService<ProcessingWorker>();
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<HousekeepingWorker>();
        services.AddHostedService<SearchIndexWorker>();
        return services;
    }
}
