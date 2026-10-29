using KnowledgeBase.Core.Infrastructure.Persistence.Seed;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeBase.Core.Application.Org;

public static class OrgModule
{
    /// <summary>Registers the Org module's services, seed contributor and contract implementations.</summary>
    public static IServiceCollection AddOrgModule(this IServiceCollection services)
    {
        services.AddScoped<OrgService>();
        services.AddScoped<OrgAdminService>();
        services.AddScoped<OrgStructureService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<TeachingLoadService>();
        services.AddScoped<ISeedContributor, ReferenceSeed>();
        return services;
    }
}
