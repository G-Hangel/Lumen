using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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

public sealed class AutomaticReadToolSessionTests
{
    private sealed class Credentials : ICredentialStore
    {
        public int Reads;
        public Task<string?> ReadAsync(string id, CancellationToken token) { Reads++; return Task.FromResult<string?>("synthetic-auto-key"); }
        public Task WriteAsync(string id, string key, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();
        public bool RequestTool = true;
        public string Arguments = "{\"path\":\"Sample.cs\"}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"fixture\"}]}") };
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            var data = RequestTool && Bodies.Count % 2 == 1
                ? JsonSerializer.Serialize(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "call" + Bodies.Count, function = new { name = "read_file", arguments = Arguments } } } }, finish_reason = "tool_calls" } } })
                : "{\"choices\":[{\"delta\":{\"content\":\"Sample.cs:1 actual disk\"},\"finish_reason\":\"stop\"}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: " + data + "\n\ndata: [DONE]\n\n") };
        }
    }
    private sealed class Executor : IReadToolExecutor, IApprovalPrompt, IDisposable
    {
        private readonly ApprovalBroker broker;
        private readonly WorkspaceReadTools tools;
        public int Captures;
        public int Calls;
        public bool CaptureFails;
        public Func<ToolInvocation, CancellationToken, Task<ToolResult>>? Override;
        public Executor(string root)
        {
            var guard = new WorkspaceGuard(root);
            broker = new ApprovalBroker(guard, this);
            tools = new WorkspaceReadTools(guard, broker);
        }
        public IReadOnlyList<ToolSpec> Specs => ReadToolCatalog.Specs;
        public Task<IReadToolExecutor> CaptureAsync(CancellationToken token) { Captures++; if (CaptureFails) throw new ProviderException("workspace_unavailable"); return Task.FromResult<IReadToolExecutor>(this); }
        public Task<ToolResult> ExecuteAsync(ToolInvocation call, CancellationToken token) { Calls++; return Override is null ? tools.ExecuteAsync(ChatMode.Ask, call, token) : Override(call, token); }
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken token) => throw new InvalidOperationException("No approvals in readonly loop");
        public void Dispose() => broker.Dispose();
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lumen-auto-" + Guid.NewGuid().ToString("N"));
        public string Id { get; } = Guid.NewGuid().ToString("D");
        public Handler Handler { get; } = new();
        public Credentials Credentials { get; } = new();
        public Executor Executor { get; }
        private readonly HttpClient client;
        private readonly ProviderFactory factory;
        public ByokSession Session { get; private set; }
        public readonly ConcurrentQueue<JsonElement> Replies = new();
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "Sample.cs"), "actual disk content\n");
            Executor = new Executor(Root);
            client = new HttpClient(Handler);
            factory = new ProviderFactory(new HttpTransport(client), new CapabilityResolver());
            Session = Create();
        }
        private ByokSession Create() => new(factory, Credentials, new SettingsStore(Path.Combine(Root, "providers.json")), readTools: Executor);
        public void Restart() { Session.Dispose(); Session = Create(); }
        public Task Send(string type, object payload) => Session.HandleAsync("https://lumen.local/index.html", JsonSerializer.Serialize(new { version = 1, id = Guid.NewGuid().ToString("D"), type, payload }),
            reply => { using var parsed = JsonDocument.Parse(reply); Replies.Enqueue(parsed.RootElement.Clone()); return Task.CompletedTask; }, CancellationToken.None);
        public JsonElement Last => Replies.Last();
        public async Task Prepare(bool tools = true)
        {
            await Send("byok.configure", new { id = Id, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = "synthetic-auto-key" });
            await Send("byok.select", new { configurationId = Id, modelId = "fixture" });
            if (tools) await Send("byok.override", new { configurationId = Id, modelId = "fixture", vision = false, tools = true, reasoning = false });
            Assert.Empty(Handler.Bodies);
        }
        public Task Chat(bool? enabled = true) => enabled.HasValue ? Send("byok.chat", new { configurationId = Id, text = "read Sample.cs", effort = "Off", readTools = enabled.Value }) : Send("byok.chat", new { configurationId = Id, text = "read Sample.cs", effort = "Off" });
        public string Error => Last.GetProperty("payload").GetProperty("code").GetString()!;
        public void Dispose() { Session.Dispose(); Executor.Dispose(); client.Dispose(); Directory.Delete(Root, true); }
    }

    [Fact]
    public async Task AutomaticInvocationArchivesPairsAndReopenExpandsProviderReplayWithOutcomeCards()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        await fixture.Chat();
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
        Assert.True(fixture.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.Equal(2, fixture.Handler.Bodies.Count);
        Assert.Equal(1, fixture.Executor.Calls);
        Assert.Equal(1, fixture.Executor.Captures);
        var id = fixture.Last.GetProperty("payload").GetProperty("conversation").GetProperty("id").GetString();
        var stored = await new ConversationStore(Path.Combine(fixture.Root, "conversations")).ReadAsync(id!, CancellationToken.None);
        Assert.Equal(2, stored.Conversation.Messages.Count);
        Assert.Single(stored.Conversation.Messages[1].Parts.OfType<ToolResultPart>());
        using (var round = JsonDocument.Parse(fixture.Handler.Bodies[1]))
        {
            var messages = round.RootElement.GetProperty("messages");
            Assert.Equal(new[] { "system", "user", "assistant", "tool" }, messages.EnumerateArray().Select(message => message.GetProperty("role").GetString()));
            Assert.Contains("actual disk content", messages[3].GetProperty("content").GetString());
        }
        fixture.Restart();
        await fixture.Send("byok.history.open", new { conversationId = id, offset = 0, limit = 10 });
        var card = Assert.Single(fixture.Last.GetProperty("payload").GetProperty("turns")[0].GetProperty("tools").EnumerateArray());
        Assert.Equal("Completed", card.GetProperty("status").GetString());
        Assert.False(card.GetProperty("isError").GetBoolean());
        Assert.Contains("actual disk content", card.GetProperty("content").GetString());
        await fixture.Send("byok.discover", new { configurationId = fixture.Id });
        fixture.Handler.RequestTool = false;
        await fixture.Chat(false);
        using var replay = JsonDocument.Parse(fixture.Handler.Bodies.Last());
        Assert.Equal(new[] { "system", "user", "assistant", "tool", "assistant", "user" }, replay.RootElement.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("role").GetString()));
        Assert.False(replay.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task ExplicitOptoutDoesNotCaptureOrExecuteOrAdvertise()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        await fixture.Chat(false);
        Assert.Single(fixture.Handler.Bodies);
        Assert.Equal(0, fixture.Executor.Captures);
        Assert.Equal(0, fixture.Executor.Calls);
        using var body = JsonDocument.Parse(fixture.Handler.Bodies[0]);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task DefaultAskCapturesAndAdvertisesReadTools()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        await fixture.Chat(null);
        Assert.Equal(2, fixture.Handler.Bodies.Count);
        Assert.Equal(1, fixture.Executor.Captures);
        Assert.Equal(1, fixture.Executor.Calls);
        using var body = JsonDocument.Parse(fixture.Handler.Bodies[0]);
        Assert.True(body.RootElement.TryGetProperty("tools", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedModelOrCaptureFailureStopsBeforeCredentialsAndHttp(bool captureFailure)
    {
        using var fixture = new Fixture();
        await fixture.Prepare(captureFailure);
        fixture.Executor.CaptureFails = captureFailure;
        var reads = fixture.Credentials.Reads;
        await fixture.Chat();
        Assert.Equal(captureFailure ? "workspace_unavailable" : "tools_not_supported", fixture.Error);
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Empty(fixture.Handler.Bodies);
        fixture.Executor.CaptureFails = false;
        fixture.Handler.RequestTool = false;
        await fixture.Chat(false);
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
    }

    [Fact]
    public async Task OversizedCompletedHistoryIsCompactedBeforePaidRequest()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        var id = Guid.NewGuid().ToString("D");
        var domain = new Conversation(id, ChatMode.Ask, new[] { new Message(MessageRole.User, new ContentPart[] { new TextPart("previous") }),
            new Message(MessageRole.Assistant, new ContentPart[] { new TextPart(new string('x', 262145)) }) }, modelId: "fixture");
        await new ConversationStore(Path.Combine(fixture.Root, "conversations")).SaveAsync(new StoredConversation(fixture.Id, domain, new[] { "completed" }), CancellationToken.None);
        await fixture.Send("byok.history.open", new { conversationId = id, offset = 0, limit = 1 });
        await fixture.Chat();
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
        Assert.True(fixture.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.Equal(2, fixture.Handler.Bodies.Count);
        using var body = JsonDocument.Parse(fixture.Handler.Bodies[0]);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("read Sample.cs", messages[1].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.DoesNotContain("xxxx", fixture.Handler.Bodies[0]);
        Assert.Contains(fixture.Replies, reply => reply.GetProperty("type").GetString() == "byok.event"
            && reply.GetProperty("payload").GetProperty("kind").GetString() == "Notice"
            && reply.GetProperty("payload").GetProperty("code").GetString() == "history_compacted");
    }

    [Fact]
    public async Task OversizedCurrentInputIsRejectedBeforeCredentialReadOrPaidRequest()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        var reads = fixture.Credentials.Reads;
        await fixture.Send("byok.chat", new { configurationId = fixture.Id, text = "read Sample.cs", effort = "Off", readTools = true,
            image = new { mediaType = "image/png", base64Data = new string('A', 300000) } });
        Assert.Equal("context_too_large", fixture.Error);
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Empty(fixture.Handler.Bodies);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Fact]
    public async Task RejectedTraversalIsArchivedAsFailedToolOutcomeAndNotExecuted()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Handler.Arguments = "{\"path\":\"../../outside.cs\"}";
        await fixture.Chat();
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
        var id = fixture.Last.GetProperty("payload").GetProperty("conversation").GetProperty("id").GetString();
        await fixture.Send("byok.history.open", new { conversationId = id, offset = 0, limit = 1 });
        var card = Assert.Single(fixture.Last.GetProperty("payload").GetProperty("turns")[0].GetProperty("tools").EnumerateArray());
        Assert.Equal("Failed", card.GetProperty("status").GetString());
        Assert.True(card.GetProperty("isError").GetBoolean());
        Assert.Contains("invalid_arguments", card.GetProperty("content").GetString());
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Fact]
    public async Task CancelDuringToolPersistsCancelledPairAndGateRecoversWithoutReplayingIt()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executor.Override = async (call, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new ToolResult(call.Id, false, "late"); };
        var chat = fixture.Chat();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Send("byok.cancel", new { });
        await chat.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("cancelled", fixture.Error);
        Assert.Contains(fixture.Replies, reply => reply.GetProperty("type").GetString() == "byok.event" && reply.GetProperty("payload").TryGetProperty("status", out var status) && status.GetString() == "Cancelled");
        var histories = await new ConversationStore(Path.Combine(fixture.Root, "conversations")).ListAsync("", CancellationToken.None);
        Assert.Equal("cancelled", Assert.Single(histories).TurnStates[0]);
        fixture.Executor.Override = null;
        fixture.Handler.RequestTool = false;
        await fixture.Chat();
        Assert.Equal("byok.chat.result", fixture.Last.GetProperty("type").GetString());
        using var retry = JsonDocument.Parse(fixture.Handler.Bodies.Last());
        Assert.Equal(2, retry.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal(1, fixture.Executor.Calls);
    }
}
