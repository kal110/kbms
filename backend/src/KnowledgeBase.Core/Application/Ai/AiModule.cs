using KnowledgeBase.Core.Infrastructure.Ai;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeBase.Core.Application.Ai;

public static class AiModule
{
    /// <summary>
    /// Registers the AI module: the embedding and vector-store clients, Claude, indexing and the
    /// assistant. Registered whatever Ai:Enabled says; the services check it themselves.
    /// </summary>
    public static IServiceCollection AddAiModule(this IServiceCollection services)
    {
        services.AddHttpClient<IEmbeddingProvider, OpenAiCompatibleEmbeddings>();
        services.AddHttpClient<IVectorIndex, QdrantVectorIndex>();
        services.AddSingleton<IChatModel, ClaudeChatModel>();

        services.AddScoped<IndexPayloadBuilder>();
        services.AddScoped<SearchIndexState>();
        services.AddScoped<SearchIndexer>();
        services.AddScoped<IVersionIndexer>(sp => sp.GetRequiredService<SearchIndexer>());
        services.AddScoped<SearchIndexMaintainer>();

        services.AddScoped<KnowledgeRetriever>();
        services.AddScoped<AssistantService>();
        return services;
    }
}
