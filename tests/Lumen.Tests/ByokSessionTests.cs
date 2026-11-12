using System;
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
using Xunit;

namespace Lumen.Tests;

public sealed class ByokSessionTests
{
    private const string Secret = "synthetic-session-key-836eca";
    private sealed class Credentials : ICredentialStore
    {
        public readonly Dictionary<string, string> Values = new();
        public Task<string?> ReadAsync(string id, CancellationToken token) => Task.FromResult(Values.TryGetValue(id, out var value) ? value : null);
        public Task WriteAsync(string id, string secret, CancellationToken token) { Values[id] = secret; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken token) { Values.Remove(id); return Task.CompletedTask; }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Chats;
        public int Lists;
        public int Status = 200;
        public string Catalog = "{\"data\":[{\"id\":\"text-only-fixture\"}]}";
        public int Probes;
        public int ProbeStatus = 200;
        public string ProbeResponse = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}";
        public string Stream = "data: {\"choices\":[{\"delta\":{\"content\":\"real transport fixture\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        public readonly List<(Uri Uri, string? Credential)> Requests = new();
        public readonly List<string> ChatBodies = new();
        public Func<CancellationToken, Task>? BeforeList;
        public Func<CancellationToken, Task>? BeforeProbe;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.Parameter));
            if (request.Method == HttpMethod.Get)
            {
                Lists++;
                if (BeforeList is not null) await BeforeList(cancellationToken);
                return new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(Status == 200 ? Catalog : Secret) };
            }
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var parsed = JsonDocument.Parse(body);
            if (parsed.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.False)
            {
                Probes++;
                if (BeforeProbe is not null) await BeforeProbe(cancellationToken);
                return new HttpResponseMessage((HttpStatusCode)ProbeStatus) { Content = new StringContent(ProbeResponse) };
            }
            Chats++;
            ChatBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Stream) };
        }
    }

    [Fact]
    public async Task FullNativeBridgeFlowPersistsOnlyMetadataAndChatRequiresExplicitRequest()
    {
        using var owned = new Folder();
        var path = Path.Combine(owned.Path, "providers.json");
        var credentials = new Credentials();
        var handler = new Handler();
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), credentials, new SettingsStore(path));
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }
        var configurationId = Guid.NewGuid().ToString("D");
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret }), Send, CancellationToken.None);
        Assert.Equal(0, handler.Chats);
        Assert.Equal(1, handler.Lists);
        Assert.Equal(Secret, credentials.Values[configurationId]);
        AssertSecretAbsent(replies.Concat(new[] { await File.ReadAllTextAsync(path) }));
        using (var result = JsonDocument.Parse(replies.Last())) Assert.True(result.RootElement.GetProperty("payload").GetProperty("credentialValid").GetBoolean());
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "text-only-fixture" }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.chat", new { configurationId, text = "hi", effort = "Off" }), Send, CancellationToken.None);
        Assert.Equal(1, handler.Chats);
        Assert.Contains(replies, json => json.Contains("byok.event", StringComparison.Ordinal) && json.Contains("real transport fixture", StringComparison.Ordinal));
        Assert.Contains(replies, json => json.Contains("byok.chat.result", StringComparison.Ordinal));
        AssertSecretAbsent(replies.Concat(new[] { await File.ReadAllTextAsync(path) }));
        var reread = await new SettingsStore(path).ReadAsync(CancellationToken.None);
        Assert.Equal("text-only-fixture", Assert.Single(reread).SelectedModelId);
    }

    [Theory]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(429, true)]
    public async Task InvalidKeyAndUrlAreNotSavedButRateLimitedKeyIsAccepted(int status, bool stored)
    {
        using var owned = new Folder();
        var credentials = new Credentials();
        var handler = new Handler { Status = status };
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client, (_, _) => Task.CompletedTask), new CapabilityResolver()), credentials, new SettingsStore(Path.Combine(owned.Path, "providers.json")));
        var replies = new List<string>();
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = Guid.NewGuid().ToString("D"), providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret }), json => { replies.Add(json); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(stored, credentials.Values.Count > 0);
        Assert.Equal(0, handler.Chats);
        AssertSecretAbsent(replies);
        foreach (var file in Directory.GetFiles(owned.Path)) AssertSecretAbsent(new[] { await File.ReadAllTextAsync(file) });
    }

    [Fact]
    public async Task OriginInvalidPayloadReplayAndUnknownRequestsCannotInvokeProvider()
    {
        using var owned = new Folder();
        var handler = new Handler();
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), new Credentials(), new SettingsStore(Path.Combine(owned.Path, "providers.json")));
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }
        var configure = Request("byok.configure", new { id = Guid.NewGuid().ToString("D"), providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret });
        await session.HandleAsync("https://attacker.test/index.html", configure, Send, CancellationToken.None);
        Assert.Empty(replies);
        Assert.Equal(0, handler.Lists);
        foreach (var bad in new[] { Request("byok.configure", new { key = Secret }), Request("byok.future", new { key = Secret }), "{\"version\":1,\"type\":\"byok.list\",\"id\":\"" + Guid.NewGuid().ToString("D") + "\",\"payload\":{},\"payload\":{}}" })
        {
            await session.HandleAsync("https://lumen.local/index.html", bad, Send, CancellationToken.None);
            using var reply = JsonDocument.Parse(replies.Last());
            Assert.Equal("error", reply.RootElement.GetProperty("type").GetString());
        }
        Assert.Equal(0, handler.Lists);
        await session.HandleAsync("https://lumen.local/index.html", configure, Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", configure, Send, CancellationToken.None);
        Assert.Equal(1, handler.Lists);
        AssertSecretAbsent(replies);
    }

    [Fact]
    public void OpaqueToolSignatureSurvivesConversationSerialization()
    {
        using var arguments = JsonDocument.Parse("{\"path\":\"a.cs\"}");
        var conversation = new Conversation("id", ChatMode.Ask, new[] { new Message(MessageRole.Assistant, new ContentPart[] { new ToolCallPart("call", "read_file", arguments.RootElement, "opaque-signed-part") }) });
        Assert.True(ConversationSerializer.TryParse(ConversationSerializer.ToJson(conversation), out var parsed, out _));
        Assert.Equal("opaque-signed-part", Assert.IsType<ToolCallPart>(parsed!.Messages[0].Parts[0]).ProviderState);
    }

    [Fact]
    public async Task ExistingCredentialCannotFollowConfigurationToAnotherOrigin()
    {
        using var owned = new Folder();
        var credentials = new Credentials();
        var handler = new Handler();
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), credentials, new SettingsStore(Path.Combine(owned.Path, "providers.json")));
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }
        var configurationId = Guid.NewGuid().ToString("D");
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://other.test/v1/", key = "" }), Send, CancellationToken.None);
        Assert.Contains("invalid_configuration", replies.Last());
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.discover", new { configurationId }), Send, CancellationToken.None);
        Assert.All(handler.Requests, request => Assert.Equal("provider.test", request.Uri.Host));
        Assert.Equal(Secret, credentials.Values[configurationId]);
    }

    [Fact]
    public async Task KeylessReplacementDeletesCredentialRatherThanRetainingIt()
    {
        using var owned = new Folder();
        var credentials = new Credentials();
        var handler = new Handler();
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), credentials, new SettingsStore(Path.Combine(owned.Path, "providers.json")));
        var configurationId = Guid.NewGuid().ToString("D");
        Task Send(string _) => Task.CompletedTask;
        foreach (var key in new[] { Secret, "" })
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key }), Send, CancellationToken.None);
        Assert.Empty(credentials.Values);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.discover", new { configurationId }), Send, CancellationToken.None);
        Assert.Null(handler.Requests.Last().Credential);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    public async Task ProviderCannotExposeOrPersistCredentialInModelMetadata(string field)
    {
        using var owned = new Folder();
        var path = Path.Combine(owned.Path, "providers.json");
        var credentials = new Credentials();
        var handler = new Handler { Catalog = field == "id" ? JsonSerializer.Serialize(new { data = new[] { new { id = Secret } } }) : JsonSerializer.Serialize(new { data = new[] { new { id = "fixture/model", name = Secret } } }) };
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), credentials, new SettingsStore(path));
        var replies = new List<string>();
        var configurationId = Guid.NewGuid().ToString("D");
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenRouter", baseUrl = "https://provider.test/v1/", key = Secret }), json => { replies.Add(json); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Contains("invalid_response", replies.Last());
        AssertSecretAbsent(replies);
        Assert.Empty(credentials.Values);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelStopsConfigureAndDiscoverBeforePersistence(bool rediscover)
    {
        using var owned = new Folder();
        var path = Path.Combine(owned.Path, "providers.json");
        var credentials = new Credentials();
        var handler = new Handler();
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), credentials, new SettingsStore(path));
        var replies = new List<string>();
        Task Send(string json) { lock (replies) replies.Add(json); return Task.CompletedTask; }
        var configurationId = Guid.NewGuid().ToString("D");
        var configure = Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret });
        if (rediscover) await session.HandleAsync("https://lumen.local/index.html", configure, Send, CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        handler.BeforeList = async token => { using var registration = token.Register(() => cancelled = true); entered.SetResult(); await released.Task; };
        var operation = session.HandleAsync("https://lumen.local/index.html", rediscover ? Request("byok.discover", new { configurationId }) : configure, Send, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.cancel", new { }), Send, CancellationToken.None);
        released.SetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled);
        Assert.Contains(replies, reply => reply.Contains("\"code\":\"cancelled\"", StringComparison.Ordinal));
        if (!rediscover) { Assert.Empty(credentials.Values); Assert.False(File.Exists(path)); }
    }

    [Fact]
    public async Task PublicOpenRouterCatalogCanBeSavedWithoutClaimingCredentialValidity()
    {
        using var owned = new Folder();
        var path = Path.Combine(owned.Path, "providers.json");
        var credentials = new Credentials();
        var handler = new Handler();
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), credentials, new SettingsStore(path));
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }
        var configurationId = Guid.NewGuid().ToString("D");
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenRouter", baseUrl = "https://provider.test/v1/", key = Secret }), Send, CancellationToken.None);
        using var result = JsonDocument.Parse(replies.Last());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("payload").GetProperty("credentialValid").ValueKind);
        Assert.Equal(Secret, credentials.Values[configurationId]);
        Assert.Null(handler.Requests.Single().Credential);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "text-only-fixture" }), Send, CancellationToken.None);
        Assert.Contains("byok.select.result", replies.Last());
        AssertSecretAbsent(replies.Concat(new[] { await File.ReadAllTextAsync(path) }));
    }

    [Fact]
    public async Task SeparateOpaqueReasoningBlocksSurviveNextTurnReplay()
    {
        using var owned = new Folder();
        var handler = new Handler { Stream = "data: {\"choices\":[{\"delta\":{\"reasoning_details\":[{\"type\":\"reasoning.encrypted\",\"index\":0,\"data\":\"opaque-a\"}]}}]}\n\ndata: {\"choices\":[{\"delta\":{\"reasoning_details\":[{\"type\":\"reasoning.encrypted\",\"index\":1,\"data\":\"opaque-b\"}]},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n" };
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), new Credentials(), new SettingsStore(Path.Combine(owned.Path, "providers.json")));
        var configurationId = Guid.NewGuid().ToString("D");
        Task Send(string _) => Task.CompletedTask;
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenRouter", baseUrl = "https://provider.test/v1/", key = Secret }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "text-only-fixture" }), Send, CancellationToken.None);
        for (var turn = 0; turn < 2; turn++) await session.HandleAsync("https://lumen.local/index.html", Request("byok.chat", new { configurationId, text = "hi", effort = "Off" }), Send, CancellationToken.None);
        using var replay = JsonDocument.Parse(handler.ChatBodies.Last());
        var details = replay.RootElement.GetProperty("messages")[2].GetProperty("reasoning_details");
        Assert.Equal(2, details.GetArrayLength());
        Assert.Equal("opaque-a", details[0].GetProperty("data").GetString());
        Assert.Equal("opaque-b", details[1].GetProperty("data").GetString());
    }

    [Fact]
    public async Task AdjacentSignedAnthropicBlocksRemainSeparateInReplay()
    {
        using var owned = new Folder();
        var handler = new Handler
        {
            Catalog = "{\"data\":[{\"id\":\"text-only-fixture\",\"max_input_tokens\":10000,\"max_tokens\":4096}],\"has_more\":false}",
            Stream = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\nevent: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"first\",\"signature\":\"signed-a\"}}\n\nevent: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\nevent: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"second\",\"signature\":\"signed-b\"}}\n\nevent: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
        };
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), new Credentials(), new SettingsStore(Path.Combine(owned.Path, "providers.json")));
        var configurationId = Guid.NewGuid().ToString("D");
        Task Send(string _) => Task.CompletedTask;
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "Anthropic", baseUrl = "https://provider.test/v1/", key = Secret }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "text-only-fixture" }), Send, CancellationToken.None);
        for (var turn = 0; turn < 2; turn++) await session.HandleAsync("https://lumen.local/index.html", Request("byok.chat", new { configurationId, text = "hi", effort = "Off" }), Send, CancellationToken.None);
        using var replay = JsonDocument.Parse(handler.ChatBodies.Last());
        var content = replay.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("first", content[0].GetProperty("thinking").GetString());
        Assert.Equal("signed-a", content[0].GetProperty("signature").GetString());
        Assert.Equal("second", content[1].GetProperty("thinking").GetString());
        Assert.Equal("signed-b", content[1].GetProperty("signature").GetString());
    }

    [Fact]
    public async Task PrematureEofDoesNotCommitIncompleteAssistantHistory()
    {
        using var owned = new Folder();
        var handler = new Handler { Stream = "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n" };
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), new Credentials(), new SettingsStore(Path.Combine(owned.Path, "providers.json")));
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }
        var configurationId = Guid.NewGuid().ToString("D");
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "text-only-fixture" }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.chat", new { configurationId, text = "hi", effort = "Off" }), Send, CancellationToken.None);
        Assert.Contains("\"completed\":false", replies.Last());
        Assert.Contains(replies, reply => reply.Contains("partial", StringComparison.Ordinal));
        Assert.Contains(replies, reply => reply.Contains("invalid_stream", StringComparison.Ordinal));
        handler.Stream = "data: {\"choices\":[{\"delta\":{\"content\":\"complete\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.chat", new { configurationId, text = "retry", effort = "Off" }), Send, CancellationToken.None);
        using var retry = JsonDocument.Parse(handler.ChatBodies.Last());
        Assert.Equal(2, retry.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task ProbeRequiresConsentSelectedModelAndNeverRunsDuringDiscovery()
    {
        using var owned = new Folder();
        var handler = new Handler();
        using var client = new HttpClient(handler);
        var transport = new HttpTransport(client);
        using var session = new ByokSession(new ProviderFactory(transport, new CapabilityResolver()), new Credentials(), new SettingsStore(Path.Combine(owned.Path, "providers.json")), new CapabilityHttpProbe(transport));
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }
        var configurationId = Guid.NewGuid().ToString("D");
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret }), Send, CancellationToken.None);
        Assert.Equal(0, handler.Probes);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.probe", new { configurationId, capability = "Tools", consent = true }), Send, CancellationToken.None);
        Assert.Contains("model_not_found", replies.Last());
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "text-only-fixture" }), Send, CancellationToken.None);
        foreach (var invalid in new[] { Request("byok.probe", new { configurationId, capability = "Tools", consent = false }), Request("byok.probe", new { configurationId, capability = "Reasoning", consent = true }), Request("byok.probe", new { configurationId, capability = "Tools", consent = true, modelId = "other" }) })
        {
            await session.HandleAsync("https://lumen.local/index.html", invalid, Send, CancellationToken.None);
            Assert.Contains("\"type\":\"error\"", replies.Last());
        }
        Assert.Equal(0, handler.Probes);
        var valid = new { configurationId, capability = "Tools", consent = true };
        for (var repeat = 0; repeat < 2; repeat++) await session.HandleAsync("https://lumen.local/index.html", Request("byok.probe", valid), Send, CancellationToken.None);
        Assert.Equal(1, handler.Probes);
        using var result = JsonDocument.Parse(replies.Last());
        var payload = result.RootElement.GetProperty("payload");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("supported").ValueKind);
        Assert.False(payload.GetProperty("model").GetProperty("tools").GetBoolean());
        Assert.DoesNotContain(payload.GetProperty("model").GetProperty("origins").EnumerateArray(), origin => origin.GetProperty("source").GetString() == "Probe");
        Assert.Equal(4, payload.GetProperty("remainingBudget").GetInt32());
        AssertSecretAbsent(replies.Concat(Directory.GetFiles(owned.Path).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task SessionProbeBudgetCannotExceedFiveExplicitRequests()
    {
        using var owned = new Folder();
        var handler = new Handler();
        using var client = new HttpClient(handler);
        var transport = new HttpTransport(client);
        using var session = new ByokSession(new ProviderFactory(transport, new CapabilityResolver()), new Credentials(), new SettingsStore(Path.Combine(owned.Path, "providers.json")), new CapabilityHttpProbe(transport));
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }
        for (var provider = 0; provider < 6; provider++)
        {
            var configurationId = Guid.NewGuid().ToString("D");
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret }), Send, CancellationToken.None);
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "text-only-fixture" }), Send, CancellationToken.None);
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.probe", new { configurationId, capability = "Vision", consent = true }), Send, CancellationToken.None);
        }
        Assert.Equal(5, handler.Probes);
        using var last = JsonDocument.Parse(replies.Last());
        Assert.Equal(0, last.RootElement.GetProperty("payload").GetProperty("remainingBudget").GetInt32());
        Assert.Equal(JsonValueKind.Null, last.RootElement.GetProperty("payload").GetProperty("supported").ValueKind);
    }

    [Fact]
    public async Task NegativeProbeCacheSurvivesRestartWithoutSendingAnotherRequest()
    {
        using var owned = new Folder();
        var path = Path.Combine(owned.Path, "providers.json");
        var handler = new Handler { ProbeStatus = 400, ProbeResponse = "{\"error\":{\"type\":\"invalid_request_error\",\"param\":\"tools\",\"message\":\"This model does not support tools.\"}}" };
        using var client = new HttpClient(handler);
        var transport = new HttpTransport(client);
        var credentials = new Credentials();
        var configurationId = Guid.NewGuid().ToString("D");
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }
        using (var session = new ByokSession(new ProviderFactory(transport, new CapabilityResolver()), credentials, new SettingsStore(path), new CapabilityHttpProbe(transport)))
        {
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret }), Send, CancellationToken.None);
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "text-only-fixture" }), Send, CancellationToken.None);
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.probe", new { configurationId, capability = "Tools", consent = true }), Send, CancellationToken.None);
        }
        Assert.Equal(1, handler.Probes);
        using var restarted = new ByokSession(new ProviderFactory(transport, new CapabilityResolver()), credentials, new SettingsStore(path), new CapabilityHttpProbe(transport));
        await restarted.HandleAsync("https://lumen.local/index.html", Request("byok.discover", new { configurationId }), Send, CancellationToken.None);
        await restarted.HandleAsync("https://lumen.local/index.html", Request("byok.probe", new { configurationId, capability = "Tools", consent = true }), Send, CancellationToken.None);
        Assert.Equal(1, handler.Probes);
        using var result = JsonDocument.Parse(replies.Last());
        Assert.False(result.RootElement.GetProperty("payload").GetProperty("supported").GetBoolean());
        Assert.False(result.RootElement.GetProperty("payload").GetProperty("model").GetProperty("tools").GetBoolean());
        Assert.Equal(5, result.RootElement.GetProperty("payload").GetProperty("remainingBudget").GetInt32());
        AssertSecretAbsent(replies.Concat(Directory.GetFiles(owned.Path).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task ConfigurationGuidCasingCannotAliasCredentialAtAnotherOrigin()
    {
        using var owned = new Folder();
        var credentials = new Credentials();
        var handler = new Handler();
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), credentials, new SettingsStore(Path.Combine(owned.Path, "providers.json")));
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }
        var configurationId = "9aa56cda-b369-41dc-a641-43cc7090eac9";
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId.ToUpperInvariant(), providerId = "fixture", kind = "OpenAI", baseUrl = "https://other.test/v1/", key = "replacement" }), Send, CancellationToken.None);
        Assert.Contains("invalid_configuration", replies.Last());
        Assert.Single(credentials.Values);
        Assert.Equal(Secret, credentials.Values[configurationId]);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.discover", new { configurationId = configurationId.ToUpperInvariant() }), Send, CancellationToken.None);
        Assert.Contains("byok.discover.result", replies.Last());
        Assert.All(handler.Requests, request => Assert.Equal("provider.test", request.Uri.Host));
    }

    [Fact]
    public async Task PaidRequestReplayRemainsBlockedAfterIdCapacityAndCasingChange()
    {
        using var owned = new Folder();
        var handler = new Handler();
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), new Credentials(), new SettingsStore(Path.Combine(owned.Path, "providers.json")));
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }
        var configurationId = Guid.NewGuid().ToString("D");
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "text-only-fixture" }), Send, CancellationToken.None);
        var id = "9aa56cda-b369-41dc-a641-43cc7090eac9";
        var payload = new { configurationId, text = "hi", effort = "Off" };
        var chat = JsonSerializer.Serialize(new { version = 1, id, type = "byok.chat", payload });
        await session.HandleAsync("https://lumen.local/index.html", chat, Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", JsonSerializer.Serialize(new { version = 1, id = id.ToUpperInvariant(), type = "byok.chat", payload }), Send, CancellationToken.None);
        Assert.Equal(1, handler.Chats);
        for (var index = 0; index < 1025; index++) await session.HandleAsync("https://lumen.local/index.html", Request("byok.list", new { }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", chat, Send, CancellationToken.None);
        Assert.Equal(1, handler.Chats);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.chat", payload), Send, CancellationToken.None);
        Assert.Equal(1, handler.Chats);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.cancel", new { }), Send, CancellationToken.None);
        Assert.Contains("byok.cancel.result", replies.Last());
    }

    [Fact]
    public async Task RemovedConfigurationCannotTransferPermanentProbeToAnotherEndpoint()
    {
        using var owned = new Folder();
        var path = Path.Combine(owned.Path, "providers.json");
        var credentials = new Credentials();
        var handler = new Handler { ProbeStatus = 400, ProbeResponse = "{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"This model does not support tools.\"}}" };
        using var client = new HttpClient(handler);
        var transport = new HttpTransport(client);
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }
        var configurationId = Guid.NewGuid().ToString("D");
        using (var session = new ByokSession(new ProviderFactory(transport, new CapabilityResolver()), credentials, new SettingsStore(path), new CapabilityHttpProbe(transport)))
        {
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret }), Send, CancellationToken.None);
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "text-only-fixture" }), Send, CancellationToken.None);
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.probe", new { configurationId, capability = "Tools", consent = true }), Send, CancellationToken.None);
            Assert.Equal(1, handler.Probes);
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.remove", new { configurationId }), Send, CancellationToken.None);
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://other.test/v1/", key = Secret }), Send, CancellationToken.None);
            Assert.DoesNotContain("Probe", replies.Last());
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "text-only-fixture" }), Send, CancellationToken.None);
            await session.HandleAsync("https://lumen.local/index.html", Request("byok.probe", new { configurationId, capability = "Tools", consent = true }), Send, CancellationToken.None);
            using var reply = JsonDocument.Parse(replies.Last());
            Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("payload").GetProperty("supported").ValueKind);
            Assert.Equal(4, reply.RootElement.GetProperty("payload").GetProperty("remainingBudget").GetInt32());
            Assert.Equal(1, handler.Probes);
        }
        handler.ProbeStatus = 200;
        using var restarted = new ByokSession(new ProviderFactory(transport, new CapabilityResolver()), credentials, new SettingsStore(path), new CapabilityHttpProbe(transport));
        await restarted.HandleAsync("https://lumen.local/index.html", Request("byok.discover", new { configurationId }), Send, CancellationToken.None);
        Assert.DoesNotContain("Probe", replies.Last());
        await restarted.HandleAsync("https://lumen.local/index.html", Request("byok.probe", new { configurationId, capability = "Tools", consent = true }), Send, CancellationToken.None);
        Assert.Equal(2, handler.Probes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeminiUnsignedThenSignedPartsReplayExactlyOnce(bool thought)
    {
        using var owned = new Folder();
        var first = JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = "A", thought } } } } } });
        var second = JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = "B", thought, thoughtSignature = "signed-b" } } }, finishReason = "STOP" } } });
        var handler = new Handler { Catalog = "{\"models\":[{\"name\":\"models/fixture\",\"supportedGenerationMethods\":[\"generateContent\"]}]}", Stream = "data: " + first + "\n\ndata: " + second + "\n\n" };
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), new Credentials(), new SettingsStore(Path.Combine(owned.Path, "providers.json")));
        Task Send(string _) => Task.CompletedTask;
        var configurationId = Guid.NewGuid().ToString("D");
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "Gemini", baseUrl = "https://provider.test/", key = Secret }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "fixture" }), Send, CancellationToken.None);
        for (var turn = 0; turn < 2; turn++) await session.HandleAsync("https://lumen.local/index.html", Request("byok.chat", new { configurationId, text = "hi", effort = "Off" }), Send, CancellationToken.None);
        Assert.Equal(2, handler.ChatBodies.Count);
        using var replay = JsonDocument.Parse(handler.ChatBodies.Last());
        var parts = replay.RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("A", parts[0].GetProperty("text").GetString());
        Assert.Equal("B", parts[1].GetProperty("text").GetString());
        Assert.Equal("signed-b", parts[1].GetProperty("thoughtSignature").GetString());
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("Gemini")]
    public async Task NativePrematureEofCannotCommitAssistantHistory(string kind)
    {
        using var owned = new Folder();
        var handler = new Handler
        {
            Catalog = kind == "Anthropic" ? "{\"data\":[{\"id\":\"fixture\"}],\"has_more\":false}" : "{\"models\":[{\"name\":\"models/fixture\",\"supportedGenerationMethods\":[\"generateContent\"]}]}",
            Stream = kind == "Anthropic"
                ? "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"partial\"}}\n\nevent: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                : "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"partial\"}]}}]}\n\n"
        };
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), new Credentials(), new SettingsStore(Path.Combine(owned.Path, "providers.json")));
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }
        var configurationId = Guid.NewGuid().ToString("D");
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind, baseUrl = "https://provider.test/", key = Secret }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "fixture" }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.chat", new { configurationId, text = "hi", effort = "Off" }), Send, CancellationToken.None);
        Assert.Contains("\"completed\":false", replies.Last());
        Assert.Contains(replies, reply => reply.Contains("invalid_stream", StringComparison.Ordinal));
        Assert.Contains(replies, reply => reply.Contains("partial", StringComparison.Ordinal));
        handler.Stream += kind == "Anthropic" ? "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n" : "data: {\"candidates\":[{\"finishReason\":\"STOP\"}]}\n\n";
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.chat", new { configurationId, text = "retry", effort = "Off" }), Send, CancellationToken.None);
        Assert.Contains("\"completed\":true", replies.Last());
        using var retry = JsonDocument.Parse(handler.ChatBodies.Last());
        Assert.Equal(1, retry.RootElement.GetProperty(kind == "Anthropic" ? "messages" : "contents").GetArrayLength());
    }

    [Fact]
    public async Task StoredConfigurationAliasesAreRejectedBeforeCredentialsAreRead()
    {
        using var owned = new Folder();
        var path = Path.Combine(owned.Path, "providers.json");
        var id = "9aa56cda-b369-41dc-a641-43cc7090eac9";
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[]
        {
            new { id, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/" },
            new { id = id.ToUpperInvariant(), providerId = "fixture", kind = "OpenAI", baseUrl = "https://other.test/" }
        }));
        await Assert.ThrowsAsync<ProviderException>(() => new SettingsStore(path).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancelledProbeStillConsumesBudgetAndCannotRetryPaidPost()
    {
        using var owned = new Folder();
        var handler = new Handler();
        using var client = new HttpClient(handler);
        var transport = new HttpTransport(client);
        using var session = new ByokSession(new ProviderFactory(transport, new CapabilityResolver()), new Credentials(), new SettingsStore(Path.Combine(owned.Path, "providers.json")), new CapabilityHttpProbe(transport));
        var replies = new List<string>();
        Task Send(string json) { lock (replies) replies.Add(json); return Task.CompletedTask; }
        var configurationId = Guid.NewGuid().ToString("D");
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new { id = configurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/", key = Secret }), Send, CancellationToken.None);
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.select", new { configurationId, modelId = "text-only-fixture" }), Send, CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeProbe = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        var operation = session.HandleAsync("https://lumen.local/index.html", Request("byok.probe", new { configurationId, capability = "Vision", consent = true }), Send, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.cancel", new { }), Send, CancellationToken.None);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("cancelled", replies.Last());
        handler.BeforeProbe = null;
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.probe", new { configurationId, capability = "Vision", consent = true }), Send, CancellationToken.None);
        Assert.Equal(1, handler.Probes);
        using var reply = JsonDocument.Parse(replies.Last());
        Assert.Equal(4, reply.RootElement.GetProperty("payload").GetProperty("remainingBudget").GetInt32());
        Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("payload").GetProperty("supported").ValueKind);
    }

    private static string Request(string type, object payload) => JsonSerializer.Serialize(new { version = 1, id = Guid.NewGuid().ToString("D"), type, payload });
    private static void AssertSecretAbsent(IEnumerable<string> values)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Assert.DoesNotContain(Secret, "mutated log " + Secret));
        foreach (var value in values) Assert.DoesNotContain(Secret, value);
    }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lumen.ByokTests." + Guid.NewGuid().ToString("N"));
        public Folder() { Directory.CreateDirectory(Path); }
        public void Dispose() { Directory.Delete(Path, true); }
    }
}
