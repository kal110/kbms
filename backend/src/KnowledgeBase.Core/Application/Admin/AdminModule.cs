using KnowledgeBase.Core.Infrastructure.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeBase.Core.Application.Admin;

public static class AdminModule
{
    /// <summary>Registers the Admin module's services: processing &amp; index status, retries and the audit log.</summary>
    public static IServiceCollection AddAdminModule(this IServiceCollection services)
    {
        services.AddScoped<ProcessingAdminService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<DocumentProcessor>();
        services.AddScoped<VersionPageBackfill>();
        return services;
    }
}
