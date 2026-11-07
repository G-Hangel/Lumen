using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public sealed class OllamaProvider : IModelCatalog, IChatProvider
{
    private readonly HttpTransport transport;
    private readonly CapabilityResolver resolver;
    private readonly ConcurrentDictionary<(Guid Id, string Provider, ProviderKind Kind, string BaseUrl), IReadOnlyDictionary<string, IReadOnlyDictionary<ReasoningIntent, object>>> thinkingControls =
        new ConcurrentDictionary<(Guid, string, ProviderKind, string), IReadOnlyDictionary<string, IReadOnlyDictionary<ReasoningIntent, object>>>();

    public OllamaProvider(HttpTransport transport, CapabilityResolver resolver) { this.transport = transport; this.resolver = resolver; }

    public async Task<CatalogResult> DiscoverAsync(ProviderConfiguration configuration, string? credential, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await transport.SendAsync(configuration, "/api/tags", credential, HttpMethod.Get, null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return HttpTransport.CatalogError(response);
            using var json = await HttpTransport.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var tags = JsonWire.Get(json.RootElement, "models");
            if (tags.ValueKind != JsonValueKind.Array) throw new ProviderException("invalid_response");
            HttpTransport.CheckCatalogEntries(tags);
            var models = new List<ModelInfo>();
            var controlsByModel = new Dictionary<string, IReadOnlyDictionary<ReasoningIntent, object>>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tag in tags.EnumerateArray())
            {
                var id = JsonWire.String(tag, "model") ?? JsonWire.String(tag, "name");
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id!)) continue;
                using var show = await transport.SendAsync(configuration, "/api/show", credential, HttpMethod.Post,
                    JsonWire.Serialize(new { model = id, verbose = false }), cancellationToken).ConfigureAwait(false);
                if (!show.IsSuccessStatusCode) return HttpTransport.CatalogError(show);
                using var details = await HttpTransport.ReadJsonAsync(show, cancellationToken).ConfigureAwait(false);
                var root = details.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new ProviderException("invalid_response");
                var capabilities = JsonWire.Get(root, "capabilities");
                var supported = new HashSet<string>(JsonWire.Array(capabilities).Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!), StringComparer.Ordinal);
                if (capabilities.ValueKind == JsonValueKind.Array && !supported.Contains("completion")) continue;
                var native = new NativeModel(id!);
                native.Capabilities[ModelCapabilities.Text] = true;
                native.Capabilities[ModelCapabilities.Streaming] = true;
                if (capabilities.ValueKind == JsonValueKind.Array)
                {
                    native.Capabilities[ModelCapabilities.Vision] = supported.Contains("vision");
                    native.Capabilities[ModelCapabilities.Tools] = supported.Contains("tools");
                    native.Capabilities[ModelCapabilities.Reasoning] = supported.Contains("thinking");
                }
                var values = JsonWire.Get(JsonWire.Get(root, "thinking"), "values");
                var controls = new Dictionary<ReasoningIntent, object>();
                foreach (var value in JsonWire.Array(values))
                {
                    if (value.ValueKind == JsonValueKind.False) controls[ReasoningIntent.Off] = false;
                    else if (value.ValueKind == JsonValueKind.True) controls[ReasoningIntent.Minimal] = true;
                    else if (value.ValueKind == JsonValueKind.String && ParseEffort(value.GetString()) is ReasoningIntent level) controls[level] = value.GetString()!;
                }
                var thinkingSupported = values.ValueKind == JsonValueKind.Array
                    ? controls.Keys.Any(level => level != ReasoningIntent.Off) : supported.Contains("thinking");
                if (values.ValueKind == JsonValueKind.Array || capabilities.ValueKind == JsonValueKind.Array) native.Capabilities[ModelCapabilities.Reasoning] = thinkingSupported;
                if (thinkingSupported)
                {
                    // Without model-defined values, keep the badge but do not guess a wire control.
                    native.Reasoning = controls.Count > 0
                        ? new ReasoningSupport(controls.Values.Any(value => value is string) ? ReasoningKind.Effort : ReasoningKind.TemplateFlag,
                            controls.Keys.OrderBy(level => level).ToArray(), !controls.ContainsKey(ReasoningIntent.Off))
                        : new ReasoningSupport(ReasoningKind.ModelSelection, new[] { ReasoningIntent.Low }, true);
                }
                controlsByModel[id!] = controls;
                var info = JsonWire.Get(root, "model_info");
                if (info.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in info.EnumerateObject())
                        if (property.Name.EndsWith(".context_length", StringComparison.Ordinal) && property.Value.TryGetInt32Safe(out var context) && context > 0)
                        { native.ContextWindowTokens = context; break; }
                }
                models.Add(resolver.Resolve(configuration.ProviderId, native));
            }
            // A failed show must not replace any metadata from the last complete catalog.
            thinkingControls[ControlKey(configuration)] = controlsByModel;
            return new CatalogResult(models);
        }
        catch (ProviderException exception) { return new CatalogResult(Array.Empty<ModelInfo>(), null, exception.Code); }
        catch (Exception exception) when (exception is JsonException || exception is ArgumentException || exception is InvalidOperationException)
        { return new CatalogResult(Array.Empty<ModelInfo>(), null, "invalid_response"); }
    }

    public string BuildPayload(ChatRequest request)
    {
        RequestPolicy.Validate(request);
        var messages = new List<object>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in request.Messages)
        {
            var calls = message.Parts.OfType<ToolCallPart>().ToArray();
            foreach (var call in calls) names[call.Id] = call.Name;
            var text = string.Concat(message.Parts.OfType<TextPart>().Select(part => part.Text));
            var images = message.Parts.OfType<ImagePart>().Select(part => part.Base64Data).ToArray();
            var reasoning = message.Parts.OfType<ReasoningPart>().ToArray();
            if (text.Length > 0 || images.Length > 0 || calls.Length > 0 || reasoning.Length > 0 || !message.Parts.OfType<ToolResultPart>().Any())
            {
                var wire = new Dictionary<string, object?> { ["role"] = message.Role.ToString().ToLowerInvariant(), ["content"] = text };
                if (images.Length > 0) wire["images"] = images;
                if (reasoning.Length > 0) wire["thinking"] = string.Concat(reasoning.Select(part => part.Text));
                if (calls.Length > 0) wire["tool_calls"] = calls.Select(call => new { function = new { name = call.Name, arguments = call.Arguments } }).ToArray();
                messages.Add(wire);
            }
            foreach (var result in message.Parts.OfType<ToolResultPart>())
            {
                if (!names.TryGetValue(result.InvocationId, out var name)) throw new ProviderException("tool_history_invalid");
                messages.Add(new { role = "tool", tool_name = name, content = result.Content });
            }
        }
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model.ModelId, ["messages"] = messages, ["stream"] = true,
            ["options"] = new { num_predict = request.MaxOutputTokens }
        };
        if (request.Tools.Count > 0) payload["tools"] = request.Tools.Select(tool => new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.ParametersSchema } }).ToArray();
        var level = RequestPolicy.MapReasoning(request.Model, request.ReasoningIntent);
        if (level is not null)
        {
            if (thinkingControls.TryGetValue(ControlKey(request.Configuration), out var models) && models.TryGetValue(request.Model.ModelId, out var controls))
            {
                if (controls.TryGetValue(level.Value, out var control)) payload["think"] = control;
            }
            else if (request.Model.Reasoning!.Kind == ReasoningKind.TemplateFlag) payload["think"] = level != ReasoningIntent.Off;
            else if (request.Model.Reasoning.Kind == ReasoningKind.Effort) payload["think"] = level == ReasoningIntent.Off ? (object)false : level.ToString()!.ToLowerInvariant();
        }
        return JsonWire.Serialize(payload);
    }

    public async Task StreamAsync(ChatRequest request, string? credential, Func<StreamEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        try
        {
            var body = BuildPayload(request);
            using var response = await transport.SendAsync(request.Configuration, "/api/chat", credential, HttpMethod.Post, body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            { var code = HttpTransport.ErrorCode(response.StatusCode); await onEvent(new StreamError(code, code)).ConfigureAwait(false); return; }
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var parser = new OllamaStreamParser(onEvent);
            await StreamFrames.ReadAsync(stream, true, parser.AcceptAsync, cancellationToken).ConfigureAwait(false);
            await parser.CompleteAsync().ConfigureAwait(false);
        }
        catch (ProviderException exception) { await onEvent(new StreamError(exception.Code, exception.Code)).ConfigureAwait(false); }
        catch (JsonException) { await onEvent(new StreamError("invalid_response", "invalid_response")).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException || exception is HttpRequestException)
        { cancellationToken.ThrowIfCancellationRequested(); await onEvent(new StreamError("network_error", "network_error")).ConfigureAwait(false); }
    }

    private static (Guid Id, string Provider, ProviderKind Kind, string BaseUrl) ControlKey(ProviderConfiguration configuration) =>
        (Guid.Parse(configuration.Id), configuration.ProviderId, configuration.Kind, configuration.BaseUrl);
    private static ReasoningIntent? ParseEffort(string? value) => value switch
    {
        "none" or "off" => ReasoningIntent.Off,
        "minimal" => ReasoningIntent.Minimal,
        "low" => ReasoningIntent.Low,
        "medium" => ReasoningIntent.Medium,
        "high" => ReasoningIntent.High,
        "max" => ReasoningIntent.Max,
        _ => null
    };
}

internal sealed class OllamaStreamParser
{
    private readonly Func<StreamEvent, Task> emit;
    private readonly string localPrefix = "ollama_" + Guid.NewGuid().ToString("N") + "_";
    private int callCount;
    private bool done;
    private bool failed;

    internal OllamaStreamParser(Func<StreamEvent, Task> emit) { this.emit = emit; }

    internal async Task AcceptAsync(string? eventName, string data)
    {
        if (done || failed) return;
        try
        {
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (JsonWire.Get(root, "error").ValueKind != JsonValueKind.Undefined)
            { failed = true; await emit(new StreamError("provider_error", "provider_error")).ConfigureAwait(false); return; }
            var message = JsonWire.Get(root, "message");
            var text = JsonWire.String(message, "content");
            if (text is not null) await emit(new TextDelta(text)).ConfigureAwait(false);
            var thinking = JsonWire.String(message, "thinking");
            if (thinking is not null) await emit(new ReasoningDelta(thinking)).ConfigureAwait(false);
            foreach (var call in JsonWire.Array(JsonWire.Get(message, "tool_calls")))
            {
                var function = JsonWire.Get(call, "function");
                var name = JsonWire.String(function, "name");
                var args = JsonWire.Get(function, "arguments");
                if (string.IsNullOrWhiteSpace(name) || args.ValueKind != JsonValueKind.Object) throw new JsonException();
                var id = JsonWire.String(call, "id");
                callCount++;
                if (string.IsNullOrEmpty(id)) id = localPrefix + callCount.ToString(CultureInfo.InvariantCulture);
                // Ollama emits complete argument objects, unlike OpenAI's fragmented argument strings.
                // https://docs.ollama.com/capabilities/tool-calling
                await emit(new ToolCallStart(id!, name!)).ConfigureAwait(false);
                await emit(new ToolCallArgsDelta(id!, args.GetRawText())).ConfigureAwait(false);
                await emit(new ToolCallEnd(id!, args)).ConfigureAwait(false);
            }
            if (JsonWire.Get(root, "done").ValueKind == JsonValueKind.True)
            {
                var input = JsonWire.Int(root, "prompt_eval_count");
                var output = JsonWire.Int(root, "eval_count");
                if (input is not null || output is not null) await emit(new Usage(input ?? 0, output ?? 0)).ConfigureAwait(false);
                var finish = JsonWire.Finish(JsonWire.String(root, "done_reason"));
                if (callCount > 0 && (finish == FinishReason.Stop || finish == FinishReason.Other)) finish = FinishReason.ToolUse;
                done = true;
                await emit(new Done(finish)).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is JsonException || exception is ArgumentException || exception is InvalidOperationException)
        { failed = true; await emit(new StreamError("invalid_stream", "invalid_stream")).ConfigureAwait(false); }
    }

    internal async Task CompleteAsync()
    {
        if (done || failed) return;
        failed = true;
        await emit(new StreamError("invalid_stream", "invalid_stream")).ConfigureAwait(false);
    }
}
