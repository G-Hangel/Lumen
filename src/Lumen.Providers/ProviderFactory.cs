using System;
using System.Collections.Concurrent;

namespace Lumen.Providers;

public sealed class ProviderFactory
{
    private readonly HttpTransport transport;
    private readonly CapabilityResolver resolver;
    private readonly ConcurrentDictionary<ProviderKind, Lazy<IModelCatalog>> adapters = new ConcurrentDictionary<ProviderKind, Lazy<IModelCatalog>>();
    public ProviderFactory(HttpTransport transport, CapabilityResolver resolver) { this.transport = transport; this.resolver = resolver; }
    public IModelCatalog Catalog(ProviderKind kind) => adapters.GetOrAdd(kind, value => new Lazy<IModelCatalog>(() => CreateCatalog(value))).Value;
    private IModelCatalog CreateCatalog(ProviderKind kind) => kind switch
    {
        ProviderKind.OpenRouter => new OpenRouterProvider(transport, resolver),
        ProviderKind.Anthropic => new AnthropicProvider(transport, resolver),
        ProviderKind.Gemini => new GeminiProvider(transport, resolver),
        ProviderKind.Ollama => new OllamaProvider(transport, resolver),
        ProviderKind.Azure => new AzureProvider(transport, resolver),
        ProviderKind.LMStudio => new LMStudioProvider(transport, resolver),
        _ => new OpenAIProvider(transport, resolver)
    };
    public IChatProvider Chat(ProviderKind kind) => (IChatProvider)Catalog(kind);
}
