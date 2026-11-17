using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Agents;
using Lumen.Core.Domain;
using Lumen.Providers;
using Lumen.Tools;
using Xunit;

namespace Lumen.Tests;

public sealed class AgentToolTests
{
    private sealed class FakeRead : IReadToolExecutor
    {
        public IReadOnlyList<ToolSpec> Specs => ReadToolCatalog.Specs;
        public Task<IReadToolExecutor> CaptureAsync(CancellationToken token) => Task.FromResult<IReadToolExecutor>(this);
        public Task<ToolResult> ExecuteAsync(ToolInvocation call, CancellationToken token) =>
            Task.FromResult(new ToolResult(call.Id, false, "read"));
    }

    private sealed class EchoAgent : AgentBase
    {
        public EchoAgent() : base("helper", AgentScope.Project) { }
        public override Task<AgentResult> ExecuteAsync(string task, CancellationToken token = default) =>
            Task.FromResult(CreateResult("done:" + task));
    }

    private static ToolInvocation Call(string name, object arguments) => new(
        Guid.NewGuid().ToString("D"), name, JsonDocument.Parse(JsonSerializer.Serialize(arguments)).RootElement.Clone());

    [Fact]
    public async Task DelegateAgentIsAdvertisedOnlyWhenAgentsExist()
    {
        using var registry = new AgentRegistry();
        using var coordinator = new DelegationCoordinator(registry);
        var inner = new FakeRead();
        var executor = new AgentToolExecutor(inner, registry, coordinator);
        Assert.DoesNotContain(executor.Specs, spec => spec.Name == AgentToolCatalog.ToolName);
        Assert.DoesNotContain(ToolChatLoop.SpecsFor(ChatMode.Agent, executor), spec => spec.Name == AgentToolCatalog.ToolName);

        using var agent = new EchoAgent();
        registry.Register(agent);
        Assert.Contains(executor.Specs, spec => spec.Name == AgentToolCatalog.ToolName);
        var specs = ToolChatLoop.SpecsFor(ChatMode.Agent, executor);
        Assert.Contains(specs, spec => spec.Name == AgentToolCatalog.ToolName);
        Assert.DoesNotContain(ToolChatLoop.SpecsFor(ChatMode.Plan, executor), spec => spec.Name == AgentToolCatalog.ToolName);
        Assert.DoesNotContain(ToolChatLoop.SpecsFor(ChatMode.Ask, executor), spec => spec.Name == AgentToolCatalog.ToolName);

        var result = await executor.ExecuteAsync(Call(AgentToolCatalog.ToolName, new { name = "helper", task = "check this" }), CancellationToken.None);
        Assert.False(result.IsError);
        using var payload = JsonDocument.Parse(result.Content);
        Assert.Equal("helper", payload.RootElement.GetProperty("name").GetString());
        Assert.True(payload.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("done:check this", payload.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("{\"name\":\"helper\"}", "invalid_arguments")]
    [InlineData("{\"name\":1,\"task\":\"x\"}", "invalid_arguments")]
    [InlineData("{\"name\":\"helper\",\"task\":\"x\",\"extra\":1}", "invalid_arguments")]
    [InlineData("{\"name\":\"missing\",\"task\":\"x\"}", "unknown_agent")]
    [InlineData("{\"name\":\"helper\",\"task\":\"\"}", "invalid_task")]
    public async Task MalformedDelegationCallsAreRejected(string json, string code)
    {
        using var registry = new AgentRegistry();
        using var coordinator = new DelegationCoordinator(registry);
        using var agent = new EchoAgent();
        registry.Register(agent);
        var executor = new AgentToolExecutor(new FakeRead(), registry, coordinator);
        var invocation = new ToolInvocation(Guid.NewGuid().ToString("D"), AgentToolCatalog.ToolName, JsonDocument.Parse(json).RootElement.Clone());
        var result = await executor.ExecuteAsync(invocation, CancellationToken.None);
        Assert.True(result.IsError);
        using var payload = JsonDocument.Parse(result.Content);
        Assert.Equal(code, payload.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AgentListRpcIsAvailableWithoutAProvider()
    {
        using var registry = new AgentRegistry();
        using var coordinator = new DelegationCoordinator(registry);
        using var agent = new EchoAgent();
        registry.Register(agent);
        using var client = new HttpClient(new StubHandler());
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()),
            new Credentials(), new SettingsStore(Path.Combine(Path.GetTempPath(), "lumen-agent-rpc-" + Guid.NewGuid().ToString("N"), "providers.json")),
            agentRegistry: registry, agentCoordinator: coordinator, agentSource: new FakeSource());
        JsonElement? reply = null;
        await session.HandleAsync("https://lumen.local/index.html", JsonSerializer.Serialize(new
        {
            version = 1, id = Guid.NewGuid().ToString("D"), type = "byok.agents.list", payload = new { }
        }), message =>
        {
            using var parsed = JsonDocument.Parse(message);
            reply = parsed.RootElement.Clone();
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal("byok.agents.list.result", reply!.Value.GetProperty("type").GetString());
        var listed = reply.Value.GetProperty("payload").GetProperty("agents").EnumerateArray().ToArray();
        Assert.Single(listed);
        Assert.Equal("helper", listed[0].GetProperty("name").GetString());
        Assert.Equal("Project", listed[0].GetProperty("scope").GetString());
    }

    private sealed class Credentials : ICredentialStore
    {
        public Task<string?> ReadAsync(string id, CancellationToken token) => Task.FromResult<string?>(null);
        public Task WriteAsync(string id, string key, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class FakeSource : IAgentSource
    {
        public Task<AgentSnapshot> DiscoverAsync(CancellationToken token) => Task.FromResult(new AgentSnapshot(
            new[] { new AgentDefinition("helper", "Help", "Instructions.", ".lumen/agents", AgentScope.Project, "C:/agents/helper") },
            Array.Empty<AgentDiagnostic>()));
    }

    [Fact]
    public async Task DelegateRpcRequiresAgentModeAndReturnsPlainText()
    {
        using var registry = new AgentRegistry();
        using var coordinator = new DelegationCoordinator(registry);
        using var client = new HttpClient(new StubHandler());
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()),
            new Credentials(), new SettingsStore(Path.Combine(Path.GetTempPath(), "lumen-agent-delegate-" + Guid.NewGuid().ToString("N"), "providers.json")),
            agentRegistry: registry, agentCoordinator: coordinator, agentSource: new FakeSource());
        async Task<JsonElement> Send(string type, object payload)
        {
            JsonElement? reply = null;
            await session.HandleAsync("https://lumen.local/index.html", JsonSerializer.Serialize(new
            { version = 1, id = Guid.NewGuid().ToString("D"), type, payload }), message =>
            {
                using var parsed = JsonDocument.Parse(message);
                reply = parsed.RootElement.Clone();
                return Task.CompletedTask;
            }, CancellationToken.None);
            return reply!.Value;
        }
        var denied = await Send("byok.agents.delegate", new { name = "helper", task = "work", mode = "Plan" });
        Assert.Equal("error", denied.GetProperty("type").GetString());
        Assert.Equal("mode_denied", denied.GetProperty("payload").GetProperty("code").GetString());
        var ok = await Send("byok.agents.delegate", new { name = "helper", task = "work", mode = "Agent" });
        Assert.Equal("byok.agents.delegate.result", ok.GetProperty("type").GetString());
        var payload = ok.GetProperty("payload");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("Instructions.", payload.GetProperty("text").GetString());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") });
    }
}
