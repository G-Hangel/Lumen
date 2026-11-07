using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public sealed class LMStudioProvider : OpenAIProvider
{
    public LMStudioProvider(HttpTransport transport, CapabilityResolver resolver) : base(transport, resolver) { }

    public override async Task<CatalogResult> DiscoverAsync(ProviderConfiguration configuration, string? credential, CancellationToken cancellationToken)
    {
        try
        {
            using var nativeResponse = await Transport.SendAsync(configuration, "/api/v0/models", credential, HttpMethod.Get, null, cancellationToken).ConfigureAwait(false);
            // Only a missing native endpoint allows falling back; auth and malformed responses must remain visible.
            if (nativeResponse.StatusCode == HttpStatusCode.NotFound)
            {
                using var fallback = await Transport.SendAsync(configuration, "/v1/models", credential, HttpMethod.Get, null, cancellationToken).ConfigureAwait(false);
                return await ReadCatalogAsync(configuration, fallback, cancellationToken).ConfigureAwait(false);
            }
            return await ReadCatalogAsync(configuration, nativeResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException exception) { return new CatalogResult(Array.Empty<ModelInfo>(), null, exception.Code); }
        catch (Exception exception) when (exception is JsonException || exception is ArgumentException || exception is InvalidOperationException)
        { return new CatalogResult(Array.Empty<ModelInfo>(), null, "invalid_response"); }
    }

    protected override string ChatPath(ChatRequest request) => "/v1/chat/completions";

    private async Task<CatalogResult> ReadCatalogAsync(ProviderConfiguration configuration, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) return HttpTransport.CatalogError(response);
        using var json = await HttpTransport.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var data = JsonWire.Get(json.RootElement, "data");
        if (data.ValueKind != JsonValueKind.Array) throw new ProviderException("invalid_response");
        HttpTransport.CheckCatalogEntries(data);
        var models = new List<ModelInfo>();
        foreach (var item in data.EnumerateArray())
        {
            var id = JsonWire.String(item, "id");
            var type = JsonWire.String(item, "type");
            if (string.IsNullOrWhiteSpace(id) || type == "embeddings") continue;
            var native = new NativeModel(id!);
            native.Capabilities[ModelCapabilities.Text] = true;
            native.Capabilities[ModelCapabilities.Streaming] = true;
            if (type == "vlm" || type == "llm") native.Capabilities[ModelCapabilities.Vision] = type == "vlm";
            var context = JsonWire.Int(item, "max_context_length");
            if (context > 0) native.ContextWindowTokens = context;
            models.Add(Resolver.Resolve(configuration.ProviderId, native));
        }
        return new CatalogResult(models);
    }
}
