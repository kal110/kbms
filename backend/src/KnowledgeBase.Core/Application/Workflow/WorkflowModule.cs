using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeBase.Core.Application.Workflow;

public static class WorkflowModule
{
    /// <summary>Registers the Workflow module's services and contract implementations.</summary>
    public static IServiceCollection AddWorkflowModule(this IServiceCollection services)
    {
        services.AddScoped<WorkflowDirectory>();
        services.AddScoped<SubmissionGate>();
        services.AddScoped<WorkflowEffects>();
        services.AddScoped<SubmissionNumbers>();
        services.AddScoped<SubmissionQueries>();
        services.AddScoped<SubmissionService>();
        services.AddScoped<ReviewService>();
        services.AddScoped<SubmissionDashboardService>();
        services.AddScoped<NotificationService>();
        return services;
    }
}
