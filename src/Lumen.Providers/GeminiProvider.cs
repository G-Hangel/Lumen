using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public sealed class GeminiProvider : IModelCatalog, IChatProvider
{
    private readonly HttpTransport transport;
    private readonly CapabilityResolver resolver;

    public GeminiProvider(HttpTransport transport, CapabilityResolver resolver)
    {
        this.transport = transport;
        this.resolver = resolver;
    }

    public async Task<CatalogResult> DiscoverAsync(ProviderConfiguration configuration, string? credential, CancellationToken cancellationToken)
    {
        try
        {
            var models = new List<ModelInfo>();
            var totalEntries = 0;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? token = null;
            do
            {
                var path = ApiPath(configuration, "models?pageSize=1000") + (token is null ? "" : "&pageToken=" + Uri.EscapeDataString(token));
                using var response = await transport.SendAsync(configuration, path, credential, HttpMethod.Get, null, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return HttpTransport.CatalogError(response);
                using var json = await HttpTransport.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                var root = json.RootElement;
                var data = JsonWire.Get(root, "models");
                if (data.ValueKind != JsonValueKind.Array) throw new JsonException();
                totalEntries = HttpTransport.CheckCatalogEntries(data, totalEntries);
                foreach (var item in data.EnumerateArray())
                {
                    if (!JsonWire.Array(JsonWire.Get(item, "supportedGenerationMethods")).Any(method => method.ValueKind == JsonValueKind.String && method.GetString() == "generateContent")) continue;
                    var name = JsonWire.String(item, "name");
                    if (string.IsNullOrWhiteSpace(name) || !name!.StartsWith("models/", StringComparison.Ordinal)) throw new JsonException();
                    var id = ModelId(name);
                    var native = new NativeModel(id, JsonWire.String(item, "displayName"))
                    {
                        ContextWindowTokens = PositiveLimit(item, "inputTokenLimit"),
                        MaxOutputTokens = PositiveLimit(item, "outputTokenLimit")
                    };
                    native.Capabilities[ModelCapabilities.Text] = true;
                    native.Capabilities[ModelCapabilities.Streaming] = true;
                    // generateContent and thinking do not prove vision or function calling.
                    var thinking = JsonWire.Get(item, "thinking");
                    if (thinking.ValueKind == JsonValueKind.True)
                    {
                        var registered = resolver.Resolve(configuration.ProviderId, native);
                        native.Capabilities[ModelCapabilities.Reasoning] = true;
                        native.Reasoning = registered.Reasoning ?? DefaultReasoning(id);
                    }
                    else if (thinking.ValueKind == JsonValueKind.False) native.Capabilities[ModelCapabilities.Reasoning] = false;
                    else if (thinking.ValueKind != JsonValueKind.Undefined && thinking.ValueKind != JsonValueKind.Null) throw new JsonException();
                    models.Add(resolver.Resolve(configuration.ProviderId, native));
                }
                token = JsonWire.String(root, "nextPageToken");
                if (string.IsNullOrEmpty(token)) break;
                if (!cursors.Add(token!)) throw new JsonException();
            } while (true);
            return new CatalogResult(models);
        }
        catch (ProviderException exception) { return new CatalogResult(Array.Empty<ModelInfo>(), null, exception.Code); }
        catch (Exception exception) when (exception is JsonException || exception is ArgumentException || exception is InvalidOperationException)
        { return new CatalogResult(Array.Empty<ModelInfo>(), null, "invalid_response"); }
    }

    public string BuildPayload(ChatRequest request)
    {
        RequestPolicy.Validate(request);
        var contents = new List<object>();
        var system = new List<object>();
        var calls = new Dictionary<string, ToolCallPart>(StringComparer.Ordinal);
        foreach (var message in request.Messages)
        {
            var parts = new List<object>();
            for (var index = 0; index < message.Parts.Count; index++)
            {
                var part = message.Parts[index];
                switch (part)
                {
                    case TextPart text:
                        // A signed answer emits a visible TextDelta followed by an opaque
                        // state marker. Replay its original Part once, not unsigned + signed.
                        if (index + 1 < message.Parts.Count && message.Parts[index + 1] is ReasoningPart next && next.ProviderState is not null && IsSignedText(next.ProviderState, text.Text)) break;
                        parts.Add(new { text = text.Text });
                        break;
                    case ImagePart image:
                        parts.Add(new { inlineData = new { mimeType = image.MediaType, data = image.Base64Data } });
                        break;
                    case ReasoningPart reasoning when reasoning.ProviderState is not null:
                        parts.Add(ReplayPart(reasoning.ProviderState));
                        break;
                    case ToolCallPart call:
                        calls[call.Id] = call;
                        parts.Add(call.ProviderState is null ? (object)new { functionCall = new { id = call.Id, name = call.Name, args = call.Arguments } } : ReplayPart(call.ProviderState));
                        break;
                    case ToolResultPart result:
                        if (!calls.TryGetValue(result.InvocationId, out var invocation)) throw new ProviderException("unknown_tool_call");
                        var functionResponse = new Dictionary<string, object?>
                        {
                            ["name"] = invocation.Name,
                            ["response"] = result.IsError ? (object)new { error = result.Content } : new { output = result.Content }
                        };
                        // Locally generated IDs identify invocations inside Lumen only. Do
                        // not inject them into a provider Part that originally had no ID.
                        var nativeId = invocation.ProviderState is null ? invocation.Id : JsonWire.String(JsonWire.Get(ReplayPart(invocation.ProviderState), "functionCall"), "id");
                        if (!string.IsNullOrEmpty(nativeId)) functionResponse["id"] = nativeId;
                        parts.Add(new { functionResponse });
                        break;
                }
            }
            if (message.Role == MessageRole.System) system.AddRange(parts);
            else if (parts.Count > 0) contents.Add(new { role = message.Role == MessageRole.Assistant ? "model" : "user", parts });
        }
        var config = new Dictionary<string, object?> { ["maxOutputTokens"] = request.MaxOutputTokens };
        var level = RequestPolicy.MapReasoning(request.Model, request.ReasoningIntent);
        var id = ModelId(request.Model.ModelId);
        if (level is not null)
        {
            if (IsGemini3OrLater(id) && request.Model.Reasoning!.Kind == ReasoningKind.Effort && level != ReasoningIntent.Off)
                config["thinkingConfig"] = new { thinkingLevel = (level == ReasoningIntent.Max ? ReasoningIntent.High : level.Value).ToString().ToUpperInvariant() };
            else if (IsGemini25(id) && request.Model.Reasoning!.Kind == ReasoningKind.BudgetTokens)
            {
                var budget = level.Value switch { ReasoningIntent.Off => 0, ReasoningIntent.Minimal or ReasoningIntent.Low => 1024, ReasoningIntent.Medium => 4096, ReasoningIntent.High => 8192, _ => 16384 };
                config["thinkingConfig"] = new { thinkingBudget = budget };
            }
        }
        var payload = new Dictionary<string, object?> { ["contents"] = contents, ["generationConfig"] = config };
        if (system.Count > 0) payload["systemInstruction"] = new { parts = system };
        if (request.Tools.Count > 0) payload["tools"] = new[] { new { functionDeclarations = request.Tools.Select(tool => new { name = tool.Name, description = tool.Description, parametersJsonSchema = tool.ParametersSchema }).ToArray() } };
        var serialized = JsonWire.Serialize(payload);
        // Inline limits include base64 expansion, system instructions, schemas and
        // all other JSON bytes, not merely the decoded image bytes.
        if (Encoding.UTF8.GetByteCount(serialized) > 20 * 1024 * 1024) throw new ProviderException("request_too_large");
        return serialized;
    }

    private static bool IsSignedText(string state, string text)
    {
        var part = ReplayPart(state);
        return JsonWire.String(part, "thoughtSignature") is not null && JsonWire.Get(part, "thought").ValueKind != JsonValueKind.True && JsonWire.String(part, "text") == text;
    }

    private static JsonElement ReplayPart(string state)
    {
        try
        {
            using var json = JsonDocument.Parse(state);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                (JsonWire.Get(root, "text").ValueKind == JsonValueKind.Undefined && JsonWire.Get(root, "functionCall").ValueKind == JsonValueKind.Undefined && JsonWire.Get(root, "thoughtSignature").ValueKind == JsonValueKind.Undefined)) throw new JsonException();
            return root.Clone();
        }
        catch (Exception exception) when (exception is JsonException || exception is InvalidOperationException)
        { throw new ProviderException("invalid_provider_state"); }
    }

    public async Task StreamAsync(ChatRequest request, string? credential, Func<StreamEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        try
        {
            var body = BuildPayload(request);
            var path = ApiPath(request.Configuration, "models/" + Uri.EscapeDataString(ModelId(request.Model.ModelId)) + ":streamGenerateContent?alt=sse");
            using var response = await transport.SendAsync(request.Configuration, path, credential, HttpMethod.Post, body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var code = HttpTransport.ErrorCode(response.StatusCode);
                await onEvent(new StreamError(code, code, HttpTransport.RetryAfterSeconds(response))).ConfigureAwait(false);
                return;
            }
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var parser = new GeminiStreamParser(onEvent);
            await StreamFrames.ReadAsync(stream, false, parser.AcceptAsync, cancellationToken).ConfigureAwait(false);
            await parser.CompleteAsync().ConfigureAwait(false);
        }
        catch (ProviderException exception) { await onEvent(new StreamError(exception.Code, exception.Code)).ConfigureAwait(false); }
        catch (JsonException) { await onEvent(new StreamError("invalid_response", "invalid_response")).ConfigureAwait(false); }
    }

    private static string ApiPath(ProviderConfiguration configuration, string suffix) =>
        new Uri(configuration.BaseUrl).AbsolutePath.TrimEnd('/').EndsWith("/v1beta", StringComparison.Ordinal) ? suffix : "v1beta/" + suffix;
    private static string ModelId(string name)
    {
        var id = name.StartsWith("models/", StringComparison.Ordinal) ? name.Substring(7) : name;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 1024 || id.IndexOfAny(new[] { '/', '\\', '?', '#', '\r', '\n' }) >= 0 || id == "." || id == "..") throw new ProviderException("invalid_model_id");
        return id;
    }
    private static bool IsGemini3OrLater(string id) => Regex.IsMatch(id, @"^gemini-(?:[3-9]|[1-9]\d)(?:[.-]|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static bool IsGemini25(string id) => id.StartsWith("gemini-2.5-", StringComparison.Ordinal);
    private static ReasoningSupport DefaultReasoning(string id) => new ReasoningSupport(
        IsGemini3OrLater(id) ? ReasoningKind.Effort : IsGemini25(id) ? ReasoningKind.BudgetTokens : ReasoningKind.ModelSelection,
        new[] { ReasoningIntent.Low, ReasoningIntent.High }, true);
    private static int? PositiveLimit(JsonElement item, string name)
    {
        var value = JsonWire.Int(item, name);
        if (value <= 0) throw new JsonException();
        return value;
    }
}

internal sealed class GeminiStreamParser
{
    private readonly Func<StreamEvent, Task> emit;
    private int nextCall;
    private bool hasTools;
    private bool hasFinishReason;
    private bool done;
    private bool failed;
    private FinishReason finish = FinishReason.Other;
    internal GeminiStreamParser(Func<StreamEvent, Task> emit) { this.emit = emit; }

    internal async Task AcceptAsync(string? eventName, string data)
    {
        if (done || failed) return;
        if (eventName is not null && eventName != "message") return;
        try
        {
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            if (JsonWire.Get(root, "error").ValueKind == JsonValueKind.Object)
            {
                failed = true;
                await emit(new StreamError("provider_error", "provider_error")).ConfigureAwait(false);
                return;
            }
            foreach (var candidate in JsonWire.Array(JsonWire.Get(root, "candidates")))
            {
                if ((JsonWire.Int(candidate, "index") ?? 0) != 0) continue;
                foreach (var part in JsonWire.Array(JsonWire.Get(JsonWire.Get(candidate, "content"), "parts")))
                {
                    var function = JsonWire.Get(part, "functionCall");
                    if (function.ValueKind == JsonValueKind.Object)
                    {
                        var name = JsonWire.String(function, "name") ?? throw new JsonException();
                        var id = JsonWire.String(function, "id");
                        if (string.IsNullOrEmpty(id)) id = "gemini-call-" + nextCall++;
                        var args = JsonWire.Get(function, "args");
                        if (args.ValueKind == JsonValueKind.Undefined)
                        {
                            using var empty = JsonDocument.Parse("{}");
                            args = empty.RootElement.Clone();
                        }
                        if (args.ValueKind != JsonValueKind.Object) throw new JsonException();
                        hasTools = true;
                        await emit(new ToolCallStart(id!, name)).ConfigureAwait(false);
                        await emit(new ToolCallArgsDelta(id!, args.GetRawText())).ConfigureAwait(false);
                        await emit(new ToolCallEnd(id!, args, part.GetRawText())).ConfigureAwait(false);
                        continue;
                    }
                    var text = JsonWire.String(part, "text");
                    var thought = JsonWire.Get(part, "thought").ValueKind == JsonValueKind.True;
                    var signature = JsonWire.String(part, "thoughtSignature");
                    if (thought) await emit(new ReasoningDelta(text ?? "", part.GetRawText())).ConfigureAwait(false);
                    else
                    {
                        if (text is not null) await emit(new TextDelta(text)).ConfigureAwait(false);
                        if (signature is not null) await emit(new ReasoningDelta("", part.GetRawText())).ConfigureAwait(false);
                    }
                }
                var reason = JsonWire.String(candidate, "finishReason");
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    hasFinishReason = true;
                    finish = JsonWire.Finish(reason);
                }
            }
            var usage = JsonWire.Get(root, "usageMetadata");
            if (usage.ValueKind == JsonValueKind.Object)
                await emit(new Usage(JsonWire.Int(usage, "promptTokenCount") ?? 0, JsonWire.Int(usage, "candidatesTokenCount") ?? 0, JsonWire.Int(usage, "thoughtsTokenCount"))).ConfigureAwait(false);
            var feedback = JsonWire.Get(root, "promptFeedback");
            if (JsonWire.String(feedback, "blockReason") is not null) finish = FinishReason.ContentFiltered;
        }
        catch (Exception exception) when (exception is JsonException || exception is ArgumentException || exception is InvalidOperationException)
        {
            failed = true;
            await emit(new StreamError("invalid_stream", "invalid_stream")).ConfigureAwait(false);
        }
    }

    internal async Task CompleteAsync()
    {
        if (done || failed) return;
        if (!hasFinishReason)
        {
            failed = true;
            await emit(new StreamError("invalid_stream", "invalid_stream")).ConfigureAwait(false);
            return;
        }
        done = true;
        await emit(new Done(hasTools && finish == FinishReason.Stop ? FinishReason.ToolUse : finish)).ConfigureAwait(false);
    }
}
