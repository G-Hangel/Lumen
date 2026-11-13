using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Lumen.Tools;
using Xunit;

namespace Lumen.Tests;

public sealed class AgentToolSessionTests
{
    private sealed class Credentials : ICredentialStore
    {
        public int Reads;
        public Task<string?> ReadAsync(string id, CancellationToken token) { Reads++; return Task.FromResult<string?>("synthetic-agent-key"); }
        public Task WriteAsync(string id, string key, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();
        public readonly Queue<Func<HttpResponseMessage>> PostResponses = new();
        public bool RequestTool = true;
        public string ToolName = "run_command";
        public string Arguments = "{\"command\":\"cmd\",\"args\":[\"/c\",\"echo\",\"fixture\"]}";
        public string ModelsJson = "{\"data\":[{\"id\":\"fixture\"}]}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ModelsJson) };
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (PostResponses.Count > 0) return PostResponses.Dequeue()();
            var data = RequestTool && Bodies.Count % 2 == 1
                ? JsonSerializer.Serialize(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "call" + Bodies.Count, function = new { name = ToolName, arguments = Arguments } } } }, finish_reason = "tool_calls" } } })
                : "{\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: " + data + "\n\ndata: [DONE]\n\n") };
        }
    }
    private sealed class ReadExecutor : IReadToolExecutor
    {
        public int Captures;
        public int Calls;
        public IReadOnlyList<ToolSpec> Specs => ReadToolCatalog.Specs;
        public Task<IReadToolExecutor> CaptureAsync(CancellationToken token) { Captures++; return Task.FromResult<IReadToolExecutor>(this); }
        public Task<ToolResult> ExecuteAsync(ToolInvocation call, CancellationToken token) { Calls++; return Task.FromResult(new ToolResult(call.Id, false, "read data")); }
    }
    private sealed class WriteExecutor : IWriteToolExecutor
    {
        public Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>> Prompt = (_, _) => Task.FromResult(ApprovalResponse.Deny);
        public bool RequiresApproval;
        public int Captures;
        public int Calls;
        public readonly List<bool> Turns = new();
        public Func<bool, CancellationToken, Task> Begin = (_, _) => Task.CompletedTask;
        public IReadOnlyList<ToolSpec> Specs => WriteToolCatalog.Specs;
        public Task<IWriteToolExecutor> CaptureAsync(CancellationToken token) { Captures++; return Task.FromResult<IWriteToolExecutor>(this); }
        public Task BeginTurnAsync(bool automatic, CancellationToken token) { Turns.Add(automatic); return Begin(automatic, token); }
        public async Task<ToolResult> ExecuteAsync(ToolInvocation call, CancellationToken token)
        {
            Assert.NotEmpty(Turns);
            Calls++;
            if (RequiresApproval)
            {
                var response = await Prompt(new ApprovalRequest(call, ToolCategory.WriteOutsideWorkspace, "C:/outside/target.cs"), token);
                if (response != ApprovalResponse.AllowOnce) return new ToolResult(call.Id, true, "{\"code\":\"approval_denied\"}");
            }
            return new ToolResult(call.Id, false, "{\"diff\":{\"truncated\":false,\"changes\":[],\"linesBefore\":0,\"linesAfter\":1}}");
        }
        public Task UndoTurnAsync(CancellationToken token) => Task.CompletedTask;
        public int PendingChanges => 0;
    }
    private sealed class ShellExecutor : IShellToolExecutor
    {
        public Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>> Prompt = (_, _) => Task.FromResult(ApprovalResponse.Deny);
        public int Captures;
        public int Calls;
        public IReadOnlyList<ToolSpec> Specs => CommandToolCatalog.Specs;
        public Task<IShellToolExecutor> CaptureAsync(CancellationToken token) { Captures++; return Task.FromResult<IShellToolExecutor>(this); }
        public async Task<ToolResult> ExecuteAsync(ToolInvocation call, CancellationToken token)
        {
            Calls++;
            var response = await Prompt(new ApprovalRequest(call, ToolCategory.ExecuteCommand, null), token);
            return response == ApprovalResponse.Deny
                ? new ToolResult(call.Id, true, "{\"code\":\"approval_denied\"}")
                : new ToolResult(call.Id, false, "{\"exitCode\":0,\"stdout\":\"fixture\",\"stderr\":\"\",\"truncated\":false}");
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lumen-agent-" + Guid.NewGuid().ToString("N"));
        public string Id { get; } = Guid.NewGuid().ToString("D");
        public Handler Handler { get; } = new();
        public Credentials Credentials { get; } = new();
        public ReadExecutor Reader { get; } = new();
        public WriteExecutor Writer { get; } = new();
        public ShellExecutor Shell { get; } = new();
        private readonly HttpClient client;
        public ByokSession Session { get; }
        public readonly ConcurrentQueue<JsonElement> Replies = new();
        public readonly TaskCompletionSource<JsonElement> ApprovalSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string LastId { get; private set; } = "";
        public Fixture(bool withWriters = true)
        {
            Directory.CreateDirectory(Root);
            client = new HttpClient(Handler);
            var factory = new ProviderFactory(new HttpTransport(client), new CapabilityResolver());
            Session = new ByokSession(factory, Credentials, new SettingsStore(Path.Combine(Root, "providers.json")),
                readTools: Reader, writeTools: withWriters ? Writer : null, shellTools: withWriters ? Shell : null);
            Writer.Prompt = (request, token) => Session.RequestApprovalAsync(request, token);
            Shell.Prompt = (request, token) => Session.RequestApprovalAsync(request, token);
        }
        public Task Send(string type, object payload)
        {
            LastId = Guid.NewGuid().ToString("D");
            return Session.HandleAsync("https://lumen.local/index.html", JsonSerializer.Serialize(new { version = 1, id = LastId, type, payload }),
                reply =>
                {
                    using var parsed = JsonDocument.Parse(reply);
                    var clone = parsed.RootElement.Clone();
                    Replies.Enqueue(clone);
                    if (clone.GetProperty("type").GetString() == "byok.event" && clone.GetProperty("payload").TryGetProperty("kind", out var kind) && kind.GetString() == "ApprovalRequest")
                        ApprovalSignal.TrySetResult(clone);
                    return Task.CompletedTask;
                }, CancellationToken.None);
        }
        public JsonElement Last => Replies.Last();
        public string Error => Last.GetProperty("payload").GetProperty("code").GetString()!;
        public async Task Prepare()
        {
            await Send("byok.configure", new { id = Id, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = "synthetic-agent-key" });
            await Send("byok.select", new { configurationId = Id, modelId = "fixture" });
            await Send("byok.override", new { configurationId = Id, modelId = "fixture", vision = false, tools = true, reasoning = false });
            Assert.Empty(Handler.Bodies);
        }
        public Task Chat(object? extra = null)
        {
            var payload = new Dictionary<string, object> { ["configurationId"] = Id, ["text"] = "agent task", ["effort"] = "Off" };
            if (extra is not null) foreach (var property in JsonSerializer.SerializeToElement(extra).EnumerateObject()) payload[property.Name] = property.Value.Clone();
            if (!payload.ContainsKey("mode")) payload["mode"] = payload.ContainsKey("writeTools") || payload.ContainsKey("terminal") ? "Agent" : "Ask";
            return Send("byok.chat", payload);
        }
        public Task<JsonElement> WaitApproval() => ApprovalSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Dispose() { Session.Dispose(); client.Dispose(); Directory.Delete(Root, true); }
    }

    [Fact]
    public async Task TerminalApprovalTravelsOnChatCorrelationAndAllowOnceExecutesWhileGateStaysBusy()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        var chat = fixture.Chat(new { terminal = true });
        var chatId = fixture.LastId;
        var approval = await fixture.WaitApproval();
        Assert.Equal(chatId, approval.GetProperty("id").GetString());
        var payload = approval.GetProperty("payload");
        Assert.Equal("run_command", payload.GetProperty("toolName").GetString());
        Assert.Equal("ExecuteCommand", payload.GetProperty("category").GetString());
        Assert.True(payload.GetProperty("canAllowSession").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("targetPath").ValueKind);
        await fixture.Send("byok.list", new { });
        Assert.Equal("bridge_busy", fixture.Error);
        var requestId = payload.GetProperty("id").GetString();
        await fixture.Send("byok.approval.respond", new { requestId, decision = "allow_once" });
        Assert.True(fixture.Last.GetProperty("payload").GetProperty("accepted").GetBoolean());
        await chat.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
        Assert.True(fixture.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.Equal(1, fixture.Shell.Calls);
        var eventSequence = fixture.Replies.Where(reply => reply.GetProperty("type").GetString() == "byok.event")
            .Select(reply => reply.GetProperty("sequence").GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(1, eventSequence.Length), eventSequence);
        Assert.Contains(approval.GetProperty("sequence").GetInt32(), eventSequence);
        Assert.Equal(1, fixture.Reader.Captures);
        Assert.Equal(1, fixture.Shell.Captures);
        Assert.Equal(0, fixture.Writer.Captures);
        using var round = JsonDocument.Parse(fixture.Handler.Bodies[1]);
        Assert.Contains("fixture", round.RootElement.GetProperty("messages")[3].GetProperty("content").GetString());
        var advertised = JsonDocument.Parse(fixture.Handler.Bodies[0]).RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ToArray();
        Assert.Equal(11, advertised.Length);
        Assert.DoesNotContain("create_file", advertised);
        Assert.DoesNotContain("edit_file", advertised);
    }

    [Fact]
    public async Task DenyIsReinjectedAsFailedOutcomeAndChatStillCompletes()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        var chat = fixture.Chat(new { terminal = true });
        var approval = await fixture.WaitApproval();
        await fixture.Send("byok.approval.respond", new { requestId = approval.GetProperty("payload").GetProperty("id").GetString(), decision = "deny" });
        await chat.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        var id = fixture.Last.GetProperty("payload").GetProperty("conversation").GetProperty("id").GetString();
        await fixture.Send("byok.history.open", new { conversationId = id, offset = 0, limit = 1 });
        var card = Assert.Single(fixture.Last.GetProperty("payload").GetProperty("turns")[0].GetProperty("tools").EnumerateArray());
        Assert.Equal("Failed", card.GetProperty("status").GetString());
        Assert.Contains("approval_denied", card.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ForgedOrMistypedDecisionsAreRejectedAndValidDenyStillWorks()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        var chat = fixture.Chat(new { terminal = true });
        var approval = await fixture.WaitApproval();
        var requestId = approval.GetProperty("payload").GetProperty("id").GetString();
        await fixture.Send("byok.approval.respond", new { requestId = Guid.NewGuid().ToString("D"), decision = "deny" });
        Assert.Equal("invalid_message", fixture.Error);
        await fixture.Send("byok.approval.respond", new { requestId, decision = "always" });
        Assert.Equal("invalid_message", fixture.Error);
        await fixture.Send("byok.approval.respond", new { requestId = "not-a-guid", decision = "deny" });
        Assert.Equal("invalid_message", fixture.Error);
        await fixture.Send("byok.approval.respond", new { requestId });
        Assert.Equal("invalid_message", fixture.Error);
        await fixture.Send("byok.approval.respond", new { requestId, decision = "deny" });
        Assert.True(fixture.Last.GetProperty("payload").GetProperty("accepted").GetBoolean());
        await chat.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Send("byok.approval.respond", new { requestId, decision = "allow_once" });
        Assert.Equal("invalid_message", fixture.Error);
    }

    [Fact]
    public async Task WriteOutsideWorkspaceApprovalForbidsSessionDecision()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Handler.ToolName = "create_file";
        fixture.Handler.Arguments = "{\"path\":\"outside.cs\",\"content\":\"x\"}";
        fixture.Writer.RequiresApproval = true;
        var chat = fixture.Chat(new { writeTools = true });
        var approval = await fixture.WaitApproval();
        var payload = approval.GetProperty("payload");
        Assert.Equal("WriteOutsideWorkspace", payload.GetProperty("category").GetString());
        Assert.False(payload.GetProperty("canAllowSession").GetBoolean());
        Assert.Equal("C:/outside/target.cs", payload.GetProperty("targetPath").GetString());
        var requestId = payload.GetProperty("id").GetString();
        await fixture.Send("byok.approval.respond", new { requestId, decision = "allow_session" });
        Assert.Equal("invalid_message", fixture.Error);
        await fixture.Send("byok.approval.respond", new { requestId, decision = "allow_once" });
        Assert.True(fixture.Last.GetProperty("payload").GetProperty("accepted").GetBoolean());
        await chat.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.Equal(1, fixture.Writer.Calls);
    }

    [Fact]
    public async Task CancelDuringApprovalCancelsChatAndRevokesThePendingDecision()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        var chat = fixture.Chat(new { terminal = true });
        var approval = await fixture.WaitApproval();
        await fixture.Send("byok.cancel", new { });
        await chat.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", fixture.Error);
        var requestId = approval.GetProperty("payload").GetProperty("id").GetString();
        await fixture.Send("byok.approval.respond", new { requestId, decision = "allow_once" });
        Assert.Equal("invalid_message", fixture.Error);
        var histories = await new ConversationStore(Path.Combine(fixture.Root, "conversations")).ListAsync("", CancellationToken.None);
        Assert.Equal("cancelled", Assert.Single(histories).TurnStates[0]);
    }

    [Theory]
    [InlineData("writeTools", "text")]
    [InlineData("terminal", "number")]
    [InlineData("readTools", "array")]
    public async Task NonBooleanFlagsAreRejectedBeforeCaptureCredentialsOrHttp(string field, string kind)
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        var reads = fixture.Credentials.Reads;
        object value = kind == "number" ? 1 : kind == "array" ? Array.Empty<string>() : "yes";
        await fixture.Chat(new Dictionary<string, object> { [field] = value });
        Assert.Equal("invalid_message", fixture.Error);
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(0, fixture.Reader.Captures + fixture.Writer.Captures + fixture.Shell.Captures);
        Assert.Empty(fixture.Handler.Bodies);
    }

    [Fact]
    public async Task MissingHostExecutorsFailBeforeCredentialRead()
    {
        using var fixture = new Fixture(withWriters: false);
        await fixture.Prepare();
        var reads = fixture.Credentials.Reads;
        await fixture.Chat(new { writeTools = true });
        Assert.Equal("workspace_unavailable", fixture.Error);
        await fixture.Chat(new { terminal = true });
        Assert.Equal("workspace_unavailable", fixture.Error);
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Empty(fixture.Handler.Bodies);
    }

    [Theory]
    [InlineData("Ask", true, false)]
    [InlineData("Plan", true, false)]
    [InlineData("Ask", false, true)]
    [InlineData("Plan", false, true)]
    public async Task ReadOnlyModesRejectForgedWriteOrTerminalBeforeCapture(string mode, bool writeTools, bool terminal)
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        var reads = fixture.Credentials.Reads;
        await fixture.Chat(new { mode, writeTools, terminal });
        Assert.Equal("mode_denied", fixture.Error);
        Assert.Equal(0, fixture.Reader.Captures + fixture.Writer.Captures + fixture.Shell.Captures);
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Empty(fixture.Handler.Bodies);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("rounds_zero")]
    [InlineData("rounds_nine")]
    [InlineData("fractional")]
    [InlineData("tokens_low")]
    [InlineData("tokens_high")]
    [InlineData("tools_zero")]
    [InlineData("tools_high")]
    [InlineData("rounds_text")]
    public async Task InvalidBudgetIsRejectedBeforeCaptureCredentialsOrHttp(string kind)
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        var reads = fixture.Credentials.Reads;
        object budget = kind switch
        {
            "string" => "large",
            "missing" => new { maxRounds = 4 },
            "extra" => new { maxRounds = 4, maxOutputTokens = 8192, maxInvocations = 8, extra = 1 },
            "rounds_zero" => new { maxRounds = 0, maxOutputTokens = 8192, maxInvocations = 8 },
            "rounds_nine" => new { maxRounds = 9, maxOutputTokens = 8192, maxInvocations = 8 },
            "fractional" => new { maxRounds = 4.5, maxOutputTokens = 8192, maxInvocations = 8 },
            "tokens_low" => new { maxRounds = 4, maxOutputTokens = 1023, maxInvocations = 8 },
            "tokens_high" => new { maxRounds = 4, maxOutputTokens = 32769, maxInvocations = 8 },
            "tools_zero" => new { maxRounds = 4, maxOutputTokens = 8192, maxInvocations = 0 },
            "tools_high" => new { maxRounds = 4, maxOutputTokens = 8192, maxInvocations = 33 },
            _ => new { maxRounds = "4", maxOutputTokens = 8192, maxInvocations = 8 }
        };
        await fixture.Chat(new Dictionary<string, object> { ["readTools"] = true, ["budget"] = budget });
        Assert.Equal("invalid_message", fixture.Error);
        Assert.Equal(0, fixture.Reader.Captures + fixture.Writer.Captures + fixture.Shell.Captures);
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Empty(fixture.Handler.Bodies);
    }

    [Fact]
    public async Task CustomBudgetBoundsTheToolLoopAndLargerBudgetCompletes()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Handler.ToolName = "read_file";
        fixture.Handler.Arguments = "{\"path\":\"Sample.cs\"}";
        await fixture.Chat(new { readTools = true, budget = new { maxRounds = 2, maxOutputTokens = 2048, maxInvocations = 2 } });
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
        Assert.True(fixture.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.Equal(2, fixture.Handler.Bodies.Count);
        await fixture.Chat(new { readTools = true, budget = new { maxRounds = 1, maxOutputTokens = 8192, maxInvocations = 8 } });
        Assert.Equal("tool_budget_exceeded", fixture.Error);
        Assert.Equal(3, fixture.Handler.Bodies.Count);
    }

    [Theory]
    [InlineData("Ask", "edit_file")]
    [InlineData("Plan", "edit_file")]
    [InlineData("Ask", "run_command")]
    [InlineData("Plan", "run_command")]
    public async Task ReadOnlyModesRejectModelToolRequests(string mode, string tool)
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Handler.ToolName = tool;
        fixture.Handler.Arguments = tool == "edit_file" ? "{\"path\":\"Sample.cs\",\"content\":\"x\"}" : "{\"command\":\"cmd\",\"args\":[\"/c\",\"echo x\"]}";
        await fixture.Chat(new { mode, readTools = true });
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
        Assert.Equal(0, fixture.Writer.Calls + fixture.Shell.Calls);
        Assert.Contains("unknown_tool", fixture.Handler.Bodies.Last());
    }

    [Fact]
    public async Task SwitchingModesKeepsHistoryAndChangesSystemInstruction()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Handler.RequestTool = false;
        await fixture.Chat(new { mode = "Plan" });
        var id = fixture.Last.GetProperty("payload").GetProperty("conversation").GetProperty("id").GetString()!;
        using (var body = JsonDocument.Parse(fixture.Handler.Bodies[0]))
            Assert.Contains("acceptance criteria", body.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
        await fixture.Chat(new { mode = "Ask" });
        Assert.Equal(id, fixture.Last.GetProperty("payload").GetProperty("conversation").GetProperty("id").GetString());
        using (var body = JsonDocument.Parse(fixture.Handler.Bodies[1]))
        {
            var messages = body.RootElement.GetProperty("messages");
            Assert.Equal(4, messages.GetArrayLength());
            Assert.Contains("cite paths:lines", messages[0].GetProperty("content")[0].GetProperty("text").GetString());
        }
        var stored = await new ConversationStore(Path.Combine(fixture.Root, "conversations")).ReadAsync(id, CancellationToken.None);
        Assert.Equal(ChatMode.Ask, stored!.Conversation.Mode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("agent")]
    [InlineData("unknown")]
    public async Task InvalidModeNeverCallsProvider(string mode)
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        await fixture.Chat(new { mode });
        Assert.Equal("invalid_message", fixture.Error);
        Assert.Empty(fixture.Handler.Bodies);
    }

    [Fact]
    public async Task ReadOnlyConsentNeverAdvertisesWritesEvenWhenHostExecutorsExist()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Handler.RequestTool = false;
        await fixture.Chat(new { readTools = true });
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
        using var body = JsonDocument.Parse(fixture.Handler.Bodies[0]);
        var tools = body.RootElement.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ToArray();
        Assert.Equal(8, tools.Length);
        Assert.DoesNotContain("run_command", tools);
        Assert.DoesNotContain("create_file", tools);
        Assert.Equal(1, fixture.Reader.Captures);
        Assert.Equal(0, fixture.Writer.Captures + fixture.Shell.Captures);
    }

    [Fact]
    public async Task WriteTurnsBeginOnlyAfterPreflightAndEachEmptyChatGetsItsOwnTurn()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Handler.RequestTool = false;
        var reads = fixture.Credentials.Reads;
        fixture.Writer.Begin = (automatic, token) =>
        {
            Assert.True(automatic);
            Assert.True(fixture.Credentials.Reads > reads);
            Assert.Equal(fixture.Writer.Turns.Count - 1, fixture.Handler.Bodies.Count);
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await fixture.Chat(new { writeTools = true, effort = "invalid" });
        Assert.Equal("invalid_message", fixture.Error);
        Assert.Empty(fixture.Writer.Turns);
        await fixture.Chat(new { writeTools = true, text = "synthetic-agent-key" });
        Assert.Equal("invalid_response", fixture.Error);
        Assert.Empty(fixture.Writer.Turns);
        Assert.Empty(fixture.Handler.Bodies);
        await fixture.Chat(new { writeTools = true });
        await fixture.Chat(new { writeTools = true });
        Assert.Equal(new[] { true, true }, fixture.Writer.Turns);
        Assert.Equal(0, fixture.Writer.Calls);
        await fixture.Chat(new { readTools = true });
        Assert.Equal(2, fixture.Writer.Turns.Count);
    }

    [Fact]
    public async Task CancelledAutomaticBeginPreventsProviderLoopAndNextChatRecovers()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Handler.RequestTool = false;
        fixture.Writer.Begin = async (_, token) =>
        {
            await fixture.Send("byok.cancel", new { });
            token.ThrowIfCancellationRequested();
        };
        await fixture.Chat(new { writeTools = true });
        Assert.Equal("cancelled", fixture.Error);
        Assert.Empty(fixture.Handler.Bodies);
        Assert.Equal(0, fixture.Writer.Calls);
        fixture.Writer.Begin = (_, _) => Task.CompletedTask;
        await fixture.Chat(new { writeTools = true });
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
        Assert.Equal(new[] { true, true }, fixture.Writer.Turns);
    }

    [Fact]
    public async Task AgentChatWithoutTerminalStillAdvertisesOnlyReadAndWriteTools()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Handler.RequestTool = false;
        await fixture.Chat(new { writeTools = true });
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
        using var body = JsonDocument.Parse(fixture.Handler.Bodies[0]);
        var tools = body.RootElement.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ToArray();
        Assert.Equal(11, tools.Length);
        Assert.Contains("apply_diff", tools);
        Assert.DoesNotContain("run_command", tools);
        Assert.Equal(1, fixture.Writer.Captures);
        Assert.Equal(0, fixture.Shell.Captures);
    }

    private static HttpResponseMessage Sse(string data) => new(HttpStatusCode.OK) { Content = new StringContent("data: " + data + "\n\ndata: [DONE]\n\n") };
    private static HttpResponseMessage ToolCallResponse(string id, string name, string arguments) => Sse(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id, function = new { name, arguments } } } }, finish_reason = "tool_calls" } } }));
    private static bool IsNotice(JsonElement reply, string code) => reply.GetProperty("type").GetString() == "byok.event"
        && reply.GetProperty("payload").GetProperty("kind").GetString() == "Notice"
        && reply.GetProperty("payload").GetProperty("code").GetString() == code;

    [Fact]
    public async Task CompactionShrinksReplayedHistoryBeforePost()
    {
        using var fixture = new Fixture();
        fixture.Handler.ModelsJson = "{\"data\":[{\"id\":\"fixture\",\"architecture\":{\"input_modalities\":[\"text\"]},\"supported_parameters\":[\"tools\"],\"context_length\":6144}]}";
        fixture.Handler.RequestTool = false;
        await fixture.Send("byok.configure", new { id = fixture.Id, providerId = "fixture", kind = "OpenRouter", baseUrl = "https://provider.test/v1/", key = "synthetic-agent-key" });
        await fixture.Send("byok.select", new { configurationId = fixture.Id, modelId = "fixture" });
        await fixture.Send("byok.override", new { configurationId = fixture.Id, modelId = "fixture", vision = false, tools = true, reasoning = false });
        for (var index = 0; index < 20; index++) await fixture.Chat(new { text = new string('a', 200) });
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
        Assert.True(fixture.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.Equal(20, fixture.Handler.Bodies.Count);
        using var first = JsonDocument.Parse(fixture.Handler.Bodies[0]);
        using var last = JsonDocument.Parse(fixture.Handler.Bodies[19]);
        Assert.Equal(2, first.RootElement.GetProperty("messages").GetArrayLength());
        // 20 turns of 200 chars plus the new message overflow the 6144-token window; the oldest turns are dropped.
        Assert.InRange(last.RootElement.GetProperty("messages").GetArrayLength(), 3, 40);
        Assert.Contains(fixture.Replies, reply => IsNotice(reply, "history_compacted"));
    }

    [Fact]
    public async Task Transient429AfterToolRetriesWithoutReexecuting()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Handler.PostResponses.Enqueue(() => ToolCallResponse("call-retry", "read_file", "{\"path\":\"Sample.cs\"}"));
        fixture.Handler.PostResponses.Enqueue(() => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") }; response.Headers.Add("Retry-After", "0"); return response; });
        fixture.Handler.PostResponses.Enqueue(() => Sse("{\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}"));
        await fixture.Chat(new { readTools = true });
        Assert.True(fixture.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.Equal(3, fixture.Handler.Bodies.Count);
        Assert.Equal(1, fixture.Reader.Calls);
        Assert.Contains("read data", fixture.Handler.Bodies[2]);
        Assert.Contains(fixture.Replies, reply => IsNotice(reply, "retrying"));
    }

    [Fact]
    public async Task NonTransient401AfterToolDoesNotRetry()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Handler.PostResponses.Enqueue(() => ToolCallResponse("call-once", "read_file", "{\"path\":\"Sample.cs\"}"));
        fixture.Handler.PostResponses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        await fixture.Chat(new { readTools = true });
        Assert.False(fixture.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.Equal(2, fixture.Handler.Bodies.Count);
        Assert.Equal(1, fixture.Reader.Calls);
        Assert.Contains(fixture.Replies, reply => reply.GetProperty("type").GetString() == "byok.event"
            && reply.GetProperty("payload").GetProperty("kind").GetString() == "Error"
            && reply.GetProperty("payload").GetProperty("code").GetString() == "invalid_key");
        Assert.DoesNotContain(fixture.Replies, reply => reply.GetProperty("type").GetString() == "byok.event"
            && reply.GetProperty("payload").GetProperty("kind").GetString() == "Notice");
    }

    [Fact]
    public async Task MultiStepCreateEditBuildFeedsEachOutcomeBeforeTheNextRound()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Handler.PostResponses.Enqueue(() => ToolCallResponse("call-create", "create_file", "{\"path\":\"Calculator.cs\",\"content\":\"public class Calculator {}\"}"));
        fixture.Handler.PostResponses.Enqueue(() => ToolCallResponse("call-edit", "edit_file", "{\"path\":\"Fixture.csproj\",\"content\":\"<Project/>\"}"));
        fixture.Handler.PostResponses.Enqueue(() => ToolCallResponse("call-build", "run_build", "{\"path\":\"Fixture.csproj\"}"));
        fixture.Handler.PostResponses.Enqueue(() => Sse("{\"choices\":[{\"delta\":{\"content\":\"task done\"},\"finish_reason\":\"stop\"}]}"));
        var chat = fixture.Chat(new { writeTools = true, terminal = true });
        var approval = await fixture.WaitApproval();
        Assert.Equal("run_build", approval.GetProperty("payload").GetProperty("toolName").GetString());
        await fixture.Send("byok.approval.respond", new { requestId = approval.GetProperty("payload").GetProperty("id").GetString(), decision = "allow_once" });
        await chat.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
        Assert.True(fixture.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.Equal(4, fixture.Handler.Bodies.Count);
        Assert.Equal(2, fixture.Writer.Calls);
        Assert.Equal(1, fixture.Shell.Calls);
        using var final = JsonDocument.Parse(fixture.Handler.Bodies[3]);
        var outcomes = final.RootElement.GetProperty("messages").EnumerateArray().Where(message => message.GetProperty("role").GetString() == "tool").ToArray();
        Assert.Equal(new[] { "call-create", "call-edit", "call-build" }, outcomes.Select(message => message.GetProperty("tool_call_id").GetString()));
        Assert.Contains("exitCode", outcomes[2].GetProperty("content").GetString());
        var id = fixture.Last.GetProperty("payload").GetProperty("conversation").GetProperty("id").GetString();
        await fixture.Send("byok.history.open", new { conversationId = id, offset = 0, limit = 1 });
        var cards = fixture.Last.GetProperty("payload").GetProperty("turns")[0].GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(new[] { "create_file", "edit_file", "run_build" }, cards.Select(card => card.GetProperty("name").GetString()));
        Assert.All(cards, card => Assert.Equal("Completed", card.GetProperty("status").GetString()));
    }
}