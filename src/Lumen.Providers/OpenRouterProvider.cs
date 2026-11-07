using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public sealed class OpenRouterProvider : OpenAIProvider
{
    public OpenRouterProvider(HttpTransport transport, CapabilityResolver resolver) : base(transport, resolver) { }

    public override async Task<CatalogResult> DiscoverAsync(ProviderConfiguration configuration, string? credential, CancellationToken cancellationToken)
    {
        try
        {
            var models = new List<ModelInfo>();
            var totalEntries = 0;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var path = "models";
            while (true)
            {
                var uri = new Uri(new Uri(configuration.BaseUrl), path);
                if (!HttpTransport.SameOrigin(new Uri(configuration.BaseUrl), uri)) throw new ProviderException("unsafe_url");
                if (!visited.Add(uri.AbsoluteUri) || visited.Count > 100) throw new ProviderException("invalid_response");
                // Omitting limit/offset returns the full catalog. Only follow a provider-supplied next link.
                // https://openrouter.ai/docs/api/api-reference/models/list-all-models-and-their-properties
                using var response = await Transport.SendAsync(configuration, uri.AbsoluteUri, null, HttpMethod.Get, null, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return new CatalogResult(Array.Empty<ModelInfo>(), null, HttpTransport.ErrorCode(response.StatusCode), HttpTransport.RetryAfter(response));
                using var json = await HttpTransport.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                var data = JsonWire.Get(json.RootElement, "data");
                if (data.ValueKind != JsonValueKind.Array) throw new ProviderException("invalid_response");
                totalEntries = HttpTransport.CheckCatalogEntries(data, totalEntries);
                foreach (var item in data.EnumerateArray())
                {
                    var id = JsonWire.String(item, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var native = new NativeModel(id!, JsonWire.String(item, "name"));
                    var architecture = JsonWire.Get(item, "architecture");
                    var inputs = JsonWire.Get(architecture, "input_modalities");
                    var outputs = JsonWire.Get(architecture, "output_modalities");
                    if (outputs.ValueKind == JsonValueKind.Array && !Contains(outputs, "text")) continue;
                    native.Capabilities[ModelCapabilities.Text] = true;
                    native.Capabilities[ModelCapabilities.Streaming] = true;
                    if (inputs.ValueKind == JsonValueKind.Array) native.Capabilities[ModelCapabilities.Vision] = Contains(inputs, "image");
                    var parameters = JsonWire.Get(item, "supported_parameters");
                    var reasoning = JsonWire.Get(item, "reasoning");
                    if (parameters.ValueKind == JsonValueKind.Array)
                    {
                        native.Capabilities[ModelCapabilities.Tools] = Contains(parameters, "tools");
                        native.Capabilities[ModelCapabilities.ParallelTools] = Contains(parameters, "parallel_tool_calls");
                        native.Capabilities[ModelCapabilities.StructuredOutput] = Contains(parameters, "structured_outputs") || Contains(parameters, "response_format");
                        native.Capabilities[ModelCapabilities.Reasoning] = reasoning.ValueKind == JsonValueKind.Object ||
                            Contains(parameters, "reasoning") || Contains(parameters, "reasoning_effort") || Contains(parameters, "include_reasoning");
                    }
                    if (reasoning.ValueKind == JsonValueKind.Object || native.Capabilities.TryGetValue(ModelCapabilities.Reasoning, out var supported) && supported)
                    {
                        var mandatory = JsonWire.Get(reasoning, "mandatory").ValueKind == JsonValueKind.True;
                        var efforts = JsonWire.Array(JsonWire.Get(reasoning, "supported_efforts"))
                            .Select(ParseEffort).Where(level => level.HasValue).Select(level => level!.Value)
                            .Where(level => !mandatory || level != ReasoningIntent.Off).Distinct().ToArray();
                        // Missing or unrepresentable enums (xhigh) do not authorize an invented effort. Use the documented enabled flag.
                        native.Reasoning = efforts.Length > 0
                            ? new ReasoningSupport(ReasoningKind.Effort, efforts, mandatory)
                            : new ReasoningSupport(ReasoningKind.ModelSelection, mandatory
                                ? new[] { ReasoningIntent.Low } : new[] { ReasoningIntent.Off, ReasoningIntent.Low }, mandatory);
                    }
                    var context = JsonWire.Int(item, "context_length") ?? JsonWire.Int(JsonWire.Get(item, "top_provider"), "context_length");
                    if (context > 0) native.ContextWindowTokens = context;
                    var maxOutput = JsonWire.Int(JsonWire.Get(item, "top_provider"), "max_completion_tokens");
                    if (maxOutput > 0) native.MaxOutputTokens = maxOutput;
                    models.Add(Resolver.Resolve(configuration.ProviderId, native));
                }
                var next = JsonWire.String(JsonWire.Get(json.RootElement, "links"), "next");
                // A public, unauthenticated catalog response cannot establish whether the supplied key is valid.
                if (string.IsNullOrEmpty(next)) return new CatalogResult(models, null);
                path = new Uri(uri, next).AbsoluteUri;
            }
        }
        catch (ProviderException exception) { return new CatalogResult(Array.Empty<ModelInfo>(), null, exception.Code); }
        catch (Exception exception) when (exception is JsonException || exception is ArgumentException || exception is InvalidOperationException || exception is UriFormatException)
        { return new CatalogResult(Array.Empty<ModelInfo>(), null, "invalid_response"); }
    }

    public override string BuildPayload(ChatRequest request)
    {
        var body = base.BuildPayload(request);
        if (request.Model.Reasoning?.Kind != ReasoningKind.ModelSelection) return body;
        using var document = JsonDocument.Parse(body);
        var payload = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        payload["reasoning"] = new { enabled = RequestPolicy.MapReasoning(request.Model, request.ReasoningIntent) != ReasoningIntent.Off };
        return JsonWire.Serialize(payload);
    }

    private static bool Contains(JsonElement values, string value) => JsonWire.Array(values).Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == value);

    private static ReasoningIntent? ParseEffort(JsonElement value) => value.ValueKind != JsonValueKind.String ? null : value.GetString() switch
    {
        "none" => ReasoningIntent.Off,
        "minimal" => ReasoningIntent.Minimal,
        "low" => ReasoningIntent.Low,
        "medium" => ReasoningIntent.Medium,
        "high" => ReasoningIntent.High,
        "max" => ReasoningIntent.Max,
        _ => null
    };
}
