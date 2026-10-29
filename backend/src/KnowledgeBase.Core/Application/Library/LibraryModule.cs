using KnowledgeBase.Core.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeBase.Core.Application.Library;

public static class LibraryModule
{
    /// <summary>Registers the Library module's services and contract implementations.</summary>
    public static IServiceCollection AddLibraryModule(this IServiceCollection services)
    {
        services.AddScoped<IAcademicFolderService, AcademicFolderService>();
        services.AddScoped<IDocumentFactory, DocumentFactory>();
        services.AddScoped<DocumentNumbering>();
        services.AddScoped<FolderService>();
        services.AddScoped<DocumentService>();
        services.AddScoped<UploadService>();
        services.AddScoped<SearchService>();
        return services;
    }
}
