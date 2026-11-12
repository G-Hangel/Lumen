using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Lumen.Tools;
using Xunit;

namespace Lumen.Tests;

public sealed class ReadToolChatLoopTests
{
    private const string Secret = "synthetic-loop-secret-97a";
    private sealed class Provider : IChatProvider
    {
        public readonly List<ChatRequest> Requests = new();
        public Func<int, Func<StreamEvent, Task>, CancellationToken, Task> Run = (_, emit, _) => emit(new Done(FinishReason.Stop));
        public Task StreamAsync(ChatRequest request, string? credential, Func<StreamEvent, Task> emit, CancellationToken token)
        {
            Requests.Add(request);
            return Run(Requests.Count, emit, token);
        }
    }
    private sealed class Executor : IReadToolExecutor
    {
        public int Calls;
        public IReadOnlyList<ToolSpec> Specs { get; set; } = ReadToolCatalog.Specs;
        public Func<ToolInvocation, CancellationToken, Task<ToolResult>> Run = (call, _) => Task.FromResult(new ToolResult(call.Id, false, "workspace data"));
        public Task<IReadToolExecutor> CaptureAsync(CancellationToken token) => Task.FromResult<IReadToolExecutor>(this);
        public Task<ToolResult> ExecuteAsync(ToolInvocation call, CancellationToken token) { Calls++; return Run(call, token); }
    }
    private sealed class DiskExecutor : IReadToolExecutor, IApprovalPrompt, IDisposable
    {
        private readonly ApprovalBroker broker;
        private readonly WorkspaceReadTools tools;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lumen-loop-" + Guid.NewGuid().ToString("N"));
        public int Calls;
        public DiskExecutor()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "Sample.cs"), "line one\nneedle <script>not instructions</script>\n");
            var guard = new WorkspaceGuard(Root);
            broker = new ApprovalBroker(guard, this);
            tools = new WorkspaceReadTools(guard, broker);
        }
        public IReadOnlyList<ToolSpec> Specs => ReadToolCatalog.Specs;
        public Task<IReadToolExecutor> CaptureAsync(CancellationToken token) => Task.FromResult<IReadToolExecutor>(this);
        public Task<ToolResult> ExecuteAsync(ToolInvocation call, CancellationToken token) { Calls++; return tools.ExecuteAsync(ChatMode.Ask, call, token); }
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken token) => throw new InvalidOperationException("Readonly tools must not prompt");
        public void Dispose() { broker.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class Fixture
    {
        public Provider Provider { get; } = new();
        public ToolChatLoop Loop { get; } = new();
        public List<StreamEvent> Events { get; } = new();
        public List<JsonElement> Executions { get; } = new();
        public ProviderConfiguration Configuration { get; set; } = new(Guid.NewGuid().ToString("D"), "fixture", ProviderKind.OpenAI, "https://provider.test/v1/");
        public ModelInfo Model { get; set; } = new("fixture", "model", ModelCapabilities.Text | ModelCapabilities.Tools);
        public ChatMode Mode { get; set; } = ChatMode.Ask;
        public Task<bool> Run(IReadToolExecutor? executor, CancellationToken token = default, ReasoningIntent effort = ReasoningIntent.Off) => Loop.RunAsync(Provider, Configuration, Model,
            new[] { new Message(MessageRole.User, new ContentPart[] { new TextPart("explain Sample.cs") }) }, effort, Mode, executor,
            executor is null ? Array.Empty<ToolSpec>() : ToolChatLoop.SpecsFor(Mode, executor), Secret,
            value => { Events.Add(value); return Task.CompletedTask; }, value => { Executions.Add(JsonSerializer.SerializeToElement(value)); return Task.CompletedTask; }, token);
    }
    private static async Task Call(Func<StreamEvent, Task> emit, string id = "call1", string name = "read_file", string args = "{\"path\":\"Sample.cs\"}", string? state = null)
    {
        using var parsed = JsonDocument.Parse(args);
        await emit(new ToolCallStart(id, name));
        await emit(new ToolCallArgsDelta(id, args));
        await emit(new ToolCallEnd(id, parsed.RootElement, state));
    }
    private static async Task Answer(Func<StreamEvent, Task> emit) { await emit(new TextDelta("Sample.cs:2 contains needle")); await emit(new Done(FinishReason.Stop)); }

    [Theory]
    [InlineData(ChatMode.Ask)]
    [InlineData(ChatMode.Plan)]
    [InlineData(ChatMode.Agent)]
    public async Task EditSnapshotIsAdvertisedAndRunsInReadonlyModes(ChatMode mode)
    {
        using var disk = new DiskExecutor();
        var fixture = new Fixture { Mode = mode };
        Assert.Contains(ToolChatLoop.SpecsFor(mode, disk), spec => spec.Name == "get_edit_snapshot");
        fixture.Provider.Run = async (round, emit, _) =>
        {
            if (round == 1) { await Call(emit, name: "get_edit_snapshot"); await emit(new Done(FinishReason.ToolUse)); }
            else await Answer(emit);
        };
        Assert.True(await fixture.Run(disk));
        Assert.Equal(1, disk.Calls);
        Assert.Contains("base_sha256", Assert.Single(fixture.Provider.Requests[1].Messages.Last().Parts.OfType<ToolResultPart>()).Content);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RealReadonlyToolsFeedDistinctMessagesAndSignedStateSurvivesArchiveReplay(int rounds)
    {
        using var disk = new DiskExecutor();
        var fixture = new Fixture();
        const string signature = "{\"type\":\"thinking\",\"thinking\":\"inspect\",\"signature\":\"opaque-signature\"}";
        fixture.Provider.Run = async (round, emit, _) =>
        {
            if (round == rounds) { await Answer(emit); return; }
            await emit(new ReasoningDelta("inspect", signature));
            await Call(emit, "call" + round);
            await emit(new Done(FinishReason.ToolUse));
        };
        Assert.True(await fixture.Run(disk));
        Assert.Equal(rounds - 1, disk.Calls);
        Assert.Equal(rounds, fixture.Provider.Requests.Count);
        var second = fixture.Provider.Requests[1];
        Assert.Equal(new[] { MessageRole.System, MessageRole.User, MessageRole.Assistant, MessageRole.Tool }, second.Messages.Select(message => message.Role));
        Assert.Contains("untrusted data", Assert.IsType<TextPart>(second.Messages[0].Parts[0]).Text);
        Assert.Equal(signature, Assert.Single(second.Messages[2].Parts.OfType<ReasoningPart>()).ProviderState);
        Assert.Contains("needle", Assert.Single(second.Messages[3].Parts.OfType<ToolResultPart>()).Content);
        Assert.All(second.Messages, message => Assert.False(message.Parts.OfType<ToolCallPart>().Any() && message.Parts.OfType<ToolResultPart>().Any()));
        var domain = new Conversation(Guid.NewGuid().ToString("D"), ChatMode.Ask, new[] { second.Messages[1], new Message(MessageRole.Assistant, fixture.Loop.Parts.ToArray()) }, modelId: "model");
        Assert.True(ConversationSerializer.TryParse(ConversationSerializer.ToJson(domain), out var reopened, out _));
        var replay = ToolChatLoop.Replay(new StoredConversation(fixture.Configuration.Id, reopened!, new[] { "completed" }));
        Assert.Equal(rounds * 2, replay.Count);
        Assert.Equal(signature, Assert.Single(replay[1].Parts.OfType<ReasoningPart>()).ProviderState);
        var body = new OpenAIProvider(new HttpTransport(new System.Net.Http.HttpClient()), new CapabilityResolver()).BuildPayload(new ChatRequest(fixture.Configuration, fixture.Model, replay));
        using var payload = JsonDocument.Parse(body);
        var messages = payload.RootElement.GetProperty("messages");
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call1", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal((rounds - 1) * 2, fixture.Executions.Count);
    }

    [Fact]
    public async Task GeminiLocalIdsAreScopedPerPostWithoutChangingSignedToolParts()
    {
        var fixture = new Fixture();
        fixture.Configuration = new ProviderConfiguration(fixture.Configuration.Id, "fixture", ProviderKind.Gemini, "https://provider.test/");
        var executor = new Executor();
        const string state = "{\"functionCall\":{\"name\":\"read_file\",\"args\":{\"path\":\"Sample.cs\"}},\"thoughtSignature\":\"signed-tool\"}";
        fixture.Provider.Run = async (round, emit, _) => { if (round < 3) { await Call(emit, "gemini-call-0", state: state); await emit(new Done(FinishReason.ToolUse)); } else await Answer(emit); };
        Assert.True(await fixture.Run(executor));
        Assert.Equal(2, executor.Calls);
        var calls = fixture.Loop.Parts.OfType<ToolCallPart>().ToArray();
        Assert.Equal(2, calls.Select(call => call.Id).Distinct().Count());
        Assert.All(calls, call => Assert.Equal(state, call.ProviderState));
        using var client = new System.Net.Http.HttpClient();
        var payload = new GeminiProvider(new HttpTransport(client), new CapabilityResolver()).BuildPayload(fixture.Provider.Requests.Last());
        using var json = JsonDocument.Parse(payload);
        var contents = json.RootElement.GetProperty("contents");
        Assert.Equal(new[] { "user", "model", "user", "model", "user" }, contents.EnumerateArray().Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("signed-tool", contents[1].GetProperty("parts")[0].GetProperty("thoughtSignature").GetString());
        Assert.False(contents[2].GetProperty("parts")[0].GetProperty("functionResponse").TryGetProperty("id", out _));
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("bad/name")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task MalformedCallIdentifiersFailBeforeIo(string id)
    {
        var fixture = new Fixture();
        var executor = new Executor();
        fixture.Provider.Run = async (_, emit, _) => { await Call(emit, id); await emit(new Done(FinishReason.ToolUse)); };
        Assert.Equal("invalid_stream", (await Assert.ThrowsAsync<ProviderException>(() => fixture.Run(executor))).Code);
        Assert.Equal(0, executor.Calls);
        Assert.Empty(fixture.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamBytesAndEventsAreCumulativeAcrossProviderRounds(bool countEvents)
    {
        var fixture = new Fixture();
        var executor = new Executor();
        fixture.Provider.Run = async (round, emit, _) =>
        {
            if (countEvents) for (var index = 0; index < 16384; index++) await emit(new Usage(0, 0));
            else { await emit(new TextDelta(new string('x', 4 * 1024 * 1024))); await emit(new TextDelta(new string('x', 4 * 1024 * 1024))); await emit(new TextDelta("x")); }
            await Call(emit, "round" + round);
            await emit(new Done(FinishReason.ToolUse));
        };
        Assert.Equal("stream_too_large", (await Assert.ThrowsAsync<ProviderException>(() => fixture.Run(executor))).Code);
        Assert.Equal(countEvents ? 2 : 1, fixture.Provider.Requests.Count);
        Assert.Equal(countEvents ? 1 : 0, executor.Calls);
    }

    [Fact]
    public async Task EightToolsCanExecuteButNinthCannotAndModelOutputCapIsRespected()
    {
        var fixture = new Fixture { Model = new ModelInfo("fixture", "model", ModelCapabilities.Text | ModelCapabilities.Tools, maxOutputTokens: 512) };
        var executor = new Executor();
        fixture.Provider.Run = async (round, emit, _) => { for (var index = 0; index < (round == 1 ? 8 : 1); index++) await Call(emit, "r" + round + "c" + index); await emit(new Done(FinishReason.ToolUse)); };
        Assert.Equal("tool_budget_exceeded", (await Assert.ThrowsAsync<ProviderException>(() => fixture.Run(executor))).Code);
        Assert.Equal(8, executor.Calls);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.All(fixture.Provider.Requests, request => Assert.Equal(512, request.MaxOutputTokens));
    }

    [Fact]
    public async Task OptoutNeverDeclaresOrInvokesToolsEvenWhenModelRequestsThem()
    {
        var fixture = new Fixture();
        fixture.Provider.Run = async (_, emit, _) => { await Call(emit); await emit(new Done(FinishReason.ToolUse)); };
        Assert.True(await fixture.Run(null));
        Assert.Empty(Assert.Single(fixture.Provider.Requests).Tools);
        Assert.Equal(new[] { MessageRole.System, MessageRole.User }, fixture.Provider.Requests[0].Messages.Select(message => message.Role));
        Assert.Empty(fixture.Executions);
    }

    [Theory]
    [InlineData("run_command", "{}", "unknown_tool")]
    [InlineData("create_file", "{}", "unknown_tool")]
    [InlineData("unknown", "{}", "unknown_tool")]
    [InlineData("get_edit_snapshot", "{\"path\":\"Sample.cs\",\"maxResults\":1}", "invalid_arguments")]
    [InlineData("get_edit_snapshot", "{\"path\":\"../../Windows/win.ini\"}", "invalid_arguments")]
    [InlineData("read_file", "{\"path\":\"../../Windows/win.ini\"}", "invalid_arguments")]
    [InlineData("read_file", "{\"path\":\"Sample.cs\",\"mode\":\"Agent\"}", "invalid_arguments")]
    [InlineData("read_file", "{\"path\":\"Sample.cs\",\"startLine\":\"1\"}", "invalid_arguments")]
    public async Task UnadvertisedWritingTraversalAndInvalidArgumentsReturnCorrelatedErrorWithoutExecutor(string name, string args, string code)
    {
        var fixture = new Fixture();
        var executor = new Executor();
        fixture.Provider.Run = async (round, emit, _) => { if (round == 1) { await Call(emit, name: name, args: args); await emit(new Done(FinishReason.ToolUse)); } else await Answer(emit); };
        Assert.True(await fixture.Run(executor));
        Assert.Equal(0, executor.Calls);
        var result = Assert.Single(fixture.Provider.Requests[1].Messages.Last().Parts.OfType<ToolResultPart>());
        Assert.Equal("call1", result.InvocationId);
        Assert.True(result.IsError);
        Assert.Contains(code, result.Content);
    }

    [Fact]
    public async Task AllowlistCannotBeExtendedByExecutorAndUnadvertisedReadonlyNameIsNotInvoked()
    {
        var executor = new Executor { Specs = ReadToolCatalog.Specs.Where(spec => spec.Name == "grep").Concat(new[] { new ToolSpec("run_command", "write", JsonSerializer.SerializeToElement(new { type = "object" })) }).ToArray() };
        var fixture = new Fixture();
        fixture.Provider.Run = async (round, emit, _) => { if (round == 1) { await Call(emit); await emit(new Done(FinishReason.ToolUse)); } else await Answer(emit); };
        Assert.True(await fixture.Run(executor));
        Assert.Equal("grep", Assert.Single(fixture.Provider.Requests[0].Tools).Name);
        Assert.Equal(0, executor.Calls);
    }

    [Theory]
    [InlineData("eof")]
    [InlineData("error")]
    [InlineData("max_tokens")]
    [InlineData("duplicate")]
    [InlineData("unfinished")]
    [InlineData("malformed")]
    [InlineData("duplicate_json")]
    public async Task BrokenOrNonToolTerminalBatchNeverInvokesToolsOrContinues(string bad)
    {
        var fixture = new Fixture();
        var executor = new Executor();
        fixture.Provider.Run = async (_, emit, _) =>
        {
            if (bad == "unfinished") { await emit(new ToolCallStart("call1", "read_file")); await emit(new Done(FinishReason.ToolUse)); return; }
            if (bad == "malformed") { await emit(new ToolCallStart("call1", "read_file")); await emit(new ToolCallArgsDelta("call1", "{")); using var json = JsonDocument.Parse("{}"); await emit(new ToolCallEnd("call1", json.RootElement)); return; }
            await Call(emit, args: bad == "duplicate_json" ? "{\"path\":\"Sample.cs\",\"path\":\"other.cs\"}" : "{\"path\":\"Sample.cs\"}");
            if (bad == "duplicate") await Call(emit);
            if (bad == "error") await emit(new StreamError("provider_error", "failure"));
            else if (bad != "eof") await emit(new Done(bad == "max_tokens" ? FinishReason.MaxTokens : FinishReason.ToolUse));
        };
        if (bad is "duplicate" or "malformed" or "duplicate_json") Assert.Equal("invalid_stream", (await Assert.ThrowsAsync<ProviderException>(() => fixture.Run(executor))).Code);
        else Assert.False(await fixture.Run(executor));
        Assert.Equal(0, executor.Calls);
        Assert.Single(fixture.Provider.Requests);
    }

    [Theory]
    [InlineData(1, false, 4, 3)]
    [InlineData(3, false, 3, 6)]
    [InlineData(1, true, 2, 1)]
    public async Task RepeatingModelStopsBeforeIoWhenRequestToolOrRequestedTokenBudgetIsExhausted(int batchSize, bool reasoning, int requests, int executions)
    {
        var fixture = new Fixture();
        if (reasoning) fixture.Model = new ModelInfo("fixture", "model", ModelCapabilities.Text | ModelCapabilities.Tools | ModelCapabilities.Reasoning, reasoning: new ReasoningSupport(ReasoningKind.BudgetTokens, new[] { ReasoningIntent.Low }));
        var executor = new Executor();
        fixture.Provider.Run = async (round, emit, _) => { for (var index = 0; index < batchSize; index++) await Call(emit, "r" + round + "c" + index); await emit(new Done(FinishReason.ToolUse)); };
        Assert.Equal("tool_budget_exceeded", (await Assert.ThrowsAsync<ProviderException>(() => fixture.Run(executor, effort: reasoning ? ReasoningIntent.Low : ReasoningIntent.Off))).Code);
        Assert.Equal(requests, fixture.Provider.Requests.Count);
        Assert.Equal(executions, executor.Calls);
        Assert.True(fixture.Provider.Requests.Sum(request => request.MaxOutputTokens) <= 8192);
        Assert.All(fixture.Provider.Requests, request => Assert.Equal(reasoning ? 4096 : 1024, request.MaxOutputTokens));
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("correlation")]
    [InlineData("secret")]
    [InlineData("escaped_secret")]
    [InlineData("stale")]
    public async Task InvalidOrStaleExecutorResultsNeverReachUiProviderOrArchive(string bad)
    {
        var fixture = new Fixture();
        var executor = new Executor { Run = (call, _) => Task.FromResult(new ToolResult(bad == "correlation" ? "other" : call.Id, bad == "stale",
            bad == "oversized" ? new string('x', 65537) : bad == "secret" ? Secret : bad == "escaped_secret" ? "{\"data\":\"" + string.Concat(Secret.Select(character => "\\u" + ((int)character).ToString("x4"))) + "\"}" : "{\"code\":\"context_stale\"}")) };
        fixture.Provider.Run = async (_, emit, _) => { await Call(emit); await emit(new Done(FinishReason.ToolUse)); };
        Assert.Equal(bad == "stale" ? "context_stale" : "invalid_response", (await Assert.ThrowsAsync<ProviderException>(() => fixture.Run(executor))).Code);
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(1, executor.Calls);
        Assert.Empty(fixture.Loop.Parts.OfType<ToolResultPart>());
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(fixture.Executions));
        Assert.Equal(new[] { "Running", "Failed" }, fixture.Executions.Select(value => value.GetProperty("status").GetString()));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("arguments")]
    [InlineData("state")]
    [InlineData("error")]
    public async Task SecretsSplitAcrossDeltasOrEscapedInOpaqueStateAreRejectedBeforeDisplay(string location)
    {
        var fixture = new Fixture();
        var executor = new Executor();
        fixture.Provider.Run = async (_, emit, _) =>
        {
            if (location == "text") { await emit(new TextDelta(Secret.Substring(0, 10))); await emit(new TextDelta(Secret.Substring(10))); }
            else if (location == "state") await emit(new ReasoningDelta("", JsonSerializer.Serialize(new { signature = Secret })));
            else if (location == "error") await emit(new StreamError(Secret, "bad"));
            else await Call(emit, args: JsonSerializer.Serialize(new { path = Secret }));
            await emit(new Done(FinishReason.ToolUse));
        };
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<ProviderException>(() => fixture.Run(executor))).Code);
        Assert.True(fixture.Loop.UnsafeResponse);
        Assert.Equal(0, executor.Calls);
        Assert.DoesNotContain(Secret, string.Concat(fixture.Events.OfType<TextDelta>().Select(value => value.Text)));
        Assert.Empty(fixture.Executions);
    }

    [Fact]
    public async Task ProviderExceptionCodesCannotEchoCredentialIntoFinalError()
    {
        var fixture = new Fixture();
        fixture.Provider.Run = (_, _, _) => throw new ProviderException(Secret);
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<ProviderException>(() => fixture.Run(new Executor()))).Code);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task CancellationBetweenSequentialToolsStopsNextIoAndMarksPendingCards()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var executor = new Executor { Run = (call, _) => { cancellation.Cancel(); return Task.FromResult(new ToolResult(call.Id, false, "data")); } };
        fixture.Provider.Run = async (_, emit, _) => { await Call(emit, "one"); await Call(emit, "two"); await emit(new Done(FinishReason.ToolUse)); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Run(executor, cancellation.Token));
        Assert.Equal(1, executor.Calls);
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(2, fixture.Executions.Count(value => value.GetProperty("status").GetString() == "Cancelled"));
        Assert.All(fixture.Executions.Where(value => value.GetProperty("status").GetString() == "Cancelled"), value => Assert.Equal("", value.GetProperty("content").GetString()));
    }

    [Fact]
    public async Task RawArgumentsAt8192BoundaryBecomeControlledErrorWithoutIo()
    {
        var fixture = new Fixture();
        var executor = new Executor();
        var args = "{\"path\":\"" + new string('x', 8181) + "\"}";
        Assert.Equal(8192, args.Length);
        fixture.Provider.Run = async (round, emit, _) => { if (round == 1) { await Call(emit, args: args); await emit(new Done(FinishReason.ToolUse)); } else await Answer(emit); };
        Assert.True(await fixture.Run(executor));
        Assert.Equal(0, executor.Calls);
        Assert.Contains("invalid_arguments", Assert.Single(fixture.Loop.Parts.OfType<ToolResultPart>()).Content);
    }

    [Fact]
    public async Task CumulativeResultBudgetStopsWithoutReinjectingExcess()
    {
        var fixture = new Fixture();
        var executor = new Executor { Run = (call, _) => Task.FromResult(new ToolResult(call.Id, false, new string('x', 65536))) };
        fixture.Provider.Run = async (_, emit, _) => { await Call(emit, "a"); await Call(emit, "b"); await Call(emit, "c"); await emit(new Done(FinishReason.ToolUse)); };
        Assert.Equal("tool_budget_exceeded", (await Assert.ThrowsAsync<ProviderException>(() => fixture.Run(executor))).Code);
        Assert.Equal(2, executor.Calls);
        Assert.Single(fixture.Provider.Requests);
    }

    [Fact]
    public void InputAndHistoryLimitsRejectRatherThanSilentlyTruncateAndOrphansAreDropped()
    {
        var fixture = new Fixture();
        var messages = new[] { new Message(MessageRole.User, new ContentPart[] { new TextPart(new string('x', 262145)) }) };
        Assert.Equal("context_too_large", Assert.Throws<ProviderException>(() => ToolChatLoop.ValidateInput(messages, Array.Empty<ToolSpec>(), fixture.Model, 1024)).Code);
        using var args = JsonDocument.Parse("{}");
        var orphan = new Message(MessageRole.Assistant, new ContentPart[] { new ToolCallPart("orphan", "read_file", args.RootElement) });
        var stored = new StoredConversation(fixture.Configuration.Id, new Conversation(Guid.NewGuid().ToString("D"), ChatMode.Ask, new[] { messages[0], orphan }, modelId: "model"), new[] { "completed" });
        Assert.Empty(ToolChatLoop.Replay(stored));
    }

    private static ToolSpec[] AgentSpecs => ReadToolCatalog.Specs.Concat(WriteToolCatalog.Specs).Concat(CommandToolCatalog.Specs).ToArray();

    [Fact]
    public void AgentAdvertisesToolsWhileAskFiltersWritesAndCommands()
    {
        var executor = new Executor { Specs = AgentSpecs };
        var agent = ToolChatLoop.SpecsFor(ChatMode.Agent, executor);
        Assert.Equal(14, agent.Count);
        Assert.Contains(agent, spec => spec.Name == "apply_diff");
        Assert.Contains(agent, spec => spec.Name == "run_command");
        var ask = ToolChatLoop.SpecsFor(ChatMode.Ask, executor);
        Assert.Equal(8, ask.Count);
        Assert.All(ask, spec => Assert.DoesNotContain(spec.Name, new[] { "create_file", "edit_file", "apply_diff", "run_command" }));
    }

    [Fact]
    public void AgentWriteSchemasAdvertiseEnforcedLimits()
    {
        var specs = ToolChatLoop.SpecsFor(ChatMode.Agent, new Executor { Specs = AgentSpecs });
        foreach (var spec in specs.Where(value => value.Name == "create_file" || value.Name == "edit_file"))
        {
            var properties = spec.ParametersSchema.GetProperty("properties");
            Assert.Equal(1024, properties.GetProperty("path").GetProperty("maxLength").GetInt32());
            Assert.Equal(65536, properties.GetProperty("content").GetProperty("maxLength").GetInt32());
        }
    }

    [Theory]
    [InlineData("C:/external.cs")]
    [InlineData("C:\\external.cs")]
    public async Task AbsoluteWritePathReachesApprovalExecutor(string path)
    {
        var fixture = new Fixture { Mode = ChatMode.Agent };
        var executor = new Executor
        {
            Specs = AgentSpecs,
            Run = (call, _) =>
            {
                Assert.Equal(path, call.Arguments.GetProperty("path").GetString());
                return Task.FromResult(new ToolResult(call.Id, true, "{\"code\":\"approval_denied\"}"));
            }
        };
        fixture.Provider.Run = async (round, emit, _) =>
        {
            if (round == 1) { await Call(emit, name: "edit_file", args: JsonSerializer.Serialize(new { path, content = "x" })); await emit(new Done(FinishReason.ToolUse)); }
            else await Answer(emit);
        };
        Assert.True(await fixture.Run(executor));
        Assert.Equal(1, executor.Calls);
        Assert.Contains("approval_denied", Assert.Single(fixture.Loop.Parts.OfType<ToolResultPart>()).Content);
    }

    [Theory]
    [InlineData(32768, true, "edit_file")]
    [InlineData(65533, false, "edit_file")]
    [InlineData(32768, true, "apply_diff")]
    [InlineData(65533, false, "apply_diff")]
    public async Task WriteReservesResultBudgetBeforeIo(int readSize, bool writeAllowed, string name)
    {
        var fixture = new Fixture { Mode = ChatMode.Agent };
        var writes = 0;
        var executor = new Executor { Specs = AgentSpecs, Run = (call, _) =>
        {
            if (call.Name == name) { writes++; return Task.FromResult(new ToolResult(call.Id, false, "visible diff")); }
            return Task.FromResult(new ToolResult(call.Id, false, new string('x', readSize)));
        } };
        fixture.Provider.Run = async (round, emit, _) =>
        {
            if (round == 1)
            {
                await Call(emit, "read-one"); await Call(emit, "read-two");
                var args = name == "edit_file" ? "{\"path\":\"Sample.cs\",\"content\":\"x\"}" : JsonSerializer.Serialize(new { path = "Sample.cs", base_sha256 = new string('a', 64), replacements = new[] { new { old = "x", @new = "y" } } });
                await Call(emit, "write", name, args);
                await emit(new Done(FinishReason.ToolUse));
            }
            else await Answer(emit);
        };
        if (writeAllowed) Assert.True(await fixture.Run(executor));
        else Assert.Equal("tool_budget_exceeded", (await Assert.ThrowsAsync<ProviderException>(() => fixture.Run(executor))).Code);
        Assert.Equal(writeAllowed ? 1 : 0, writes);
        Assert.Equal(writeAllowed ? 3 : 2, executor.Calls);
    }

    [Theory]
    [InlineData("edit_file")]
    [InlineData("apply_diff")]
    public async Task CancellationAfterCommittedWriteArchivesDiffAndStopsNextIo(string name)
    {
        var fixture = new Fixture { Mode = ChatMode.Agent };
        using var cancellation = new CancellationTokenSource();
        const string outcome = "{\"edited\":true,\"diff\":\"visible change\"}";
        var executor = new Executor { Specs = AgentSpecs, Run = (call, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new ToolResult(call.Id, false, outcome));
        } };
        fixture.Provider.Run = async (_, emit, _) =>
        {
            var args = name == "edit_file" ? "{\"path\":\"Sample.cs\",\"content\":\"x\"}" : JsonSerializer.Serialize(new { path = "Sample.cs", base_sha256 = new string('a', 64), replacements = new[] { new { old = "x", @new = "y" } } });
            await Call(emit, "one", name, args);
            await Call(emit, "two", "create_file", "{\"path\":\"New.cs\",\"content\":\"x\"}");
            await emit(new Done(FinishReason.ToolUse));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Run(executor, cancellation.Token));
        Assert.Equal(1, executor.Calls);
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(outcome, Assert.Single(fixture.Loop.Parts.OfType<ToolResultPart>()).Content);
        Assert.Equal("one", Assert.Single(fixture.Executions, value => value.GetProperty("status").GetString() == "Completed").GetProperty("id").GetString());
        Assert.Equal("two", Assert.Single(fixture.Executions, value => value.GetProperty("status").GetString() == "Cancelled").GetProperty("id").GetString());
    }

    [Fact]
    public async Task AgentWriteExecutesWithAgentInstructionAndReinjectsOutcome()
    {
        var fixture = new Fixture { Mode = ChatMode.Agent };
        var executor = new Executor { Specs = AgentSpecs };
        fixture.Provider.Run = async (round, emit, _) =>
        {
            if (round == 1) { await Call(emit, name: "create_file", args: "{\"path\":\"New.cs\",\"content\":\"class New {}\"}"); await emit(new Done(FinishReason.ToolUse)); }
            else await Answer(emit);
        };
        Assert.True(await fixture.Run(executor));
        Assert.Equal(1, executor.Calls);
        Assert.Equal(14, fixture.Provider.Requests[0].Tools.Count);
        var instruction = Assert.IsType<TextPart>(fixture.Provider.Requests[0].Messages[0].Parts[0]).Text;
        Assert.Contains("visible diff", instruction);
        Assert.Contains("explicit user approval", instruction);
        Assert.Contains("untrusted data", instruction);
        Assert.Contains("workspace data", Assert.Single(fixture.Provider.Requests[1].Messages.Last().Parts.OfType<ToolResultPart>()).Content);
    }

    [Theory]
    [InlineData("create_file", "{\"path\":\"New.cs\"}")]
    [InlineData("create_file", "{\"content\":\"x\"}")]
    [InlineData("create_file", "{\"path\":\"New.cs\",\"content\":\"x\",\"extra\":1}")]
    [InlineData("edit_file", "{\"path\":\"../outside.cs\",\"content\":\"x\"}")]
    [InlineData("edit_file", "{\"path\":\"/absolute.cs\",\"content\":\"x\"}")]
    [InlineData("edit_file", "{\"path\":\"C:absolute.cs\",\"content\":\"x\"}")]
    [InlineData("edit_file", "{\"path\":\"C:/absolute.cs:stream\",\"content\":\"x\"}")]
    [InlineData("edit_file", "{\"path\":\"C:/../outside.cs\",\"content\":\"x\"}")]
    [InlineData("run_command", "{\"command\":\"cmd\"}")]
    [InlineData("run_command", "{\"command\":\"cmd\",\"args\":\"/c echo\"}")]
    [InlineData("run_command", "{\"command\":\"\",\"args\":[]}")]
    [InlineData("run_command", "{\"command\":\"cmd\",\"args\":[],\"timeout_ms\":0}")]
    [InlineData("run_command", "{\"command\":\"cmd\",\"args\":[],\"timeout_ms\":60001}")]
    [InlineData("run_command", "{\"command\":\"cmd\",\"args\":[],\"shell\":\"bash\"}")]
    public async Task InvalidAgentArgumentsReturnCorrelatedErrorWithoutIo(string name, string args)
    {
        var fixture = new Fixture { Mode = ChatMode.Agent };
        var executor = new Executor { Specs = AgentSpecs };
        fixture.Provider.Run = async (round, emit, _) => { if (round == 1) { await Call(emit, name: name, args: args); await emit(new Done(FinishReason.ToolUse)); } else await Answer(emit); };
        Assert.True(await fixture.Run(executor));
        Assert.Equal(0, executor.Calls);
        var result = Assert.Single(fixture.Provider.Requests[1].Messages.Last().Parts.OfType<ToolResultPart>());
        Assert.True(result.IsError);
        Assert.Contains("invalid_arguments", result.Content);
    }

    [Fact]
    public async Task OversizedWriteContentIsRejectedWithoutIo()
    {
        var fixture = new Fixture { Mode = ChatMode.Agent };
        var executor = new Executor { Specs = AgentSpecs };
        var args = "{\"path\":\"New.cs\",\"content\":\"" + new string('x', 65537) + "\"}";
        fixture.Provider.Run = async (round, emit, _) => { if (round == 1) { await Call(emit, name: "create_file", args: args); await emit(new Done(FinishReason.ToolUse)); } else await Answer(emit); };
        Assert.True(await fixture.Run(executor));
        Assert.Equal(0, executor.Calls);
        Assert.Contains("invalid_arguments", Assert.Single(fixture.Loop.Parts.OfType<ToolResultPart>()).Content);
    }

    [Fact]
    public async Task RawCommandArgumentsAtPerToolCapBecomeControlledErrorWithoutIo()
    {
        var fixture = new Fixture { Mode = ChatMode.Agent };
        var executor = new Executor { Specs = AgentSpecs };
        var args = "{\"command\":\"cmd\",\"args\":[\"\"]}";
        args = args.Insert(args.Length - 3, new string('x', 65536 - args.Length));
        Assert.Equal(65536, args.Length);
        fixture.Provider.Run = async (round, emit, _) => { if (round == 1) { await Call(emit, name: "run_command", args: args); await emit(new Done(FinishReason.ToolUse)); } else await Answer(emit); };
        Assert.True(await fixture.Run(executor));
        Assert.Equal(0, executor.Calls);
        Assert.Contains("invalid_arguments", Assert.Single(fixture.Loop.Parts.OfType<ToolResultPart>()).Content);
    }

    [Fact]
    public async Task RawWriteArgumentsBeyondContextBudgetFailControlledWithoutIo()
    {
        var fixture = new Fixture { Mode = ChatMode.Agent };
        var executor = new Executor { Specs = AgentSpecs };
        var args = "{\"path\":\"New.cs\",\"content\":\"\"}";
        args = args.Insert(args.Length - 2, new string('x', 420000 - args.Length));
        Assert.Equal(420000, args.Length);
        fixture.Provider.Run = async (_, emit, _) => { await Call(emit, name: "create_file", args: args); await emit(new Done(FinishReason.ToolUse)); };
        Assert.Equal("context_too_large", (await Assert.ThrowsAsync<ProviderException>(() => fixture.Run(executor))).Code);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task DeniedCommandIsReinjectedAsErrorAndLoopCompletes()
    {
        var fixture = new Fixture { Mode = ChatMode.Agent };
        var executor = new Executor { Specs = AgentSpecs, Run = (call, _) => Task.FromResult(new ToolResult(call.Id, true, "{\"code\":\"approval_denied\"}")) };
        fixture.Provider.Run = async (round, emit, _) =>
        {
            if (round == 1) { await Call(emit, name: "run_command", args: "{\"command\":\"cmd\",\"args\":[\"/c\",\"echo\",\"hi\"]}"); await emit(new Done(FinishReason.ToolUse)); }
            else await Answer(emit);
        };
        Assert.True(await fixture.Run(executor));
        Assert.Equal(1, executor.Calls);
        var result = Assert.Single(fixture.Provider.Requests[1].Messages.Last().Parts.OfType<ToolResultPart>());
        Assert.True(result.IsError);
        Assert.Contains("approval_denied", result.Content);
    }

    [Fact]
    public async Task AgentOptoutNeverDeclaresToolsEvenWithAgentExecutor()
    {
        var fixture = new Fixture { Mode = ChatMode.Agent };
        fixture.Provider.Run = async (_, emit, _) => { await Call(emit, name: "run_command", args: "{\"command\":\"cmd\",\"args\":[]}"); await emit(new Done(FinishReason.ToolUse)); };
        Assert.True(await fixture.Run(null));
        Assert.Empty(Assert.Single(fixture.Provider.Requests).Tools);
        Assert.Empty(fixture.Executions);
    }
}
