using KnowledgeBase.Core.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeBase.Core.Application.Access;

public static class AccessModule
{
    /// <summary>Registers the Access module's services, seed contributor and contract implementations.</summary>
    public static IServiceCollection AddAccessModule(this IServiceCollection services)
    {
        services.AddScoped<IAccessEvaluator, AccessEvaluator>();
        services.AddScoped<PrincipalDirectory>();
        services.AddScoped<AccessService>();
        services.AddScoped<AccessExpirySweeper>();
        return services;
    }
}
