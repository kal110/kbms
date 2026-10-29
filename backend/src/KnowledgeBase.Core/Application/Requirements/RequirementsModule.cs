using KnowledgeBase.Core.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeBase.Core.Application.Requirements;

public static class RequirementsModule
{
    /// <summary>Registers the Requirements module's services and contract implementations.</summary>
    public static IServiceCollection AddRequirementsModule(this IServiceCollection services)
    {
        services.AddScoped<ScopeDirectory>();
        services.AddScoped<IRequirementResolver, RequirementResolver>();
        services.AddScoped<RequirementSetService>();
        return services;
    }
}
