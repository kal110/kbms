using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeBase.Core.Application.Settings;

public static class SettingsModule
{
    /// <summary>Registers the Settings module's services, seed contributor and contract implementations.</summary>
    public static IServiceCollection AddSettingsModule(this IServiceCollection services)
    {
        services.AddScoped<DocumentTypeService>();
        services.AddScoped<AcademicCalendarService>();
        services.AddScoped<SubmissionTypeService>();
        return services;
    }
}
