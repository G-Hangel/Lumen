using System;
using System.Collections.Concurrent;
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

public sealed class AnthropicProvider : IModelCatalog, IChatProvider
{
    private readonly HttpTransport transport;
    private readonly CapabilityResolver resolver;
    private readonly ConcurrentDictionary<(Guid Id, string Provider, ProviderKind Kind, string BaseUrl), IReadOnlyDictionary<string, ThinkingOptions>> thinkingModels =
        new ConcurrentDictionary<(Guid, string, ProviderKind, string), IReadOnlyDictionary<string, ThinkingOptions>>();

    public AnthropicProvider(HttpTransport transport, CapabilityResolver resolver)
    {
        this.transport = transport;
        this.resolver = resolver;
    }

    public async Task<CatalogResult> DiscoverAsync(ProviderConfiguration configuration, string? credential, CancellationToken cancellationToken)
    {
        try
        {
            var models = new List<ModelInfo>();
            var optionsByModel = new Dictionary<string, ThinkingOptions>(StringComparer.Ordinal);
            var totalEntries = 0;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? after = null;
            do
            {
                var path = ApiPath(configuration, "models?limit=1000") + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after));
                using var response = await transport.SendAsync(configuration, path, credential, HttpMethod.Get, null, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return HttpTransport.CatalogError(response);
                using var json = await HttpTransport.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                var root = json.RootElement;
                var data = JsonWire.Get(root, "data");
                if (data.ValueKind != JsonValueKind.Array) throw new JsonException();
                totalEntries = HttpTransport.CheckCatalogEntries(data, totalEntries);
                foreach (var item in data.EnumerateArray())
                {
                    var id = JsonWire.String(item, "id");
                    if (string.IsNullOrWhiteSpace(id) || id!.Length > 1024) throw new JsonException();
                    var native = new NativeModel(id, JsonWire.String(item, "display_name"))
                    {
                        ContextWindowTokens = PositiveLimit(item, "max_input_tokens"),
                        MaxOutputTokens = PositiveLimit(item, "max_tokens")
                    };
                    native.Capabilities[ModelCapabilities.Text] = true;
                    native.Capabilities[ModelCapabilities.Streaming] = true;
                    var caps = JsonWire.Get(item, "capabilities");
                    AddCapability(native, caps, "image_input", ModelCapabilities.Vision);
                    AddCapability(native, caps, "pdf_input", ModelCapabilities.Pdf);
                    AddCapability(native, caps, "structured_outputs", ModelCapabilities.StructuredOutput);
                    // server_tools is not evidence for caller-defined function tools.
                    var thinking = JsonWire.Get(caps, "thinking");
                    var supported = Boolean(thinking, "supported");
                    var options = ThinkingOptions.Read(caps, id);
                    if (supported is not null) native.Capabilities[ModelCapabilities.Reasoning] = supported.Value;
                    if (supported == true) native.Reasoning = options.Support();
                    var resolved = resolver.Resolve(configuration.ProviderId, native);
                    optionsByModel[id] = options;
                    models.Add(resolved);
                }
                if (Boolean(root, "has_more") != true) break;
                after = JsonWire.String(root, "last_id");
                if (string.IsNullOrEmpty(after) || !cursors.Add(after!)) throw new JsonException();
            } while (true);
            // Publish only a complete catalog, replacing metadata for removed models too.
            thinkingModels[Key(configuration)] = optionsByModel;
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
        var system = new List<object>();
        foreach (var message in request.Messages)
        {
            var content = new List<object>();
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case TextPart text:
                        content.Add(new { type = "text", text = text.Text });
                        break;
                    case ImagePart image:
                        content.Add(new { type = "image", source = new { type = "base64", media_type = image.MediaType, data = image.Base64Data } });
                        break;
                    case ReasoningPart reasoning when reasoning.ProviderState is not null:
                        content.Add(ReplayBlock(reasoning.ProviderState, true));
                        break;
                    case ToolCallPart call:
                        content.Add(call.ProviderState is null ? (object)new { type = "tool_use", id = call.Id, name = call.Name, input = call.Arguments } : ReplayBlock(call.ProviderState, false));
                        break;
                    case ToolResultPart result:
                        content.Add(new { type = "tool_result", tool_use_id = result.InvocationId, content = result.Content, is_error = result.IsError });
                        break;
                }
            }
            if (message.Role == MessageRole.System) system.AddRange(content);
            else if (content.Count > 0) messages.Add(new { role = message.Role == MessageRole.Assistant ? "assistant" : "user", content });
        }
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model.ModelId,
            ["messages"] = messages,
            ["stream"] = true,
            ["max_tokens"] = request.MaxOutputTokens
        };
        if (system.Count > 0) payload["system"] = system;
        if (request.Tools.Count > 0) payload["tools"] = request.Tools.Select(tool => new { name = tool.Name, description = tool.Description, input_schema = tool.ParametersSchema }).ToArray();
        ApplyThinking(request, payload);
        var serialized = JsonWire.Serialize(payload);
        if (Encoding.UTF8.GetByteCount(serialized) > 128L * 1024 * 1024) throw new ProviderException("request_too_large");
        return serialized;
    }

    private void ApplyThinking(ChatRequest request, Dictionary<string, object?> payload)
    {
        var level = RequestPolicy.MapReasoning(request.Model, request.ReasoningIntent);
        if (level is null) return;
        if (!thinkingModels.TryGetValue(Key(request.Configuration), out var models) || !models.TryGetValue(request.Model.ModelId, out var options))
        {
            // Persisted ModelInfo has no native thinking-type/effort subcapabilities.
            // Without a fresh catalog, do not synthesize an adaptive or effort knob.
            if (request.Model.Reasoning!.Kind == ReasoningKind.BudgetTokens && !RejectsBudget(request.Model.ModelId) && level != ReasoningIntent.Off)
                payload["thinking"] = new { type = "enabled", budget_tokens = Budget(level.Value, request.MaxOutputTokens) };
            return;
        }
        if (!options.Thinking) return;
        var off = level == ReasoningIntent.Off && !request.Model.Reasoning!.Mandatory;
        if (off && options.Disabled) payload["thinking"] = new { type = "disabled" };
        else if (off && options.BetweenTools) payload["thinking"] = new { type = "between_tools" };
        else if (options.Adaptive) payload["thinking"] = new { type = "adaptive" };
        else if (options.Enabled) payload["thinking"] = new { type = "enabled", budget_tokens = Budget(level.Value, request.MaxOutputTokens) };
        if (options.Efforts.Count > 0)
        {
            // Off is a thinking type, never an output_config.effort value. The lowest
            // supported effort also satisfies between_tools' <= high restriction.
            var requested = off ? ReasoningIntent.Low : level.Value;
            var effort = options.Efforts.Where(pair => pair.Key <= requested).OrderBy(pair => pair.Key).LastOrDefault();
            if (effort.Value is null) effort = options.Efforts.OrderBy(pair => pair.Key).First();
            if (off && effort.Key > ReasoningIntent.High) return;
            payload["output_config"] = new { effort = effort.Value };
        }
    }

    private static int Budget(ReasoningIntent level, int maxTokens)
    {
        if (maxTokens <= 1024) throw new ProviderException("reasoning_budget_too_small");
        var desired = level switch { ReasoningIntent.Minimal or ReasoningIntent.Low => 1024, ReasoningIntent.Medium => 4096, ReasoningIntent.High => 8192, _ => 16384 };
        return Math.Min(desired, maxTokens - 1);
    }

    private static JsonElement ReplayBlock(string state, bool reasoning)
    {
        try
        {
            using var json = JsonDocument.Parse(state);
            var root = json.RootElement;
            var type = JsonWire.String(root, "type");
            if (reasoning ? type != "thinking" && type != "redacted_thinking" : type != "tool_use") throw new JsonException();
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
            using var response = await transport.SendAsync(request.Configuration, ApiPath(request.Configuration, "messages"), credential, HttpMethod.Post, body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var code = HttpTransport.ErrorCode(response.StatusCode);
                await onEvent(new StreamError(code, code)).ConfigureAwait(false);
                return;
            }
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var parser = new AnthropicStreamParser(onEvent);
            await StreamFrames.ReadAsync(stream, false, parser.AcceptAsync, cancellationToken).ConfigureAwait(false);
            await parser.CompleteAsync().ConfigureAwait(false);
        }
        catch (ProviderException exception) { await onEvent(new StreamError(exception.Code, exception.Code)).ConfigureAwait(false); }
        catch (JsonException) { await onEvent(new StreamError("invalid_response", "invalid_response")).ConfigureAwait(false); }
    }

    private static string ApiPath(ProviderConfiguration configuration, string suffix) =>
        new Uri(configuration.BaseUrl).AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? suffix : "v1/" + suffix;
    private static (Guid Id, string Provider, ProviderKind Kind, string BaseUrl) Key(ProviderConfiguration configuration) =>
        (Guid.Parse(configuration.Id), configuration.ProviderId, configuration.Kind, configuration.BaseUrl);
    private static bool RejectsBudget(string id) => Regex.IsMatch(id, @"(?:^|-)claude-(?:opus-|sonnet-|haiku-|fable-|mythos-)?(?:4[.-](?:[7-9]|[1-9]\d)(?:[.-]|$)|[5-9](?:[.-]|$)|[1-9]\d(?:[.-]|$))", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static int? PositiveLimit(JsonElement item, string name)
    {
        var value = JsonWire.Int(item, name);
        if (value <= 0) throw new JsonException();
        return value;
    }
    private static bool? Boolean(JsonElement item, string name)
    {
        var value = JsonWire.Get(item, name);
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Undefined or JsonValueKind.Null => null, _ => throw new JsonException() };
    }
    private static bool? Supported(JsonElement caps, string name) => Boolean(JsonWire.Get(caps, name), "supported");
    private static void AddCapability(NativeModel model, JsonElement caps, string name, ModelCapabilities capability)
    {
        var supported = Supported(caps, name);
        if (supported is not null) model.Capabilities[capability] = supported.Value;
    }

    private sealed class ThinkingOptions
    {
        internal bool Thinking;
        internal bool Adaptive;
        internal bool Enabled;
        internal bool Disabled;
        internal bool BetweenTools;
        internal readonly Dictionary<ReasoningIntent, string> Efforts = new Dictionary<ReasoningIntent, string>();
        internal static ThinkingOptions Read(JsonElement caps, string id)
        {
            var thinking = JsonWire.Get(caps, "thinking");
            var types = JsonWire.Get(thinking, "types");
            var result = new ThinkingOptions
            {
                Thinking = Boolean(thinking, "supported") == true,
                Adaptive = Supported(types, "adaptive") == true,
                Enabled = Supported(types, "enabled") == true && !RejectsBudget(id),
                Disabled = Supported(types, "disabled") == true,
                BetweenTools = Supported(types, "between_tools") == true
            };
            var effort = JsonWire.Get(caps, "effort");
            if (Boolean(effort, "supported") == true)
            {
                foreach (var pair in new[] { (ReasoningIntent.Low, "low"), (ReasoningIntent.Medium, "medium"), (ReasoningIntent.High, "high"), (ReasoningIntent.Max, "max") })
                    if (Supported(effort, pair.Item2) == true) result.Efforts[pair.Item1] = pair.Item2;
                // xhigh has no unified enum member; Max means native max only.
            }
            return result;
        }
        internal ReasoningSupport Support()
        {
            var efforts = Efforts.Keys.OrderBy(level => level).ToList();
            if (efforts.Count == 0)
            {
                if (Enabled && !Adaptive) efforts.AddRange(new[] { ReasoningIntent.Minimal, ReasoningIntent.Low, ReasoningIntent.Medium, ReasoningIntent.High, ReasoningIntent.Max });
                else efforts.Add(ReasoningIntent.High);
            }
            if (Disabled || BetweenTools) efforts.Insert(0, ReasoningIntent.Off);
            return new ReasoningSupport(Adaptive ? ReasoningKind.Effort : Enabled ? ReasoningKind.BudgetTokens : ReasoningKind.ModelSelection,
                efforts, !Disabled && !BetweenTools);
        }
    }
}

internal sealed class AnthropicStreamParser
{
    private sealed class Block
    {
        internal JsonElement Original;
        internal string Type = "";
        internal string? Id;
        internal readonly StringBuilder Text = new StringBuilder();
        internal readonly StringBuilder Signature = new StringBuilder();
        internal readonly StringBuilder Arguments = new StringBuilder();
    }
    private readonly Func<StreamEvent, Task> emit;
    private readonly Dictionary<int, Block> blocks = new Dictionary<int, Block>();
    private FinishReason finish = FinishReason.Other;
    private int inputTokens;
    private int outputTokens;
    private bool messageStopped;
    private bool done;
    private bool failed;
    internal AnthropicStreamParser(Func<StreamEvent, Task> emit) { this.emit = emit; }

    internal async Task AcceptAsync(string? eventName, string data)
    {
        if (done || failed) return;
        // Unknown events may carry a completely different (or non-JSON) schema.
        if (eventName is not null && eventName != "message_start" && eventName != "content_block_start" && eventName != "content_block_delta" && eventName != "content_block_stop" && eventName != "message_delta" && eventName != "message_stop" && eventName != "error") return;
        try
        {
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            var type = eventName ?? JsonWire.String(root, "type");
            switch (type)
            {
                case "message_start":
                    await EmitUsage(JsonWire.Get(JsonWire.Get(root, "message"), "usage")).ConfigureAwait(false);
                    break;
                case "content_block_start":
                    var index = Index(root);
                    if (blocks.ContainsKey(index)) throw new JsonException();
                    var original = JsonWire.Get(root, "content_block");
                    if (original.ValueKind != JsonValueKind.Object) throw new JsonException();
                    var block = new Block { Original = original.Clone(), Type = JsonWire.String(original, "type") ?? "", Id = JsonWire.String(original, "id") };
                    blocks.Add(index, block);
                    if (block.Type == "text")
                    {
                        var text = JsonWire.String(original, "text") ?? "";
                        block.Text.Append(text);
                        if (text.Length > 0) await emit(new TextDelta(text)).ConfigureAwait(false);
                    }
                    else if (block.Type == "thinking")
                    {
                        var text = JsonWire.String(original, "thinking") ?? "";
                        block.Text.Append(text);
                        block.Signature.Append(JsonWire.String(original, "signature") ?? "");
                        if (text.Length > 0) await emit(new ReasoningDelta(text)).ConfigureAwait(false);
                    }
                    else if (block.Type == "tool_use")
                        await emit(new ToolCallStart(block.Id!, JsonWire.String(original, "name")!)).ConfigureAwait(false);
                    break;
                case "content_block_delta":
                    if (!blocks.TryGetValue(Index(root), out var current)) throw new JsonException();
                    var delta = JsonWire.Get(root, "delta");
                    switch (JsonWire.String(delta, "type"))
                    {
                        case "text_delta" when current.Type == "text":
                            var text = JsonWire.String(delta, "text") ?? throw new JsonException();
                            current.Text.Append(text);
                            await emit(new TextDelta(text)).ConfigureAwait(false);
                            break;
                        case "thinking_delta" when current.Type == "thinking":
                            var thought = JsonWire.String(delta, "thinking") ?? throw new JsonException();
                            current.Text.Append(thought);
                            await emit(new ReasoningDelta(thought)).ConfigureAwait(false);
                            break;
                        case "signature_delta" when current.Type == "thinking":
                            current.Signature.Append(JsonWire.String(delta, "signature") ?? throw new JsonException());
                            break;
                        case "input_json_delta" when current.Type == "tool_use":
                            var arguments = JsonWire.String(delta, "partial_json") ?? throw new JsonException();
                            current.Arguments.Append(arguments);
                            await emit(new ToolCallArgsDelta(current.Id!, arguments)).ConfigureAwait(false);
                            break;
                    }
                    if (current.Arguments.Length + current.Text.Length + current.Signature.Length > 1024 * 1024) throw new JsonException();
                    break;
                case "content_block_stop":
                    var stopIndex = Index(root);
                    if (!blocks.TryGetValue(stopIndex, out var stopped)) throw new JsonException();
                    if (stopped.Type == "thinking")
                    {
                        var state = stopped.Original.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
                        state["thinking"] = stopped.Text.ToString();
                        state["signature"] = stopped.Signature.ToString();
                        await emit(new ReasoningDelta("", JsonWire.Serialize(state))).ConfigureAwait(false);
                    }
                    else if (stopped.Type == "redacted_thinking")
                        await emit(new ReasoningDelta("", stopped.Original.GetRawText())).ConfigureAwait(false);
                    else if (stopped.Type == "tool_use")
                    {
                        using var args = JsonDocument.Parse(stopped.Arguments.Length == 0 ? JsonWire.Get(stopped.Original, "input").GetRawText() : stopped.Arguments.ToString());
                        await emit(new ToolCallEnd(stopped.Id!, args.RootElement)).ConfigureAwait(false);
                    }
                    blocks.Remove(stopIndex);
                    break;
                case "message_delta":
                    finish = JsonWire.Finish(JsonWire.String(JsonWire.Get(root, "delta"), "stop_reason"));
                    await EmitUsage(JsonWire.Get(root, "usage")).ConfigureAwait(false);
                    break;
                case "message_stop":
                    messageStopped = true;
                    await CompleteAsync().ConfigureAwait(false);
                    break;
                case "error":
                    failed = true;
                    await emit(new StreamError("provider_error", "provider_error")).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException || exception is ArgumentException || exception is InvalidOperationException)
        {
            failed = true;
            await emit(new StreamError("invalid_stream", "invalid_stream")).ConfigureAwait(false);
        }
    }

    private async Task EmitUsage(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return;
        inputTokens = JsonWire.Int(usage, "input_tokens") ?? inputTokens;
        outputTokens = JsonWire.Int(usage, "output_tokens") ?? outputTokens;
        await emit(new Usage(inputTokens, outputTokens, JsonWire.Int(JsonWire.Get(usage, "output_tokens_details"), "thinking_tokens"))).ConfigureAwait(false);
    }
    private static int Index(JsonElement root) => JsonWire.Int(root, "index") is int index && index >= 0 ? index : throw new JsonException();
    internal async Task CompleteAsync()
    {
        if (done || failed) return;
        if (!messageStopped || blocks.Count > 0)
        {
            failed = true;
            await emit(new StreamError("invalid_stream", "invalid_stream")).ConfigureAwait(false);
            return;
        }
        done = true;
        await emit(new Done(finish)).ConfigureAwait(false);
    }
}
