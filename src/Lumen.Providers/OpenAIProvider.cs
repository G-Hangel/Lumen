using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public class OpenAIProvider : IModelCatalog, IChatProvider
{
    protected readonly HttpTransport Transport;
    protected readonly CapabilityResolver Resolver;
    public OpenAIProvider(HttpTransport transport, CapabilityResolver resolver) { Transport = transport; Resolver = resolver; }

    public virtual async Task<CatalogResult> DiscoverAsync(ProviderConfiguration configuration, string? credential, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Transport.SendAsync(configuration, "models", credential, HttpMethod.Get, null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return HttpTransport.CatalogError(response);
            using var json = await HttpTransport.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var data = JsonWire.Get(json.RootElement, "data");
            if (data.ValueKind != JsonValueKind.Array) return new CatalogResult(Array.Empty<ModelInfo>(), null, "invalid_response");
            HttpTransport.CheckCatalogEntries(data);
            var models = new List<ModelInfo>();
            foreach (var item in data.EnumerateArray())
            {
                var id = JsonWire.String(item, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var model = new NativeModel(id!);
                var context = JsonWire.Int(item, "max_model_len");
                if (context > 0) model.ContextWindowTokens = context;
                models.Add(Resolver.Resolve(configuration.ProviderId, model));
            }
            return new CatalogResult(models);
        }
        catch (ProviderException exception) { return new CatalogResult(Array.Empty<ModelInfo>(), null, exception.Code); }
        catch (JsonException) { return new CatalogResult(Array.Empty<ModelInfo>(), null, "invalid_response"); }
    }

    public virtual string BuildPayload(ChatRequest request)
    {
        RequestPolicy.Validate(request);
        var messages = new List<object>();
        foreach (var message in request.Messages)
        {
            foreach (var result in message.Parts.OfType<ToolResultPart>()) messages.Add(new { role = "tool", tool_call_id = result.InvocationId, content = result.Content });
            var content = new List<object>();
            foreach (var part in message.Parts)
            {
                if (part is TextPart text) content.Add(new { type = "text", text = text.Text });
                if (part is ImagePart image) content.Add(new { type = "image_url", image_url = new { url = "data:" + image.MediaType + ";base64," + image.Base64Data, detail = image.Detail.ToString().ToLowerInvariant() } });
            }
            var calls = message.Parts.OfType<ToolCallPart>().Select(call => new { id = call.Id, type = "function", function = new { name = call.Name, arguments = call.Arguments.GetRawText() } }).ToArray();
            if (content.Count == 0 && calls.Length == 0 && !message.Parts.OfType<ReasoningPart>().Any()) continue;
            var wire = new Dictionary<string, object?> { ["role"] = message.Role.ToString().ToLowerInvariant(), ["content"] = content.Count > 0 ? content : null };
            if (calls.Length > 0) wire["tool_calls"] = calls;
            var reasoning = message.Parts.OfType<ReasoningPart>().ToArray();
            if (request.Configuration.ProviderId == "deepseek" && reasoning.Length > 0) wire["reasoning_content"] = string.Concat(reasoning.Select(part => part.Text));
            if (request.Configuration.Kind == ProviderKind.OpenRouter)
            {
                var details = MergeReasoningDetails(reasoning.Where(part => part.ProviderState is not null).SelectMany(part => ReadReasoningDetails(part.ProviderState!)));
                if (details.Length > 0) wire["reasoning_details"] = details;
            }
            messages.Add(wire);
        }
        var payload = new Dictionary<string, object?> { ["model"] = request.Model.ModelId, ["messages"] = messages, ["stream"] = true, [request.Configuration.ProviderId == "openai" || request.Configuration.Kind == ProviderKind.Azure ? "max_completion_tokens" : "max_tokens"] = request.MaxOutputTokens };
        if (request.Configuration.ProviderId == "openai" || request.Configuration.Kind == ProviderKind.OpenRouter || request.Configuration.Kind == ProviderKind.Azure) payload["stream_options"] = new { include_usage = true };
        if (request.Tools.Count > 0) payload["tools"] = request.Tools.Select(tool => new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.ParametersSchema } }).ToArray();
        var level = RequestPolicy.MapReasoning(request.Model, request.ReasoningIntent);
        if (level is not null)
        {
            if (request.Configuration.Kind == ProviderKind.OpenRouter) payload["reasoning"] = new { effort = level == ReasoningIntent.Off ? "none" : level.ToString()!.ToLowerInvariant() };
            else if (request.Model.Reasoning!.Kind == ReasoningKind.TemplateFlag) payload["chat_template_kwargs"] = new { enable_thinking = level != ReasoningIntent.Off };
            else if (request.Model.Reasoning.Kind == ReasoningKind.Effort)
            {
                payload.Remove("max_tokens"); payload["max_completion_tokens"] = request.MaxOutputTokens;
                payload["reasoning_effort"] = level == ReasoningIntent.Off ? "none" : level.ToString()!.ToLowerInvariant();
            }
        }
        return JsonWire.Serialize(payload);
    }

    private static IEnumerable<JsonElement> ReadReasoningDetails(string state)
    {
        using var parsed = JsonDocument.Parse(state);
        return JsonWire.Array(parsed.RootElement).Select(element => element.Clone()).ToArray();
    }

    private static JsonElement[] MergeReasoningDetails(IEnumerable<JsonElement> fragments)
    {
        var details = new List<JsonElement>();
        foreach (var fragment in fragments)
        {
            var position = details.FindIndex(detail => SameReasoningDetail(detail, fragment));
            if (position < 0) { details.Add(fragment); continue; }
            var previous = details[position];
            var fields = previous.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            var deltaField = JsonWire.String(fragment, "type") switch
            {
                "reasoning.text" => "text",
                "reasoning.summary" => "summary",
                "reasoning.encrypted" => "data",
                _ => null
            };
            foreach (var property in fragment.EnumerateObject())
            {
                if (fields.TryGetValue(property.Name, out var value))
                {
                    if (property.Name == deltaField && value.ValueKind == JsonValueKind.String && property.Value.ValueKind == JsonValueKind.String)
                    {
                        fields[property.Name] = JsonSerializer.SerializeToElement(value.GetString() + property.Value.GetString());
                        continue;
                    }
                    // A later null placeholder must not erase an opaque signature or native identity.
                    if (property.Value.ValueKind == JsonValueKind.Null && value.ValueKind != JsonValueKind.Null) continue;
                }
                fields[property.Name] = property.Value;
            }
            details[position] = JsonSerializer.SerializeToElement(fields);
        }
        return details.ToArray();
    }

    private static bool SameReasoningDetail(JsonElement previous, JsonElement fragment)
    {
        var type = JsonWire.String(fragment, "type");
        if (type is null || type != JsonWire.String(previous, "type")) return false;
        var index = JsonWire.Int(fragment, "index");
        var previousIndex = JsonWire.Int(previous, "index");
        var id = JsonWire.String(fragment, "id");
        var previousId = JsonWire.String(previous, "id");
        if (index.HasValue && previousIndex.HasValue)
            return index == previousIndex && (id is null || previousId is null || id == previousId);
        return !string.IsNullOrEmpty(id) && id == previousId;
    }

    protected virtual string ChatPath(ChatRequest request) => "chat/completions";

    public async Task StreamAsync(ChatRequest request, string? credential, Func<StreamEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        try
        {
            var body = BuildPayload(request);
            using var response = await Transport.SendAsync(request.Configuration, ChatPath(request), credential, HttpMethod.Post, body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { await onEvent(new StreamError(HttpTransport.ErrorCode(response.StatusCode), HttpTransport.ErrorCode(response.StatusCode))).ConfigureAwait(false); return; }
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var parser = new OpenAIStreamParser(onEvent);
            await StreamFrames.ReadAsync(stream, false, parser.AcceptAsync, cancellationToken).ConfigureAwait(false);
            await parser.CompleteAsync().ConfigureAwait(false);
        }
        catch (ProviderException exception) { await onEvent(new StreamError(exception.Code, exception.Code)).ConfigureAwait(false); }
        catch (JsonException) { await onEvent(new StreamError("invalid_response", "invalid_response")).ConfigureAwait(false); }
    }
}

internal sealed class OpenAIStreamParser
{
    private sealed class Call { internal string? Id; internal string? Name; internal bool Started; internal readonly StringBuilder Arguments = new StringBuilder(); }
    private readonly Func<StreamEvent, Task> emit;
    private readonly SortedDictionary<int, Call> calls = new SortedDictionary<int, Call>();
    private FinishReason finish = FinishReason.Other;
    private bool terminalSeen;
    private bool done;
    private bool failed;
    internal OpenAIStreamParser(Func<StreamEvent, Task> emit) { this.emit = emit; }
    internal async Task AcceptAsync(string? eventName, string data)
    {
        if (done || failed) return;
        if (data == "[DONE]") { terminalSeen = true; await CompleteAsync().ConfigureAwait(false); return; }
        try
        {
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            if (JsonWire.Get(root, "error").ValueKind == JsonValueKind.Object) { failed = true; await emit(new StreamError("provider_error", "provider_error")).ConfigureAwait(false); return; }
            foreach (var choice in JsonWire.Array(JsonWire.Get(root, "choices")))
            {
                if ((JsonWire.Int(choice, "index") ?? 0) != 0) continue;
                var delta = JsonWire.Get(choice, "delta");
                var text = JsonWire.String(delta, "content");
                if (text is not null) await emit(new TextDelta(text)).ConfigureAwait(false);
                var reasoning = JsonWire.String(delta, "reasoning_content") ?? JsonWire.String(delta, "reasoning");
                var details = JsonWire.Get(delta, "reasoning_details");
                if (reasoning is not null || details.ValueKind == JsonValueKind.Array) await emit(new ReasoningDelta(reasoning ?? "", details.ValueKind == JsonValueKind.Array ? details.GetRawText() : null)).ConfigureAwait(false);
                foreach (var fragment in JsonWire.Array(JsonWire.Get(delta, "tool_calls")))
                {
                    var index = JsonWire.Int(fragment, "index") ?? 0;
                    if (!calls.TryGetValue(index, out var call)) { call = new Call(); calls.Add(index, call); }
                    var id = JsonWire.String(fragment, "id");
                    var function = JsonWire.Get(fragment, "function");
                    var name = JsonWire.String(function, "name");
                    if ((!string.IsNullOrEmpty(call.Id) && id is not null && id != call.Id)
                        || (!string.IsNullOrEmpty(call.Name) && name is not null && name != call.Name)) throw new JsonException();
                    call.Id = id ?? call.Id;
                    call.Name = name ?? call.Name;
                    if (!call.Started && !string.IsNullOrEmpty(call.Id) && !string.IsNullOrEmpty(call.Name)) { call.Started = true; await emit(new ToolCallStart(call.Id!, call.Name!)).ConfigureAwait(false); }
                    var arguments = JsonWire.String(function, "arguments");
                    if (arguments is not null)
                    {
                        call.Arguments.Append(arguments);
                        if (call.Arguments.Length > 1024 * 1024) throw new JsonException();
                        if (call.Started) await emit(new ToolCallArgsDelta(call.Id!, arguments)).ConfigureAwait(false);
                    }
                }
                var stop = JsonWire.String(choice, "finish_reason");
                if (stop is not null) { terminalSeen = true; finish = JsonWire.Finish(stop); }
            }
            var usage = JsonWire.Get(root, "usage");
            if (usage.ValueKind == JsonValueKind.Object) await emit(new Usage(JsonWire.Int(usage, "prompt_tokens") ?? 0, JsonWire.Int(usage, "completion_tokens") ?? 0, JsonWire.Int(JsonWire.Get(usage, "completion_tokens_details"), "reasoning_tokens"))).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException || exception is ArgumentException || exception is InvalidOperationException)
        { failed = true; await emit(new StreamError("invalid_stream", "invalid_stream")).ConfigureAwait(false); }
    }
    internal async Task CompleteAsync()
    {
        if (done || failed) return;
        done = true;
        try
        {
            if (!terminalSeen) throw new JsonException();
            foreach (var call in calls.Values)
            {
                if (!call.Started) throw new JsonException();
                using var parsed = JsonDocument.Parse(call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString());
                await emit(new ToolCallEnd(call.Id!, parsed.RootElement)).ConfigureAwait(false);
            }
            await emit(new Done(finish)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException || exception is ArgumentException)
        { await emit(new StreamError("invalid_stream", "invalid_stream")).ConfigureAwait(false); }
    }
}
