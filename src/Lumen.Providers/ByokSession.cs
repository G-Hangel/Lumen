using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Agents;
using Lumen.Core.Bridge;
using Lumen.Core.Domain;
using Lumen.Tools;

namespace Lumen.Providers;

public sealed class ByokSession : IDisposable
{
    private readonly ProviderFactory providers;
    private readonly ICredentialStore credentials;
    private readonly SettingsStore settings;
    private readonly CapabilityProbe probe;
    private readonly bool canProbe;
    private readonly IContextSource? contextSource;
    private readonly IReadToolExecutor? readTools;
    private readonly IWriteToolExecutor? writeTools;
    private readonly IShellToolExecutor? shellTools;
    private readonly ISkillSource? skills;
    private readonly AgentRegistry? agentRegistry;
    private readonly DelegationCoordinator? agentCoordinator;
    private readonly IAgentSource? agentSource;
    private readonly IDiagnosticLog? diagnosticLog;
    private readonly HttpClient http = new();
    private readonly Dictionary<string, ContextSnapshot> contexts = new Dictionary<string, ContextSnapshot>();
    private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
    private readonly List<ProviderConfiguration> configurations = new List<ProviderConfiguration>();
    private readonly Dictionary<string, IReadOnlyList<ModelInfo>> models = new Dictionary<string, IReadOnlyList<ModelInfo>>();
    private StoredConversation? conversation;
    private readonly object activeGate = new object();
    private CancellationTokenSource? active;
    private bool userCancelled;
    private bool loaded;
    private readonly HashSet<string> requestIds = new HashSet<string>();
    private readonly object approvalGate = new object();
    private readonly Dictionary<string, PendingApproval> approvals = new Dictionary<string, PendingApproval>();
    private Func<object, Task>? approvalEmit;
    public ByokSession(ProviderFactory providers, ICredentialStore credentials, SettingsStore settings, CapabilityHttpProbe? httpProbe = null, IContextSource? contextSource = null, IReadToolExecutor? readTools = null, IWriteToolExecutor? writeTools = null, IShellToolExecutor? shellTools = null, ISkillSource? skills = null, AgentRegistry? agentRegistry = null, DelegationCoordinator? agentCoordinator = null, IAgentSource? agentSource = null, IDiagnosticLog? diagnosticLog = null)
    {
        this.providers = providers; this.credentials = credentials; this.settings = settings; this.contextSource = contextSource; this.readTools = readTools; this.writeTools = writeTools; this.shellTools = shellTools; this.skills = skills; this.agentRegistry = agentRegistry; this.agentCoordinator = agentCoordinator; this.agentSource = agentSource; this.diagnosticLog = diagnosticLog;
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
            if (requestIds.Count >= 1024) requestLimitReached = type != "byok.cancel" && type != "byok.approval.respond";
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
            lock (activeGate) { userCancelled = true; active?.Cancel(); }
            await Reply(send, id, type + ".result", new { cancelled = true }).ConfigureAwait(false);
            return true;
        }
        if (type == "byok.approval.respond")
        {
            // Runs outside the request gate so a decision can reach a chat blocked on approval.
            try
            {
                Require(payload, new[] { "requestId", "decision" });
                if (!Guid.TryParseExact(RequiredString(payload, "requestId", 36), "D", out var approvalGuid) || approvalGuid == Guid.Empty) throw new ProviderException("invalid_message");
                var decision = RequiredString(payload, "decision", 16);
                if (decision != "deny" && decision != "allow_once" && decision != "allow_session") throw new ProviderException("invalid_message");
                PendingApproval? pending;
                lock (approvalGate) approvals.TryGetValue(approvalGuid.ToString("D"), out pending);
                if (pending is null) throw new ProviderException("invalid_message");
                if (decision == "allow_session" && pending.Category != ToolCategory.ExecuteCommand) throw new ProviderException("invalid_message");
                if (!pending.Decision.TrySetResult(decision)) throw new ProviderException("invalid_message");
                await Reply(send, id, type + ".result", new { accepted = true }).ConfigureAwait(false);
            }
            catch (ProviderException exception) { await Reply(send, id, "error", new { code = exception.Code }).ConfigureAwait(false); }
            return true;
        }
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { await Reply(send, id, "error", new { code = "bridge_busy" }).ConfigureAwait(false); return true; }
        var callerToken = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        cancellationToken = deadline.Token;
        lock (activeGate) { userCancelled = false; active = deadline; }
        try
        {
            if (type == "byok.tool.read")
            {
                Require(payload, new[] { "name", "arguments" });
                var toolName = RequiredString(payload, "name", 64);
                if (!new[] { "read_file", "list_dir", "find_files", "grep", "get_edit_snapshot" }.Contains(toolName)) throw new ProviderException("unknown_tool");
                var arguments = JsonWire.Get(payload, "arguments");
                if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 8192) throw new ProviderException("invalid_arguments");
                if (readTools is null) throw new ProviderException("path_unavailable");
                var invocation = new ToolInvocation(id!, toolName, arguments.Clone());
                var result = await readTools.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.InvocationId != invocation.Id || result.Content.Length > 65536) throw new ProviderException("invalid_response");
                await Reply(send, id, type + ".result", new { name = toolName, isError = result.IsError, content = result.Content }).ConfigureAwait(false);
                return true;
            }
            if (type == "byok.tool.write")
            {
                Require(payload, new[] { "name", "arguments" });
                var toolName = RequiredString(payload, "name", 64);
                if (!new[] { "create_file", "edit_file" }.Contains(toolName)) throw new ProviderException("unknown_tool");
                var arguments = JsonWire.Get(payload, "arguments");
                Require(arguments, new[] { "path", "content" });
                var writePath = RequiredString(arguments, "path", 1024);
                var writeContent = RequiredString(arguments, "content", 65536, true);
                ValidateWriteText(writePath, false);
                ValidateWriteText(writeContent, true);
                if (writePath[0] == '/' || writePath[0] == '\\' || writePath.Contains(":") || writePath.Replace('\\', '/').Split('/').Contains("..")) throw new ProviderException("invalid_path");
                if (writeTools is null) throw new ProviderException("path_unavailable");
                var capturedWriter = await writeTools.CaptureAsync(cancellationToken).ConfigureAwait(false);
                await capturedWriter.BeginTurnAsync(false, cancellationToken).ConfigureAwait(false);
                var invocation = new ToolInvocation(id!, toolName, arguments.Clone());
                var result = await capturedWriter.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
                if (result.InvocationId != invocation.Id || result.Content.Length > 65536) throw new ProviderException("invalid_response");
                await Reply(send, id, type + ".result", new { name = toolName, isError = result.IsError, content = result.Content, pending = writeTools.PendingChanges }).ConfigureAwait(false);
                return true;
            }
            if (type == "byok.tool.write.undo")
            {
                if (!Fields(payload, Array.Empty<string>())) { await Reply(send, id, "error", new { code = "invalid_message" }).ConfigureAwait(false); return true; }
                if (writeTools is null) throw new ProviderException("path_unavailable");
                await writeTools.UndoTurnAsync(cancellationToken).ConfigureAwait(false);
                await Reply(send, id, type + ".result", new { undone = true, pending = writeTools.PendingChanges }).ConfigureAwait(false);
                return true;
            }
            if (type == "byok.tool.write.status")
            {
                Require(payload, Array.Empty<string>());
                await Reply(send, id, type + ".result", new { pending = writeTools?.PendingChanges ?? 0 }).ConfigureAwait(false);
                return true;
            }
            if (type == "byok.tool.command")
            {
                Require(payload, new[] { "name", "arguments" });
                var toolName = RequiredString(payload, "name", 64);
                if (!new[] { "run_command" }.Contains(toolName)) throw new ProviderException("unknown_tool");
                var arguments = JsonWire.Get(payload, "arguments");
                if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 8192) throw new ProviderException("invalid_arguments");
                if (shellTools is null) throw new ProviderException("path_unavailable");
                var invocation = new ToolInvocation(id!, toolName, arguments.Clone());
                var result = await shellTools.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.InvocationId != invocation.Id || result.Content.Length > 65536) throw new ProviderException("invalid_response");
                await Reply(send, id, type + ".result", new { name = toolName, isError = result.IsError, content = result.Content }).ConfigureAwait(false);
                return true;
            }
            if (!loaded)
            {
                var savedConfigurations = await settings.ReadAsync(cancellationToken).ConfigureAwait(false);
                await settings.ReadProbeCacheAsync(probe.Store, cancellationToken).ConfigureAwait(false);
                configurations.AddRange(savedConfigurations);
                loaded = true;
            }
            switch (type)
            {
                case "byok.skills.list":
                {
                    Require(payload, Array.Empty<string>());
                    var skillList = skills is null ? SkillSnapshot.Empty : await skills.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                    var disabled = new HashSet<string>(await settings.ReadDisabledSkillsAsync(cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
                    await Reply(send, id, type + ".result", new
                    {
                        skills = skillList.Skills.Select(skill => new { name = skill.Name, description = skill.Description, source = skill.Source, scope = skill.Scope.ToString(), modes = skill.Modes.Select(mode => mode.ToString()).ToArray(), enabled = !disabled.Contains(skill.Name) }).ToArray(),
                        diagnostics = skillList.Diagnostics.Take(256).Select(diagnostic => new { location = diagnostic.Location, code = diagnostic.Code, detail = diagnostic.Detail }).ToArray()
                    }).ConfigureAwait(false);
                    break;
                }
                case "byok.skills.set":
                {
                    Require(payload, new[] { "name", "enabled" });
                    var name = RequiredString(payload, "name", SkillParser.MaxNameLength);
                    var enabled = JsonWire.Get(payload, "enabled");
                    if (enabled.ValueKind != JsonValueKind.True && enabled.ValueKind != JsonValueKind.False) throw new ProviderException("invalid_message");
                    var currentSkills = skills is null ? SkillSnapshot.Empty : await skills.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                    if (!currentSkills.Skills.Any(skill => skill.Name == name)) throw new ProviderException("skill_unavailable");
                    var disabled = new HashSet<string>(await settings.ReadDisabledSkillsAsync(cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
                    if (enabled.ValueKind == JsonValueKind.True) disabled.Remove(name);
                    else if (!disabled.Contains(name) && disabled.Count >= SkillDiscovery.MaxSkills) throw new ProviderException("invalid_configuration");
                    else disabled.Add(name);
                    await settings.WriteDisabledSkillsAsync(disabled, cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new { name, enabled = enabled.GetBoolean() }).ConfigureAwait(false);
                    break;
                }
                case "byok.skills.write":
                {
                    Require(payload, new[] { "name", "description", "body", "edit" });
                    if (skills is not ISkillEditor editor) throw new ProviderException("unsupported_type");
                    var name = RequiredString(payload, "name", SkillParser.MaxNameLength);
                    var description = RequiredString(payload, "description", SkillParser.MaxDescriptionLength);
                    var body = RequiredString(payload, "body", SkillParser.MaxFileChars);
                    var path = await editor.WriteAsync(name, description, body, JsonWire.Get(payload, "edit").GetBoolean(), cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new { path }).ConfigureAwait(false);
                    break;
                }
                case "byok.skills.undo":
                {
                    Require(payload, Array.Empty<string>());
                    if (skills is not ISkillEditor editor) throw new ProviderException("unsupported_type");
                    await editor.UndoAsync(cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new { undone = true }).ConfigureAwait(false);
                    break;
                }
                case "byok.skills.open":
                {
                    Require(payload, Array.Empty<string>());
                    if (skills is not ISkillEditor editor) throw new ProviderException("unsupported_type");
                    var path = await editor.OpenFolderAsync(cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new { path }).ConfigureAwait(false);
                    break;
                }
                case "byok.log.get":
                {
                    Require(payload, Array.Empty<string>());
                    if (diagnosticLog is null) throw new ProviderException("unsupported_type");
                    await Reply(send, id, type + ".result", new { level = diagnosticLog.Level }).ConfigureAwait(false);
                    break;
                }
                case "byok.log.set":
                {
                    Require(payload, new[] { "level" });
                    if (diagnosticLog is null) throw new ProviderException("unsupported_type");
                    var level = RequiredString(payload, "level", 16);
                    if (!DiagnosticLevels.IsValid(level)) throw new ProviderException("invalid_configuration");
                    await settings.WriteLogLevelAsync(level, cancellationToken).ConfigureAwait(false);
                    diagnosticLog.SetLevel(level);
                    await Reply(send, id, type + ".result", new { level }).ConfigureAwait(false);
                    break;
                }
                case "byok.log.open":
                {
                    Require(payload, Array.Empty<string>());
                    if (diagnosticLog is null) throw new ProviderException("unsupported_type");
                    diagnosticLog.Open();
                    await Reply(send, id, type + ".result", new { opened = true }).ConfigureAwait(false);
                    break;
                }
                case "byok.azure.arm.list":
                {
                    Require(payload, new[] { "configurationId", "tenantId", "clientId", "subscriptionId", "resourceGroup", "accountName" });
                    var armOwner = Find(payload);
                    if (armOwner.Kind != ProviderKind.Azure) throw new ProviderException("invalid_configuration");
                    var tenantId = RequiredString(payload, "tenantId", 36);
                    var clientId = RequiredString(payload, "clientId", 36);
                    AzureArmConfiguration arm;
                    try
                    {
                        arm = new AzureArmConfiguration(
                            RequiredString(payload, "subscriptionId", 36),
                            RequiredString(payload, "resourceGroup", 90),
                            RequiredString(payload, "accountName", 64));
                    }
                    catch (ArgumentException) { throw new ProviderException("invalid_configuration"); }
                    armOwner.Arm = arm;
                    var deploymentNames = await AzureArmInventory.ListDeploymentsAsync(arm, tenantId, clientId, async code =>
                    {
                        await Reply(send, id, "byok.event", new
                        {
                            kind = "Notice", code = "azure_device_code", message = code.Message,
                            verificationUri = code.VerificationUrl, expiresIn = code.ExpiresOn - DateTimeOffset.UtcNow
                        }).ConfigureAwait(false);
                    }, http, cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new { deployments = deploymentNames, arm = new { arm.SubscriptionId, arm.ResourceGroup, arm.AccountName } }).ConfigureAwait(false);
                    break;
                }
                case "byok.agents.delegate":
                {
                    Require(payload, new[] { "name", "task", "mode" });
                    if (RequiredString(payload, "mode", 8) != "Agent") throw new ProviderException("mode_denied");
                    var name = RequiredString(payload, "name", AgentRegistry.MaxNameLength);
                    var task = RequiredString(payload, "task", DelegationCoordinator.MaxTaskChars);
                    if (agentRegistry is null || agentCoordinator is null) throw new ProviderException("agent_unavailable");
                    await RefreshAgentsAsync(cancellationToken).ConfigureAwait(false);
                    var delegationResult = await agentCoordinator.DelegateAsync(name, task, ChatMode.Agent, 0, cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new
                    {
                        name = delegationResult.AgentName, success = delegationResult.Success, text = delegationResult.Text, generatedAtUtc = delegationResult.GeneratedAt
                    }).ConfigureAwait(false);
                    break;
                }
                case "byok.agents.list":
                {
                    Require(payload, Array.Empty<string>());
                    var agentSnapshot = await RefreshAgentsAsync(cancellationToken).ConfigureAwait(false);
                    var agents = agentSnapshot.Agents.Select(agent => (object)new
                    {
                        name = agent.Name, description = agent.Description, source = agent.Source, scope = agent.Scope.ToString()
                    }).ToArray();
                    var diagnostics = agentSnapshot.Diagnostics.Select(item => new { location = item.Location, code = item.Code, detail = item.Detail }).ToArray();
                    await Reply(send, id, type + ".result", new { agents, diagnostics }).ConfigureAwait(false);
                    break;
                }
                case "byok.theme.get":
                {
                    Require(payload, Array.Empty<string>());
                    var theme = await settings.ReadThemeAsync(cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new
                    {
                        theme = theme is null ? null : ThemeView(theme),
                        warnings = theme is null ? Array.Empty<string>() : ThemeCatalog.Warnings(theme)
                    }).ConfigureAwait(false);
                    break;
                }
                case "byok.theme.set":
                {
                    Require(payload, new[] { "theme" });
                    var themeJson = JsonWire.Get(payload, "theme");
                    if (themeJson.ValueKind != JsonValueKind.Object) throw new ProviderException("invalid_configuration");
                    var validated = settings.ValidateThemeJson(themeJson);
                    await settings.WriteThemeAsync(validated, cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new
                    {
                        theme = ThemeView(validated),
                        warnings = ThemeCatalog.Warnings(validated)
                    }).ConfigureAwait(false);
                    break;
                }
                case "byok.theme.reset":
                {
                    Require(payload, Array.Empty<string>());
                    await settings.ClearThemeAsync(cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new { reset = true }).ConfigureAwait(false);
                    break;
                }
                case "byok.context.capture":
                    Require(payload, new[] { "kind" }, new[] { "path" });
                    var contextKind = RequiredString(payload, "kind", 16);
                    if (!new[] { "file", "solution", "selection", "output" }.Contains(contextKind)) throw new ProviderException("invalid_message");
                    var contextPath = JsonWire.Get(payload, "path").ValueKind == JsonValueKind.Undefined ? null : RequiredString(payload, "path", 1024, true);
                    if (contextKind != "file" && contextPath is not null) throw new ProviderException("invalid_message");
                    if (contextSource is null) throw new ProviderException("context_unavailable");
                    if (contexts.Count >= 8) throw new ProviderException("context_too_large");
                    var snapshot = await contextSource.CaptureAsync(contextKind, contextPath, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (contexts.Values.Sum(value => value.Text.Length) + snapshot.Text.Length > 131072) throw new ProviderException("context_too_large");
                    var contextId = Guid.NewGuid().ToString("D");
                    contexts.Add(contextId, snapshot);
                    await Reply(send, id, type + ".result", new { context = new { id = contextId, kind = snapshot.Kind, label = snapshot.Label, text = snapshot.Text } }).ConfigureAwait(false);
                    break;
                case "byok.context.release":
                    Require(payload, new[] { "ids" });
                    foreach (var releasedContext in ContextIds(payload, "ids")) contexts.Remove(releasedContext);
                    await Reply(send, id, type + ".result", new { released = true }).ConfigureAwait(false);
                    break;
                case "byok.history.list":
                    Require(payload, new[] { "query" });
                    var saved = await settings.Conversations.ListAsync(RequiredString(payload, "query", 256, true), cancellationToken).ConfigureAwait(false);
                    await Reply(send, id, type + ".result", new { conversations = saved.Take(256).Select(ConversationView).ToArray() }).ConfigureAwait(false);
                    break;
                case "byok.history.open":
                    Require(payload, new[] { "conversationId", "offset", "limit" });
                    var offset = JsonWire.Int(payload, "offset");
                    var limit = JsonWire.Int(payload, "limit");
                    if (!offset.HasValue || offset < 0 || !limit.HasValue || limit < 1 || limit > 10) throw new ProviderException("invalid_message");
                    var opened = await settings.Conversations.ReadAsync(RequiredString(payload, "conversationId", 36), cancellationToken).ConfigureAwait(false);
                    if (offset > opened.TurnStates.Count) throw new ProviderException("invalid_message");
                    var page = new { conversation = ConversationView(opened), offset, total = opened.TurnStates.Count,
                        turns = Enumerable.Range(offset.Value, Math.Min(limit.Value, opened.TurnStates.Count - offset.Value)).Select(index => TurnView(opened, index)).ToArray() };
                    if (JsonWire.Serialize(page).Length > 4 * 1024 * 1024) throw new ProviderException("history_too_large");
                    conversation = opened;
                    await Reply(send, id, type + ".result", page).ConfigureAwait(false);
                    break;
                case "byok.history.new":
                    Require(payload, Array.Empty<string>());
                    conversation = null;
                    await Reply(send, id, type + ".result", new { created = true }).ConfigureAwait(false);
                    break;
                case "byok.history.rename":
                    Require(payload, new[] { "conversationId", "title" });
                    var renamed = await settings.Conversations.RenameAsync(RequiredString(payload, "conversationId", 36), RequiredString(payload, "title", 160), cancellationToken).ConfigureAwait(false);
                    if (conversation?.Conversation.Id == renamed.Conversation.Id) conversation = renamed;
                    await Reply(send, id, type + ".result", new { conversation = ConversationView(renamed) }).ConfigureAwait(false);
                    break;
                case "byok.history.delete":
                    Require(payload, new[] { "conversationId", "confirm" });
                    if (JsonWire.Get(payload, "confirm").ValueKind != JsonValueKind.True) throw new ProviderException("invalid_message");
                    var deletedId = RequiredString(payload, "conversationId", 36);
                    await settings.Conversations.DeleteAsync(deletedId, cancellationToken).ConfigureAwait(false);
                    if (conversation?.Conversation.Id == deletedId) conversation = null;
                    await Reply(send, id, type + ".result", new { deleted = true }).ConfigureAwait(false);
                    break;
                case "byok.list":
                    Require(payload, Array.Empty<string>());
                    await Reply(send, id, type + ".result", new { configurations = configurations.Select(ConfigurationView).ToArray(), remainingBudget = probe.RemainingBudget }).ConfigureAwait(false);
                    break;
                case "byok.configure":
                    Require(payload, new[] { "id", "providerId", "kind", "baseUrl", "key" }, new[] { "deploymentNames", "arm" });
                    var kindText = RequiredString(payload, "kind", 32);
                    if (!Enum.TryParse<ProviderKind>(kindText, out var kind) || !Enum.IsDefined(typeof(ProviderKind), kind)) throw new ProviderException("invalid_message");
                    var names = JsonWire.Array(JsonWire.Get(payload, "deploymentNames")).Select(value => value.GetString()!).ToArray();
                    var baseUrl = RequiredString(payload, "baseUrl", 2048);
                    HttpTransport.ValidateBaseUrl(baseUrl);
                    var configuration = new ProviderConfiguration(RequiredString(payload, "id", 36), RequiredString(payload, "providerId", 64), kind, baseUrl, null, names);
                    var secret = RequiredString(payload, "key", 4096, true);
                    if (JsonWire.Get(payload, "arm").ValueKind == JsonValueKind.Object)
                    {
                        var arm = JsonWire.Get(payload, "arm");
                        if (arm.TryGetProperty("subscriptionId", out var sub) && arm.TryGetProperty("resourceGroup", out var rg) && arm.TryGetProperty("accountName", out var acc))
                        {
                            try { configuration.Arm = new AzureArmConfiguration(sub.GetString()!, rg.GetString()!, acc.GetString()!); }
                            catch (ArgumentException) { throw new ProviderException("invalid_configuration"); }
                        }
                    }
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
                    if (conversation?.ConfigurationId == removed.Id) conversation = null;
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
                    if (conversation is not null && (conversation.ConfigurationId != selected.Id || conversation.Conversation.ModelId != modelId)) conversation = null;
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
                {
                    Require(payload, new[] { "configurationId", "text", "effort" }, new[] { "image", "contextIds", "readTools", "writeTools", "terminal", "mode", "budget", "skills" });
                    var chatConfiguration = Find(payload);
                    var chatModel = FindModel(chatConfiguration, chatConfiguration.SelectedModelId ?? "");
                    var readToolsValue = JsonWire.Get(payload, "readTools");
                    var writeToolsValue = JsonWire.Get(payload, "writeTools");
                    var terminalValue = JsonWire.Get(payload, "terminal");
                    if ((readToolsValue.ValueKind != JsonValueKind.Undefined && readToolsValue.ValueKind != JsonValueKind.True && readToolsValue.ValueKind != JsonValueKind.False) ||
                        (writeToolsValue.ValueKind != JsonValueKind.Undefined && writeToolsValue.ValueKind != JsonValueKind.True && writeToolsValue.ValueKind != JsonValueKind.False) ||
                        (terminalValue.ValueKind != JsonValueKind.Undefined && terminalValue.ValueKind != JsonValueKind.True && terminalValue.ValueKind != JsonValueKind.False)) throw new ProviderException("invalid_message");
                    var modeValue = JsonWire.Get(payload, "mode");
                    var mode = ChatMode.Ask;
                    if (modeValue.ValueKind != JsonValueKind.Undefined)
                    {
                        if (modeValue.ValueKind != JsonValueKind.String) throw new ProviderException("invalid_message");
                        mode = modeValue.GetString() switch
                        {
                            "Ask" => ChatMode.Ask,
                            "Plan" => ChatMode.Plan,
                            "Agent" => ChatMode.Agent,
                            _ => throw new ProviderException("invalid_message")
                        };
                    }
                    var budgetValue = JsonWire.Get(payload, "budget");
                    ToolBudget? budget = null;
                    if (budgetValue.ValueKind != JsonValueKind.Undefined)
                    {
                        Require(budgetValue, new[] { "maxRounds", "maxOutputTokens", "maxInvocations" });
                        budget = new ToolBudget(
                            JsonWire.Int(budgetValue, "maxRounds") ?? throw new ProviderException("invalid_message"),
                            JsonWire.Int(budgetValue, "maxOutputTokens") ?? throw new ProviderException("invalid_message"),
                            JsonWire.Int(budgetValue, "maxInvocations") ?? throw new ProviderException("invalid_message"));
                    }
                    var enableWriteTools = writeToolsValue.ValueKind == JsonValueKind.True;
                    var enableTerminal = terminalValue.ValueKind == JsonValueKind.True;
                    if (mode != ChatMode.Agent && (enableWriteTools || enableTerminal)) throw new ProviderException("mode_denied");
                    var enableReadTools = readToolsValue.ValueKind == JsonValueKind.True || enableWriteTools || enableTerminal;
                    if (enableReadTools && !chatModel.Has(ModelCapabilities.Tools)) throw new ProviderException("tools_not_supported");
                    if (enableReadTools && readTools is null) throw new ProviderException("workspace_unavailable");
                    if (enableWriteTools && writeTools is null) throw new ProviderException("workspace_unavailable");
                    if (enableTerminal && shellTools is null) throw new ProviderException("workspace_unavailable");
                    var capturedRead = enableReadTools ? await readTools!.CaptureAsync(cancellationToken).ConfigureAwait(false) : null;
                    cancellationToken.ThrowIfCancellationRequested();
                    var capturedWrite = enableWriteTools ? await writeTools!.CaptureAsync(cancellationToken).ConfigureAwait(false) : null;
                    cancellationToken.ThrowIfCancellationRequested();
                    var capturedShell = enableTerminal ? await shellTools!.CaptureAsync(cancellationToken).ConfigureAwait(false) : null;
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadToolExecutor? capturedTools = capturedRead is null ? null : mode == ChatMode.Agent ? new CompositeExecutor(capturedRead, capturedWrite, capturedShell) : capturedRead;
                    var skillSnapshot = skills is null ? SkillSnapshot.Empty : await skills.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var disabledSkills = new HashSet<string>(await settings.ReadDisabledSkillsAsync(cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
                    var availableSkills = SkillCatalog.Available(skillSnapshot, mode, disabledSkills);
                    var skillCatalog = string.Empty;
                    if (capturedTools is not null && availableSkills.Count > 0)
                    {
                        capturedTools = new SkillToolExecutor(capturedTools, availableSkills);
                        skillCatalog = SkillCatalog.PromptSection(availableSkills);
                    }
                    if (capturedTools is not null && mode == ChatMode.Agent && agentRegistry is not null && agentCoordinator is not null)
                        await RefreshAgentsAsync(cancellationToken).ConfigureAwait(false);
                    if (capturedTools is not null && mode == ChatMode.Agent && agentRegistry is not null && agentCoordinator is not null && agentRegistry.Count > 0)
                        capturedTools = new AgentToolExecutor(capturedTools, agentRegistry, agentCoordinator);
                    var toolSpecs = capturedTools is null ? Array.Empty<ToolSpec>() : ToolChatLoop.SpecsFor(mode, capturedTools);
                    if (!Enum.TryParse<ReasoningIntent>(RequiredString(payload, "effort", 16), out var effort) || !Enum.IsDefined(typeof(ReasoningIntent), effort)) throw new ProviderException("invalid_message");
                    var text = RequiredString(payload, "text", 4096);
                    var parts = new List<ContentPart> { new TextPart(text) };
                    var selectedContexts = JsonWire.Get(payload, "contextIds").ValueKind == JsonValueKind.Undefined ? Array.Empty<string>() : ContextIds(payload, "contextIds");
                    var attachedContexts = new List<ContextSnapshot>();
                    foreach (var selectedContext in selectedContexts)
                    {
                        if (contextSource is null || !contexts.TryGetValue(selectedContext, out var captured)) throw new ProviderException("context_stale");
                        await contextSource.ValidateAsync(captured, cancellationToken).ConfigureAwait(false);
                        attachedContexts.Add(captured);
                    }
                    if (attachedContexts.Count > 0) parts.Add(new TextPart(string.Join("\n\n", attachedContexts.Select(value => "[Context: " + value.Label + "]\n" + value.Text))));
                    foreach (var skill in ActivatedSkills(payload, text, skillSnapshot, availableSkills)) parts.Add(new TextPart(SkillCatalog.Activation(skill)));
                    var image = JsonWire.Get(payload, "image");
                    if (image.ValueKind != JsonValueKind.Undefined)
                    {
                        Require(image, new[] { "mediaType", "base64Data" });
                        parts.Add(new ImagePart(RequiredString(image, "mediaType", 32), RequiredString(image, "base64Data", 6 * 1024 * 1024)));
                    }
                    if (conversation is not null && (conversation.ConfigurationId != chatConfiguration.Id || conversation.Conversation.ModelId != chatModel.ModelId)) conversation = null;
                    var userMessage = new Message(MessageRole.User, parts);
                    var titleText = text.Trim();
                    var titleLength = Math.Min(80, titleText.Length);
                    if (char.IsHighSurrogate(titleText[titleLength - 1])) titleLength--;
                    var currentConversation = conversation ?? new StoredConversation(chatConfiguration.Id,
                        new Conversation(Guid.NewGuid().ToString("D"), mode, Array.Empty<Message>(), titleText.Substring(0, titleLength), chatModel.ModelId, effort), Array.Empty<string>());
                    if (currentConversation.TurnStates.Count >= 10000) throw new ProviderException("history_too_large");
                    var replay = ToolChatLoop.Replay(currentConversation);
                    var outputTokens = Math.Min(chatModel.MaxOutputTokens ?? int.MaxValue, chatModel.Reasoning?.Kind == ReasoningKind.BudgetTokens && effort != ReasoningIntent.Off ? 4096 : 1024);
                    var pending = replay.Concat(new[] { userMessage }).ToArray();
                    var input = ToolChatLoop.Compact(pending, toolSpecs, chatModel, outputTokens);
                    ToolChatLoop.ValidateInput(input, toolSpecs, chatModel, outputTokens);
                    var chatSecret = await credentials.ReadAsync(chatConfiguration.Id, cancellationToken).ConfigureAwait(false);
                    ToolChatLoop.RejectSecret(JsonSerializer.SerializeToElement(input, ConversationSerializer.Options), chatSecret);
                    if (capturedWrite is not null) await capturedWrite.BeginTurnAsync(true, cancellationToken).ConfigureAwait(false);
                    var loop = new ToolChatLoop();
                    var turnState = "failed";
                    var eventSequence = 0;
                    using var eventGate = new SemaphoreSlim(1, 1);
                    async Task EmitChatEvent(object eventPayload)
                    {
                        await eventGate.WaitAsync().ConfigureAwait(false);
                        try { await send(JsonWire.Serialize(new { version = 1, id, type = "byok.event", sequence = ++eventSequence, payload = eventPayload })).ConfigureAwait(false); }
                        finally { eventGate.Release(); }
                    }
                    lock (approvalGate) approvalEmit = EmitChatEvent;
                    if (!ReferenceEquals(input, pending)) await EmitChatEvent(new { kind = "Notice", code = "history_compacted" }).ConfigureAwait(false);
                    try
                    {
                        var finished = await loop.RunAsync(providers.Chat(chatConfiguration.Kind), chatConfiguration, chatModel, input, effort, mode, capturedTools, toolSpecs, chatSecret,
                            streamEvent => EmitChatEvent(EventView(streamEvent)),
                            EmitChatEvent, cancellationToken, budget, skillCatalog: skillCatalog).ConfigureAwait(false);
                        turnState = loop.Paused ? "paused" : finished ? "completed" : "failed";
                        if (loop.Paused) await EmitChatEvent(new { kind = "Notice", code = "tool_budget_paused" }).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (callerToken.IsCancellationRequested || userCancelled) { turnState = "cancelled"; throw; }
                        turnState = "paused";
                        await EmitChatEvent(new { kind = "Notice", code = "tool_budget_paused" }).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (loop.UnsafeResponse) throw new ProviderException("invalid_response");
                        var assistant = loop.Parts.ToList();
                        if (turnState == "paused") assistant = Resumable(assistant);
                        if (assistant.Count == 0) assistant.Add(new TextPart(string.Empty));
                        var domain = currentConversation.Conversation;
                        var updated = new Conversation(domain.Id, mode,
                            domain.Messages.Concat(new[] { userMessage, new Message(MessageRole.Assistant, assistant, chatModel.ModelId) }).ToArray(),
                            domain.Title, chatModel.ModelId, effort, domain.CreatedAtUtc, DateTimeOffset.UtcNow);
                        if (!string.IsNullOrEmpty(chatSecret))
                        {
                            using var archiveJson = JsonDocument.Parse(ConversationSerializer.ToJson(updated));
                            if (ContainsSecret(archiveJson.RootElement, chatSecret!)) throw new ProviderException("invalid_response");
                        }
                        var stored = new StoredConversation(chatConfiguration.Id, updated, currentConversation.TurnStates.Concat(new[] { turnState }).ToArray());
                        try { await settings.Conversations.SaveAsync(stored, CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is System.IO.IOException || exception is UnauthorizedAccessException) { throw new ProviderException("history_write_failed"); }
                        conversation = stored;
                    }
                    foreach (var selectedContext in selectedContexts) contexts.Remove(selectedContext);
                    await Reply(send, id, type + ".result", new { completed = turnState == "completed", paused = turnState == "paused", conversation = ConversationView(conversation!) }).ConfigureAwait(false);
                    break;
                }
                default: throw new ProviderException("invalid_message");
            }
        }
        catch (OperationCanceledException) { await Reply(send, id, "error", new { code = "cancelled" }).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ProviderException || exception is ArgumentException || exception is JsonException || exception is InvalidOperationException || exception is System.IO.IOException || exception is UnauthorizedAccessException || exception is System.ComponentModel.Win32Exception)
        { await Reply(send, id, "error", new { code = exception is ProviderException error ? error.Code : "invalid_message" }).ConfigureAwait(false); }
        finally
        {
            lock (approvalGate)
            {
                approvalEmit = null;
                foreach (var pending in approvals.Values) pending.Decision.TrySetCanceled();
                approvals.Clear();
            }
            lock (activeGate) active = null;
            gate.Release();
        }
        return true;
    }

    public async Task<ApprovalResponse> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Func<object, Task> emit;
        var requestId = Guid.NewGuid().ToString("D");
        PendingApproval pending;
        lock (approvalGate)
        {
            if (approvalEmit is null || approvals.Count >= 8) return ApprovalResponse.Deny;
            emit = approvalEmit;
            pending = new PendingApproval(request.Category, cancellationToken);
            approvals.Add(requestId, pending);
        }
        try
        {
            await emit(new
            {
                kind = "ApprovalRequest", id = requestId, toolName = request.ToolName, category = request.Category.ToString(),
                arguments = request.Arguments, targetPath = request.TargetPath, canAllowSession = request.CanAllowCommandsForSession
            }).ConfigureAwait(false);
            var decision = await pending.Decision.Task.ConfigureAwait(false);
            return decision == "allow_session" && request.CanAllowCommandsForSession ? ApprovalResponse.AllowCommandsForSession
                : decision == "allow_once" ? ApprovalResponse.AllowOnce : ApprovalResponse.Deny;
        }
        catch (OperationCanceledException) { return ApprovalResponse.Deny; }
        finally
        {
            lock (approvalGate) approvals.Remove(requestId);
            pending.Complete();
        }
    }

    private sealed class PendingApproval
    {
        internal readonly TaskCompletionSource<string> Decision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ToolCategory Category;
        private readonly CancellationTokenRegistration registration;
        internal PendingApproval(ToolCategory category, CancellationToken token)
        {
            Category = category;
            registration = token.Register(state => ((TaskCompletionSource<string>)state!).TrySetCanceled(), Decision);
        }
        internal void Complete() => registration.Dispose();
    }

    private sealed class CompositeExecutor : IReadToolExecutor
    {
        private readonly IReadToolExecutor read;
        private readonly IWriteToolExecutor? write;
        private readonly IShellToolExecutor? shell;
        internal CompositeExecutor(IReadToolExecutor read, IWriteToolExecutor? write, IShellToolExecutor? shell) { this.read = read; this.write = write; this.shell = shell; }
        public IReadOnlyList<ToolSpec> Specs => read.Specs.Concat(write?.Specs ?? Array.Empty<ToolSpec>()).Concat(shell?.Specs ?? Array.Empty<ToolSpec>()).ToArray();
        public Task<IReadToolExecutor> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult<IReadToolExecutor>(this);
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            if (invocation.Name == "create_file" || invocation.Name == "edit_file" || invocation.Name == "apply_diff")
                return write is null ? throw new ProviderException("workspace_unavailable") : write.ExecuteAsync(invocation, cancellationToken);
            if (invocation.Name == "run_command" || invocation.Name == "run_build" || invocation.Name == "run_tests")
                return shell is null ? throw new ProviderException("workspace_unavailable") : shell.ExecuteAsync(invocation, cancellationToken);
            return read.ExecuteAsync(invocation, cancellationToken);
        }
    }

    // Explicit activation only names discovered skills, so ordinary text such as "/usr" stays plain text.
    private static IReadOnlyList<SkillDefinition> ActivatedSkills(JsonElement payload, string text, SkillSnapshot snapshot, IReadOnlyList<SkillDefinition> available)
    {
        var names = new List<string>();
        var typed = SkillCatalog.ExplicitName(text);
        if (typed is not null && snapshot.Skills.Any(skill => skill.Name == typed)) names.Add(typed);
        var selected = JsonWire.Get(payload, "skills");
        if (selected.ValueKind != JsonValueKind.Undefined)
        {
            if (selected.ValueKind != JsonValueKind.Array || selected.GetArrayLength() > 8) throw new ProviderException("invalid_message");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in selected.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString()!.Length > SkillParser.MaxNameLength || !seen.Add(item.GetString()!)) throw new ProviderException("invalid_message");
                if (!names.Contains(item.GetString()!)) names.Add(item.GetString()!);
            }
        }
        var activated = names.Select(name => available.FirstOrDefault(skill => skill.Name == name) ?? throw new ProviderException("skill_unavailable")).ToList();
        // Automatic activation never throws and never displaces explicit or manual picks: extras past 8 are skipped.
        foreach (var skill in SkillCatalog.AutoActivate(text, available.Where(value => !names.Contains(value.Name)).ToArray()))
        {
            if (activated.Count >= 8) break;
            activated.Add(skill);
        }
        return activated;
    }

    private async Task<AgentSnapshot> RefreshAgentsAsync(CancellationToken cancellationToken)
    {
        if (agentSource is null) return AgentSnapshot.Empty;
        var snapshot = await agentSource.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (agentRegistry is not null)
        {
            var agents = snapshot.Agents.Select(agent =>
            {
                if (agent.HasModelConfig)
                    return (AgentBase)new ModelAgent(agent, providers, credentials);
                return (AgentBase)new MarkdownAgent(agent);
            });
            agentRegistry.Replace(agents);
        }
        return snapshot;
    }

    private static object ThemeView(ThemePreset theme) => new
    {
        name = theme.Name,
        background = theme.Background,
        foreground = theme.Foreground,
        accent = theme.Accent,
        chatFont = theme.ChatFont,
        codeFont = theme.CodeFont,
        fontSize = theme.FontSize,
        density = theme.Density,
        borderRadius = theme.BorderRadius,
        modeAccent = theme.ModeAccent
    };

    private static bool ContainsSecret(JsonElement value, string secret) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.IndexOf(secret, StringComparison.Ordinal) >= 0,
        JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsSecret(item, secret)),
        JsonValueKind.Object => value.EnumerateObject().Any(property => property.Name.IndexOf(secret, StringComparison.Ordinal) >= 0 || ContainsSecret(property.Value, secret)),
        _ => false
    };

    private static object ConversationView(StoredConversation value) => new
    {
        id = value.Conversation.Id, title = value.Conversation.Title ?? string.Empty, configurationId = value.ConfigurationId,
        modelId = value.Conversation.ModelId, updatedAtUtc = value.Conversation.UpdatedAtUtc.ToString("O"), turnCount = value.TurnStates.Count
    };

    // A paused turn is replayed on the next send, so every archived tool call needs a result.
    private static List<ContentPart> Resumable(IReadOnlyList<ContentPart> source)
    {
        var parts = source.Where(part => !(part is UnknownPart unknown && unknown.Kind == "incompleteToolCall")).ToList();
        var answered = new HashSet<string>(parts.OfType<ToolResultPart>().Select(result => result.InvocationId), StringComparer.Ordinal);
        foreach (var call in parts.OfType<ToolCallPart>().Where(call => answered.Add(call.Id)))
            parts.Add(new ToolResultPart(call.Id, true, JsonWire.Serialize(new { code = "deferred" })));
        return parts;
    }

    private static object TurnView(StoredConversation value, int index)
    {
        var user = value.Conversation.Messages[index * 2];
        var assistant = value.Conversation.Messages[index * 2 + 1];
        var userText = user.Parts.OfType<TextPart>().ToArray();
        var view = new Dictionary<string, object>
        {
            ["index"] = index, ["userText"] = userText.FirstOrDefault()?.Text ?? string.Empty, ["hasImage"] = user.Parts.OfType<ImagePart>().Any(),
            ["text"] = string.Concat(assistant.Parts.OfType<TextPart>().Select(part => part.Text)),
            ["reasoning"] = string.Concat(assistant.Parts.OfType<ReasoningPart>().Select(part => part.Text)),
            ["tools"] = assistant.Parts.OfType<ToolCallPart>().Select(call => ToolView(call, assistant, value.TurnStates[index])).ToArray(),
            ["status"] = value.TurnStates[index],
            // Ordered timeline: the UI renders the turn from this sequence, not from the grouped fields above.
            ["blocks"] = assistant.Parts.SelectMany(part => part switch
            {
                ReasoningPart reasoning => new object[] { new { type = "reasoning", text = reasoning.Text } },
                TextPart textPart => new object[] { new { type = "text", text = textPart.Text } },
                ToolCallPart call => new object[] { ToolBlockView(call, assistant, value.TurnStates[index]) },
                _ => Array.Empty<object>()
            }).ToArray()
        };
        if (userText.Length > 1) view["contextText"] = string.Concat(userText.Skip(1).Select(part => part.Text));
        return view;
    }

    private static object ToolView(ToolCallPart call, Message assistant, string turnState)
    {
        var tool = new Dictionary<string, object> { ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = call.Arguments };
        ApplyToolResult(tool, call.Id, assistant, turnState);
        return tool;
    }

    private static object ToolBlockView(ToolCallPart call, Message assistant, string turnState)
    {
        var block = new Dictionary<string, object> { ["type"] = "tool", ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = call.Arguments };
        ApplyToolResult(block, call.Id, assistant, turnState);
        return block;
    }

    private static void ApplyToolResult(Dictionary<string, object> target, string invocationId, Message assistant, string turnState)
    {
        var result = assistant.Parts.OfType<ToolResultPart>().FirstOrDefault(part => part.InvocationId == invocationId);
        if (result is not null)
        {
            target["status"] = result.IsError ? "Failed" : "Completed";
            target["isError"] = result.IsError;
            target["content"] = result.Content;
        }
        else if (turnState != "completed")
        {
            target["status"] = turnState == "cancelled" ? "Cancelled" : "Failed";
            target["isError"] = true;
            target["content"] = string.Empty;
        }
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
    private static object ConfigurationView(ProviderConfiguration configuration) => new { id = configuration.Id, providerId = configuration.ProviderId, kind = configuration.Kind.ToString(), baseUrl = configuration.BaseUrl, selectedModelId = configuration.SelectedModelId, deploymentNames = configuration.DeploymentNames, arm = configuration.Arm != null ? new { configuration.Arm.SubscriptionId, configuration.Arm.ResourceGroup, configuration.Arm.AccountName } : null };
    private static object CatalogView(ProviderConfiguration configuration, CatalogResult result) => new
  {
    configuration = ConfigurationView(configuration),
    models = result.Models.Select(ModelView).ToArray(),
    credentialValid = result.CredentialValid,
    code = result.ErrorCode,
    retryAfter = result.RetryAfter is TimeSpan wait ? (long)Math.Ceiling(wait.TotalSeconds) : (long?)null
  };
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
        StreamError error => new { kind = "Error", code = error.Code, retryAfter = error.RetryAfterSeconds is long seconds ? seconds : (long?)null },
        _ => throw new InvalidOperationException()
    };
    private static Task Reply(Func<string, Task> send, string? id, string type, object payload) => send(JsonWire.Serialize(new { version = 1, id, type, payload }));
    private static void ValidateWriteText(string text, bool multiline)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsControl(character) && !(multiline && (character == '\r' || character == '\n' || character == '\t'))) throw new ProviderException("invalid_arguments");
            if (char.IsHighSurrogate(character))
            {
                if (++index == text.Length || !char.IsLowSurrogate(text[index])) throw new ProviderException("invalid_arguments");
            }
            else if (char.IsLowSurrogate(character)) throw new ProviderException("invalid_arguments");
        }
    }

    private static string RequiredString(JsonElement payload, string name, int maximum, bool allowEmpty = false)
    {
        var value = JsonWire.String(payload, name);
        if (value is null || value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value))) throw new ProviderException("invalid_message");
        return value;
    }
    private static string[] ContextIds(JsonElement payload, string name)
    {
        var value = JsonWire.Get(payload, name);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 8) throw new ProviderException("invalid_message");
        var ids = new HashSet<string>();
        var ordered = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParseExact(item.GetString(), "D", out var guid) || guid == Guid.Empty || !ids.Add(guid.ToString("D"))) throw new ProviderException("invalid_message");
            ordered.Add(guid.ToString("D"));
        }
        return ordered.ToArray();
    }
    private static void Require(JsonElement value, string[] required, string[]? optional = null) { if (!Fields(value, required, optional)) throw new ProviderException("invalid_message"); }
    private static bool Fields(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>();
        foreach (var property in value.EnumerateObject()) if (!seen.Add(property.Name) || (!required.Contains(property.Name) && !(optional?.Contains(property.Name) ?? false))) return false;
        return required.All(seen.Contains);
    }
    public void Dispose()
    {
        lock (activeGate) active?.Cancel();
        lock (approvalGate)
        {
            foreach (var pending in approvals.Values) pending.Decision.TrySetCanceled();
            approvals.Clear();
        }
        http.Dispose();
    }
}
