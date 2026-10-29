using KnowledgeBase.Core.Infrastructure.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KnowledgeBase.Core.Application.Ai;

public static class AiModule
{
    /// <summary>
    /// Registers the AI module: the embedding and vector-store clients, the chat model chosen by
    /// Ai:Chat:Provider (a local model or Claude), indexing and the assistant. Registered whatever
    /// Ai:Enabled says; the services check it themselves.
    /// </summary>
    public static IServiceCollection AddAiModule(this IServiceCollection services)
    {
        services.AddHttpClient<IEmbeddingProvider, OpenAiCompatibleEmbeddings>();
        services.AddHttpClient<IVectorIndex, QdrantVectorIndex>();

        // The local model's own timeout covers the wait for the first token; the stream after it can
        // take longer than HttpClient's 100-second default on a small GPU.
        services.AddHttpClient<OpenAiCompatibleChatModel>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<ClaudeChatModel>();
        services.AddTransient<IChatModel>(sp => sp.GetRequiredService<IOptions<AiOptions>>().Value.Chat.Provider == ChatProvider.Anthropic
            ? sp.GetRequiredService<ClaudeChatModel>()
            : sp.GetRequiredService<OpenAiCompatibleChatModel>());

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
