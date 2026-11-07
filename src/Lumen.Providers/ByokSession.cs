using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Bridge;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public sealed class ByokSession : IDisposable
{
    private readonly ProviderFactory providers;
    private readonly ICredentialStore credentials;
    private readonly SettingsStore settings;
    private readonly CapabilityProbe probe;
    private readonly bool canProbe;
    private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
    private readonly List<ProviderConfiguration> configurations = new List<ProviderConfiguration>();
    private readonly Dictionary<string, IReadOnlyList<ModelInfo>> models = new Dictionary<string, IReadOnlyList<ModelInfo>>();
    private readonly List<Message> history = new List<Message>();
    private readonly object activeGate = new object();
    private CancellationTokenSource? active;
    private bool loaded;
    private string? historyModel;
    private readonly HashSet<string> requestIds = new HashSet<string>();
    public ByokSession(ProviderFactory providers, ICredentialStore credentials, SettingsStore settings, CapabilityHttpProbe? httpProbe = null)
    {
        this.providers = providers; this.credentials = credentials; this.settings = settings;
        canProbe = httpProbe is not null;
        probe = new CapabilityProbe(async (configurationId, modelId, capability, token) =>
        {
            var configuration = configurations.First(value => value.Id == configurationId);
            return httpProbe is null ? null : await httpProbe.ProbeAsync(configuration, FindModel(configuration, modelId),
                await credentials.ReadAsync(configuration.Id, token).ConfigureAwait(false), capability, token).ConfigureAwait(false);
        });
    }

    public static bool IsRequest(string json)
    {
        if (json.Length > 8 * 1024 * 1024) return false;
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            return JsonWire.String(parsed.RootElement, "type")?.StartsWith("byok.", StringComparison.Ordinal) == true;
        }
        catch (Exception exception) when (exception is JsonException || exception is InvalidOperationException) { return false; }
    }

    public async Task<bool> HandleAsync(string source, string json, Func<string, Task> send, CancellationToken cancellationToken)
    {
        if (!BridgeProtocol.IsTrustedSource(source)) return true;
        string? id = null;
        string? type = null;
        JsonElement payload;
        try
        {
            if (json.Length > 8 * 1024 * 1024) throw new ProviderException("payload_too_large");
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            type = JsonWire.String(parsed.RootElement, "type");
            if (type?.StartsWith("byok.", StringComparison.Ordinal) != true) return false;
            if (!Fields(parsed.RootElement, new[] { "version", "id", "type", "payload" })) throw new ProviderException("invalid_message");
            id = JsonWire.String(parsed.RootElement, "id");
            if (!Guid.TryParseExact(id, "D", out var guid) || guid == Guid.Empty || JsonWire.Int(parsed.RootElement, "version") != 1) throw new ProviderException("invalid_message");
            payload = JsonWire.Get(parsed.RootElement, "payload").Clone();
        }
        catch (Exception exception) when (exception is JsonException || exception is InvalidOperationException || exception is ProviderException)
        { await Reply(send, id, "error", new { code = exception is ProviderException error ? error.Code : "invalid_message" }).ConfigureAwait(false); return true; }
        var requestLimitReached = false;
        lock (requestIds)
        {
            var canonicalId = Guid.Parse(id!).ToString("D");
            if (requestIds.Contains(canonicalId)) return true;
            if (requestIds.Count >= 1024) requestLimitReached = type != "byok.cancel";
            else requestIds.Add(canonicalId);
        }
        if (requestLimitReached)
        {
            await Reply(send, id, "error", new { code = "bridge_busy" }).ConfigureAwait(false);
            return true;
        }
        if (type == "byok.cancel")
        {
            if (!Fields(payload, Array.Empty<string>())) { await Reply(send, id, "error", new { code = "invalid_message" }).ConfigureAwait(false); return true; }
            lock (activeGate) active?.Cancel();
            await Reply(send, id, type + ".result", new { cancelled = true }).ConfigureAwait(false);
            return true;
        }
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { await Reply(send, id, "error", new { code = "bridge_busy" }).ConfigureAwait(false); return true; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        cancellationToken = deadline.Token;
        lock (activeGate) active = deadline;
        try
        {
            if (!loaded)
            {
                var savedConfigurations = await settings.ReadAsync(cancellationToken).ConfigureAwait(false);
                await settings.ReadProbeCacheAsync(probe.Store, cancellationToken).ConfigureAwait(false);
                configurations.AddRange(savedConfigurations);
                loaded = true;
            }
            switch (type)
            {
                case "byok.list":
                    Require(payload, Array.Empty<string>());
                    await Reply(send, id, type + ".result", new { configurations = configurations.Select(ConfigurationView).ToArray(), remainingBudget = probe.RemainingBudget }).ConfigureAwait(false);
                    break;
                case "byok.configure":
                    Require(payload, new[] { "id", "providerId", "kind", "baseUrl", "key" }, new[] { "deploymentNames" });
                    var kindText = RequiredString(payload, "kind", 32);
                    if (!Enum.TryParse<ProviderKind>(kindText, out var kind) || !Enum.IsDefined(typeof(ProviderKind), kind)) throw new ProviderException("invalid_message");
                    var names = JsonWire.Array(JsonWire.Get(payload, "deploymentNames")).Select(value => value.GetString()!).ToArray();
                    var configuration = new ProviderConfiguration(RequiredString(payload, "id", 36), RequiredString(payload, "providerId", 64), kind, RequiredString(payload, "baseUrl", 2048), null, names);
                    var secret = RequiredString(payload, "key", 4096, true);
                    var existing = configurations.FirstOrDefault(value => value.Id == configuration.Id);
                    if (existing is not null && (existing.Kind != configuration.Kind || existing.ProviderId != configuration.ProviderId || existing.BaseUrl != configuration.BaseUrl))
                        throw new ProviderException("invalid_configuration");
                    RejectSecretMetadata(configuration, Array.Empty<ModelInfo>(), secret);
                    var discovery = await providers.Catalog(kind).DiscoverAsync(configuration, secret, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectSecretMetadata(configuration, discovery.Models, secret);
                    discovery = ApplyProbeCatalog(configuration, discovery);
                    if (discovery.CredentialValid == true || (kind == ProviderKind.OpenRouter && discovery.CredentialValid is null && discovery.ErrorCode is null))
                    {
                        if (!string.IsNullOrEmpty(secret)) await credentials.WriteAsync(configuration.Id, secret, cancellationToken).ConfigureAwait(false);
                        else await credentials.DeleteAsync(configuration.Id, cancellationToken).ConfigureAwait(false);
                        configurations.RemoveAll(value => value.Id == configuration.Id);
                        configurations.Add(configuration);
                        models[configuration.Id] = discovery.Models;
                        await settings.WriteAsync(configurations, cancellationToken).ConfigureAwait(false);
                    }
                    await Reply(send, id, type + ".result", CatalogView(configuration, discovery)).ConfigureAwait(false);
                    break;
                case "byok.discover":
                    Require(payload, new[] { "configurationId" });
                    var current = Find(payload);
                    var currentSecret = await credentials.ReadAsync(current.Id, cancellationToken).ConfigureAwait(false);
                    var result = await providers.Catalog(current.Kind).DiscoverAsync(current, currentSecret, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectSecretMetadata(current, result.Models, currentSecret);
                    result = ApplyProbeCatalog(current, result);
                    models[current.Id] = result.Models;
                    await Reply(send, id, type + ".result", CatalogView(current, result)).ConfigureAwait(false);
                    break;
                case "byok.remove":
                    Require(payload, new[] { "configurationId" });
                    var removed = Find(payload);
                    await credentials.DeleteAsync(removed.Id, cancellationToken).ConfigureAwait(false);
                    configurations.Remove(removed);
                    models.Remove(removed.Id);
                    probe.Store.RemoveProvider(removed.Id);
                    history.Clear(); historyModel = null;
                    await settings.WriteProbeCacheAsync(probe.Store, cancellationToken).ConfigureAwait(false);
                    await settings.WriteAsync(configurations, cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new { removed = true }).ConfigureAwait(false);
                    break;
                case "byok.select":
                    Require(payload, new[] { "configurationId", "modelId" });
                    var selected = Find(payload);
                    var modelId = RequiredString(payload, "modelId", 512);
                    FindModel(selected, modelId);
                    var replacement = new ProviderConfiguration(selected.Id, selected.ProviderId, selected.Kind, selected.BaseUrl, modelId, selected.DeploymentNames);
                    configurations[configurations.IndexOf(selected)] = replacement;
                    history.Clear(); historyModel = null;
                    await settings.WriteAsync(configurations, cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new { configuration = ConfigurationView(replacement) }).ConfigureAwait(false);
                    break;
                case "byok.override":
                    Require(payload, new[] { "configurationId", "modelId", "vision", "tools", "reasoning" });
                    var owner = Find(payload);
                    var previous = FindModel(owner, RequiredString(payload, "modelId", 512));
                    var caps = previous.Capabilities;
                    var origins = previous.Origins.ToList();
                    foreach (var capability in new[] { ModelCapabilities.Vision, ModelCapabilities.Tools, ModelCapabilities.Reasoning })
                    {
                        var value = JsonWire.Get(payload, capability.ToString().ToLowerInvariant());
                        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False) throw new ProviderException("invalid_message");
                        caps = value.GetBoolean() ? caps | capability : caps & ~capability;
                        origins.RemoveAll(origin => origin.Capability == capability);
                        origins.Add(new CapabilityOrigin(capability, CapabilitySource.UserOverride, Confidence.High));
                    }
                    var overridden = new ModelInfo(owner.ProviderId, previous.ModelId, caps, previous.DisplayName,
                        (caps & ModelCapabilities.Reasoning) != 0 ? previous.Reasoning ?? new ReasoningSupport(ReasoningKind.ModelSelection, new[] { ReasoningIntent.Off, ReasoningIntent.Low }) : null,
                        previous.ContextWindowTokens, previous.MaxOutputTokens, origins);
                    models[owner.Id] = models[owner.Id].Select(value => value.ModelId == overridden.ModelId ? overridden : value).ToArray();
                    await Reply(send, id, type + ".result", new { model = ModelView(overridden) }).ConfigureAwait(false);
                    break;
                case "byok.probe":
                    Require(payload, new[] { "configurationId", "capability", "consent" });
                    if (JsonWire.Get(payload, "consent").ValueKind != JsonValueKind.True) throw new ProviderException("consent_required");
                    var probeConfiguration = Find(payload);
                    var probeModel = FindModel(probeConfiguration, probeConfiguration.SelectedModelId ?? "");
                    var capabilityName = RequiredString(payload, "capability", 16);
                    var probeCapability = capabilityName == "Vision" ? ModelCapabilities.Vision : capabilityName == "Tools" ? ModelCapabilities.Tools : throw new ProviderException("invalid_message");
                    if (!canProbe || !CapabilityHttpProbe.Supports(probeConfiguration.Kind)) throw new ProviderException("unsupported_type");
                    var support = await probe.ProbeAsync(probeConfiguration.Id, probeModel.ModelId, probeCapability, true, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var probedModel = ApplyProbe(probeConfiguration, probeModel);
                    models[probeConfiguration.Id] = models[probeConfiguration.Id].Select(value => value.ModelId == probedModel.ModelId ? probedModel : value).ToArray();
                    await settings.WriteProbeCacheAsync(probe.Store, cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new { model = ModelView(probedModel), supported = support, remainingBudget = probe.RemainingBudget }).ConfigureAwait(false);
                    break;
                case "byok.chat":
                    Require(payload, new[] { "configurationId", "text", "effort" }, new[] { "image" });
                    var chatConfiguration = Find(payload);
                    var chatModel = FindModel(chatConfiguration, chatConfiguration.SelectedModelId ?? "");
                    if (!Enum.TryParse<ReasoningIntent>(RequiredString(payload, "effort", 16), out var effort) || !Enum.IsDefined(typeof(ReasoningIntent), effort)) throw new ProviderException("invalid_message");
                    var text = RequiredString(payload, "text", 4096);
                    var parts = new List<ContentPart> { new TextPart(text) };
                    var image = JsonWire.Get(payload, "image");
                    if (image.ValueKind != JsonValueKind.Undefined)
                    {
                        Require(image, new[] { "mediaType", "base64Data" });
                        parts.Add(new ImagePart(RequiredString(image, "mediaType", 32), RequiredString(image, "base64Data", 6 * 1024 * 1024)));
                    }
                    if (historyModel != chatConfiguration.Id + "/" + chatModel.ModelId) { history.Clear(); historyModel = chatConfiguration.Id + "/" + chatModel.ModelId; }
                    var userMessage = new Message(MessageRole.User, parts);
                    var assistant = new List<ContentPart>();
                    var callNames = new Dictionary<string, string>();
                    var callIds = new HashSet<string>();
                    long streamSize = 0;
                    var eventCount = 0;
                    var failed = false;
                    {
                        var outputTokens = chatModel.Reasoning?.Kind == ReasoningKind.BudgetTokens && effort != ReasoningIntent.Off ? 4096 : 1024;
                        await providers.Chat(chatConfiguration.Kind).StreamAsync(new ChatRequest(chatConfiguration, chatModel, history.Concat(new[] { userMessage }).ToArray(), effort, maxOutputTokens: outputTokens),
                            await credentials.ReadAsync(chatConfiguration.Id, cancellationToken).ConfigureAwait(false), async streamEvent =>
                            {
                                streamSize += streamEvent switch
                                {
                                    TextDelta delta => delta.Text.Length,
                                    ReasoningDelta delta => delta.Text.Length + (delta.ProviderState?.Length ?? 0),
                                    ToolCallArgsDelta delta => delta.Chunk.Length,
                                    ToolCallEnd call => call.ParsedArgs.GetRawText().Length + (call.ProviderState?.Length ?? 0),
                                    _ => 0
                                };
                                if (streamSize > 8 * 1024 * 1024 || ++eventCount > 32768) throw new ProviderException("stream_too_large");
                                switch (streamEvent)
                                {
                                    case TextDelta delta:
                                        if (chatConfiguration.Kind == ProviderKind.Gemini) assistant.Add(new TextPart(delta.Text));
                                        else AppendText(assistant, delta.Text);
                                        break;
                                    case ReasoningDelta delta:
                                        if (assistant.LastOrDefault() is ReasoningPart reasoning && reasoning.ProviderState is null)
                                        {
                                            assistant[assistant.Count - 1] = new ReasoningPart(reasoning.Text + delta.Text, delta.ProviderState ?? reasoning.ProviderState);
                                        }
                                        else assistant.Add(new ReasoningPart(delta.Text, delta.ProviderState));
                                        break;
                                    case ToolCallStart call:
                                        if (!callIds.Add(call.Id) || callIds.Count > 128) throw new ProviderException("invalid_stream");
                                        callNames.Add(call.Id, call.Name);
                                        break;
                                    case ToolCallEnd call:
                                        if (!callNames.TryGetValue(call.Id, out var callName)) throw new ProviderException("invalid_stream");
                                        assistant.Add(new ToolCallPart(call.Id, callName, call.ParsedArgs, call.ProviderState));
                                        callNames.Remove(call.Id);
                                        break;
                                    case StreamError _: failed = true; break;
                                }
                                await Reply(send, id, "byok.event", EventView(streamEvent)).ConfigureAwait(false);
                            }, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!failed && assistant.Count > 0) { history.Add(userMessage); history.Add(new Message(MessageRole.Assistant, assistant, chatModel.ModelId)); }
                        await Reply(send, id, type + ".result", new { completed = !failed }).ConfigureAwait(false);
                    }
                    break;
                default: throw new ProviderException("invalid_message");
            }
        }
        catch (OperationCanceledException) { await Reply(send, id, "error", new { code = "cancelled" }).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ProviderException || exception is ArgumentException || exception is JsonException || exception is InvalidOperationException || exception is System.IO.IOException || exception is UnauthorizedAccessException || exception is System.ComponentModel.Win32Exception)
        { await Reply(send, id, "error", new { code = exception is ProviderException error ? error.Code : "invalid_message" }).ConfigureAwait(false); }
        finally
        {
            lock (activeGate) active = null;
            gate.Release();
        }
        return true;
    }

    private CatalogResult ApplyProbeCatalog(ProviderConfiguration configuration, CatalogResult catalog) =>
        new CatalogResult(catalog.Models.Select(model => ApplyProbe(configuration, model)).ToArray(), catalog.CredentialValid, catalog.ErrorCode, catalog.RetryAfter);

    private ModelInfo ApplyProbe(ProviderConfiguration configuration, ModelInfo model)
    {
        var caps = model.Capabilities;
        var origins = model.Origins.ToList();
        foreach (var capability in new[] { ModelCapabilities.Vision, ModelCapabilities.Tools })
        {
            var supported = probe.Store.Get(configuration.Id, model.ModelId, capability);
            var origin = origins.FirstOrDefault(value => value.Capability == capability);
            if (!supported.HasValue || (origin is not null && origin.Source != CapabilitySource.Heuristic && origin.Source != CapabilitySource.Probe)) continue;
            caps = supported.Value ? caps | capability : caps & ~capability;
            origins.RemoveAll(value => value.Capability == capability);
            origins.Add(new CapabilityOrigin(capability, CapabilitySource.Probe, Confidence.Medium));
        }
        return new ModelInfo(model.ProviderId, model.ModelId, caps, model.DisplayName, model.Reasoning, model.ContextWindowTokens, model.MaxOutputTokens, origins);
    }

    private static void RejectSecretMetadata(ProviderConfiguration configuration, IReadOnlyList<ModelInfo> catalog, string? secret)
    {
        if (catalog.Count > 8192) throw new ProviderException("response_too_large");
        if (catalog.Any(model => model.ModelId.Length > 512 || model.DisplayName?.Length > 512) || catalog.Select(model => model.ModelId).Distinct(StringComparer.Ordinal).Count() != catalog.Count)
            throw new ProviderException("invalid_response");
        if (string.IsNullOrEmpty(secret)) return;
        var values = new[] { configuration.ProviderId, configuration.BaseUrl, configuration.SelectedModelId }
            .Concat(configuration.DeploymentNames)
            .Concat(catalog.SelectMany(model => new[] { model.ModelId, model.DisplayName }));
        if (values.Any(value => value?.IndexOf(secret, StringComparison.Ordinal) >= 0)) throw new ProviderException("invalid_response");
    }

    private ProviderConfiguration Find(JsonElement payload)
    {
        if (!Guid.TryParseExact(RequiredString(payload, "configurationId", 36), "D", out var guid) || guid == Guid.Empty) throw new ProviderException("invalid_message");
        var id = guid.ToString("D");
        return configurations.FirstOrDefault(value => value.Id == id) ?? throw new ProviderException("provider_not_found");
    }
    private ModelInfo FindModel(ProviderConfiguration configuration, string modelId) => models.TryGetValue(configuration.Id, out var catalog) ? catalog.FirstOrDefault(value => value.ModelId == modelId) ?? throw new ProviderException("model_not_found") : throw new ProviderException("model_not_found");
    private static void AppendText(List<ContentPart> parts, string text)
    {
        if (parts.LastOrDefault() is TextPart previous) parts[parts.Count - 1] = new TextPart(previous.Text + text); else parts.Add(new TextPart(text));
    }
    private static object ConfigurationView(ProviderConfiguration configuration) => new { id = configuration.Id, providerId = configuration.ProviderId, kind = configuration.Kind.ToString(), baseUrl = configuration.BaseUrl, selectedModelId = configuration.SelectedModelId, deploymentNames = configuration.DeploymentNames };
    private static object CatalogView(ProviderConfiguration configuration, CatalogResult result) => new { configuration = ConfigurationView(configuration), models = result.Models.Select(ModelView).ToArray(), credentialValid = result.CredentialValid, code = result.ErrorCode };
    private static object ModelView(ModelInfo model) => new
    {
        id = model.ModelId, name = model.DisplayName ?? model.ModelId,
        vision = model.Has(ModelCapabilities.Vision), tools = model.Has(ModelCapabilities.Tools), reasoning = model.Has(ModelCapabilities.Reasoning),
        origins = model.Origins.Select(origin => new { capability = origin.Capability.ToString(), source = origin.Source.ToString(), confidence = origin.Confidence.ToString() }).ToArray(),
        efforts = model.Reasoning?.Efforts.Where(level => !model.Reasoning.Mandatory || level != ReasoningIntent.Off).Select(level => level.ToString()).ToArray() ?? Array.Empty<string>()
    };
    private static object EventView(StreamEvent value) => value switch
    {
        TextDelta delta => new { kind = "TextDelta", text = delta.Text },
        ReasoningDelta delta => new { kind = "ReasoningDelta", text = delta.Text },
        ToolCallStart call => new { kind = "ToolCallStart", id = call.Id, name = call.Name },
        ToolCallArgsDelta call => new { kind = "ToolCallArgsDelta", id = call.Id, chunk = call.Chunk },
        ToolCallEnd call => new { kind = "ToolCallEnd", id = call.Id, arguments = call.ParsedArgs },
        Usage usage => new { kind = "Usage", input = usage.InputTokens, output = usage.OutputTokens },
        Done done => new { kind = "Done", reason = done.FinishReason.ToString() },
        StreamError error => new { kind = "Error", code = error.Code },
        _ => throw new InvalidOperationException()
    };
    private static Task Reply(Func<string, Task> send, string? id, string type, object payload) => send(JsonWire.Serialize(new { version = 1, id, type, payload }));
    private static string RequiredString(JsonElement payload, string name, int maximum, bool allowEmpty = false)
    {
        var value = JsonWire.String(payload, name);
        if (value is null || value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value))) throw new ProviderException("invalid_message");
        return value;
    }
    private static void Require(JsonElement value, string[] required, string[]? optional = null) { if (!Fields(value, required, optional)) throw new ProviderException("invalid_message"); }
    private static bool Fields(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>();
        foreach (var property in value.EnumerateObject()) if (!seen.Add(property.Name) || (!required.Contains(property.Name) && !(optional?.Contains(property.Name) ?? false))) return false;
        return required.All(seen.Contains);
    }
    public void Dispose() { lock (activeGate) active?.Cancel(); }
}
