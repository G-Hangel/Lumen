using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public sealed class CapabilityHttpProbe
{
    private const string PixelDataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    private readonly HttpTransport transport;

    public CapabilityHttpProbe(HttpTransport transport)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public static bool Supports(ProviderKind kind) => kind is ProviderKind.OpenAI or ProviderKind.OpenRouter or
        ProviderKind.LMStudio or ProviderKind.Azure;

    // Consent, selected-model identity, deduplication and the visible session budget belong
    // to CapabilityProbe and its UI caller; this service performs exactly one paid POST.
    public async Task<bool?> ProbeAsync(ProviderConfiguration configuration, ModelInfo model, string? credential,
        ModelCapabilities capability, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (!Supports(configuration.Kind) || capability is not (ModelCapabilities.Vision or ModelCapabilities.Tools)) return null;
        if (model.ProviderId != configuration.ProviderId ||
            configuration.SelectedModelId is not null && configuration.SelectedModelId != model.ModelId) return null;

        var payload = new Dictionary<string, object>
        {
            ["model"] = model.ModelId,
            ["stream"] = false
        };
        var nativeTokens = configuration.Kind == ProviderKind.Azure ||
            configuration.Kind == ProviderKind.OpenAI && configuration.ProviderId == "openai";
        payload[nativeTokens ? "max_completion_tokens" : "max_tokens"] = nativeTokens ? 16 : 1;
        if (capability == ModelCapabilities.Vision)
        {
            payload["messages"] = new[] { new { role = "user", content = new object[]
            {
                new { type = "text", text = "." },
                new { type = "image_url", image_url = new { url = PixelDataUri, detail = "low" } }
            } } };
        }
        else
        {
            payload["messages"] = new[] { new { role = "user", content = "." } };
            payload["tools"] = new[] { new { type = "function", function = new
            {
                name = "lumen_probe", description = "No operation.",
                parameters = new { type = "object", properties = new { } }
            } } };
            payload["tool_choice"] = "none";
        }

        var path = configuration.Kind switch
        {
            ProviderKind.Azure => "/openai/v1/chat/completions",
            ProviderKind.LMStudio => "/v1/chat/completions",
            _ => "chat/completions"
        };
        try
        {
            using var response = await transport.SendAsync(configuration, path, credential, HttpMethod.Post,
                JsonWire.Serialize(payload), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var status = (int)response.StatusCode;
            if (status != 200 && status != 400 && status != 422) return null;
            using var json = await HttpTransport.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // This tiny payload cannot prove image understanding or tool calling: a provider
            // may silently ignore either input. Do not spend more to force semantic evidence.
            if (status == 200) return null;
            return RejectsCapability(JsonWire.Get(root, "error"), capability) ? false : (bool?)null;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception exception) when (exception is ProviderException || exception is OperationCanceledException ||
            exception is InvalidOperationException || exception is RegexMatchTimeoutException)
        { return null; }
    }

    private static bool RejectsCapability(JsonElement error, ModelCapabilities capability)
    {
        if (error.ValueKind != JsonValueKind.Object) return false;
        foreach (var field in new[] { "type", "code", "param", "message" })
            if (JsonWire.Get(error, field).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String)) return false;
        var type = JsonWire.String(error, "type");
        var code = JsonWire.String(error, "code");
        if (type is not (null or "invalid_request_error")) return false;
        if (code is not (null or "invalid_request_error" or "unsupported_parameter" or "unsupported_value")) return false;
        if (type != "invalid_request_error" && code != "unsupported_parameter" && code != "unsupported_value") return false;
        var message = JsonWire.String(error, "message");
        if (message is null || message.Length > 4096) return false;
        var parameter = JsonWire.String(error, "param");
        var exactParameter = capability == ModelCapabilities.Tools
            ? parameter == "tools"
            : parameter == "image_url" || parameter?.EndsWith(".image_url", StringComparison.Ordinal) == true;
        if (parameter is not null && !exactParameter && !(capability == ModelCapabilities.Vision &&
            (parameter == "messages" || parameter.StartsWith("messages[", StringComparison.Ordinal) ||
             parameter.StartsWith("messages.", StringComparison.Ordinal)))) return false;

        // A negative is permanent. Require the entire message to unconditionally reject
        // this model's capability, not a parameter, value, account or request configuration.
        // In particular, rejecting tool_choice says nothing about support for tools.
        var names = capability == ModelCapabilities.Vision
            ? "(?:image_url|image inputs?|images?|vision)"
            : "(?:tool calling|function calling|tools)";
        var name = @"['""`]?\b" + names + @"\b['""`]?";
        var model = @"(?:this|the(?: selected| requested)?)\s+model";
        var pattern = @"\A\s*(?:" + model + @"\s+(?:does not|doesn't|cannot)\s+support\s+" + name +
            @"|(?:unsupported\s+parameter\s*:\s*)?" + name + @"\s+(?:is|are)\s+(?:not supported|unsupported)\s+(?:by|with|for)\s+" + model + @")\s*[.!]?\s*\z";
        return Regex.IsMatch(message, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
