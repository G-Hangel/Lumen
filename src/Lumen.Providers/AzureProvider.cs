using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public sealed class AzureProvider : OpenAIProvider
{
    public AzureProvider(HttpTransport transport, CapabilityResolver resolver) : base(transport, resolver) { }

    public override async Task<CatalogResult> DiscoverAsync(ProviderConfiguration configuration, string? credential, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Transport.SendAsync(configuration, "/openai/v1/models", credential, HttpMethod.Get, null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return HttpTransport.CatalogError(response);
            using var json = await HttpTransport.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            HttpTransport.CheckCatalogEntries(JsonWire.Get(json.RootElement, "data"));
            if (configuration.DeploymentNames.Count > HttpTransport.MaxCatalogEntries) throw new ProviderException("response_too_large");
            // The data-plane catalog lists offerings, not deployed names. ARM inventory requires Entra/RBAC.
            // https://learn.microsoft.com/en-us/rest/api/microsoft-foundry/azureopenai/models
            // https://learn.microsoft.com/en-us/azure/foundry/openai/api-version-lifecycle
            var names = configuration.DeploymentNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length == 0) return new CatalogResult(Array.Empty<ModelInfo>(), true, "deployment_required");
            var models = names.Select(name => Resolver.Resolve(configuration.ProviderId, new NativeModel(name))).ToArray();
            return new CatalogResult(models);
        }
        catch (ProviderException exception) { return new CatalogResult(Array.Empty<ModelInfo>(), null, exception.Code); }
        catch (Exception exception) when (exception is JsonException || exception is ArgumentException || exception is InvalidOperationException)
        { return new CatalogResult(Array.Empty<ModelInfo>(), null, "invalid_response"); }
    }

    public override string BuildPayload(ChatRequest request)
    {
        if (!request.Configuration.DeploymentNames.Any(name => !string.IsNullOrWhiteSpace(name))) throw new ProviderException("deployment_required");
        if (!request.Configuration.DeploymentNames.Contains(request.Model.ModelId, StringComparer.Ordinal)) throw new ProviderException("deployment_not_configured");
        return base.BuildPayload(request);
    }

    protected override string ChatPath(ChatRequest request) => "/openai/v1/chat/completions";
}
