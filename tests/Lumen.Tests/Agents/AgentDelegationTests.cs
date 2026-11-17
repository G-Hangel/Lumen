using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Lumen.Core.Agents;
using Lumen.Core.Domain;

namespace Lumen.Tests;

public sealed class AgentDelegationTests
{
    private sealed class ScriptedAgent : AgentBase
    {
        private readonly Func<string, AgentResult?> execute;
        public ScriptedAgent(string name, Func<string, AgentResult?>? execute = null, AgentScope scope = AgentScope.Project)
            : base(name, scope) { this.execute = execute ?? (task => CreateResult("ok:" + task)); }

        public override Task<AgentResult> ExecuteAsync(string taskDescription, CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            return Task.FromResult(execute(taskDescription)!);
        }

        public AgentResult Succeed(string text) => CreateResult(text);
        public AgentResult Fail(string text) => CreateErrorResult(text);
    }

    [Fact]
    public async Task RegistryRegistersAndDelegatesToAnAgent()
    {
        using var registry = new AgentRegistry();
        using var coordinator = new DelegationCoordinator(registry);
        using var agent = new ScriptedAgent("review-helper");
        registry.Register(agent);
        Assert.Equal(1, registry.Count);
        Assert.Equal(new[] { "review-helper" }, registry.Names());
        var result = await coordinator.DelegateAsync("review-helper", "review the diff", ChatMode.Agent);
        Assert.True(result.Success);
        Assert.Equal("ok:review the diff", result.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("-start")]
    [InlineData("end-")]
    [InlineData("two--dashes")]
    public void RegistryRejectsInvalidAgentNames(string name)
    {
        using var registry = new AgentRegistry();
        using var agent = new ScriptedAgent(name);
        var error = Assert.Throws<AgentException>(() => registry.Register(agent));
        Assert.Equal("invalid_agent_name", error.Code);
    }

    [Fact]
    public void RegistryRejectsDuplicatesAndUnknownLookups()
    {
        using var registry = new AgentRegistry();
        using var first = new ScriptedAgent("helper");
        using var second = new ScriptedAgent("helper");
        registry.Register(first);
        var error = Assert.Throws<AgentException>(() => registry.Register(second));
        Assert.Equal("duplicate_agent", error.Code);
        Assert.Equal("unknown_agent", Assert.Throws<AgentException>(() => registry.Require("missing")).Code);
        Assert.False(registry.TryGet("missing", out _));
    }

    [Fact]
    public async Task DelegationRejectsWrongModeDepthAndTasks()
    {
        using var registry = new AgentRegistry();
        using var coordinator = new DelegationCoordinator(registry);
        using var agent = new ScriptedAgent("helper");
        registry.Register(agent);

        Assert.Equal("mode_denied", (await Assert.ThrowsAsync<AgentException>(() =>
            coordinator.DelegateAsync("helper", "task", ChatMode.Plan))).Code);
        Assert.Equal("mode_denied", (await Assert.ThrowsAsync<AgentException>(() =>
            coordinator.DelegateAsync("helper", "task", ChatMode.Ask))).Code);
        Assert.Equal("depth_exceeded", (await Assert.ThrowsAsync<AgentException>(() =>
            coordinator.DelegateAsync("helper", "task", ChatMode.Agent, DelegationCoordinator.MaxDepth))).Code);
        Assert.Equal("unknown_agent", (await Assert.ThrowsAsync<AgentException>(() =>
            coordinator.DelegateAsync("missing", "task", ChatMode.Agent))).Code);
        foreach (var bad in new[] { "", "   ", new string('x', DelegationCoordinator.MaxTaskChars + 1), "a\u0000b" })
            Assert.Equal("invalid_task", (await Assert.ThrowsAsync<AgentException>(() =>
                coordinator.DelegateAsync("helper", bad, ChatMode.Agent))).Code);
    }

    [Fact]
    public async Task DelegationConvertsAgentFailuresAndMalformedResults()
    {
        using var registry = new AgentRegistry();
        using var coordinator = new DelegationCoordinator(registry);
        using var failing = new ScriptedAgent("failing", task => throw new InvalidOperationException("secret detail"));
        using var empty = new ScriptedAgent("empty", task => null);
        using var wrongName = new ScriptedAgent("wrong-name", task => new AgentResult { Success = true, Text = "x", AgentName = "someone-else" });
        using var oversized = new ScriptedAgent("oversized", task => new AgentResult { Success = true, Text = new string('x', DelegationCoordinator.MaxResultChars + 1), AgentName = "oversized" });
        foreach (var agent in new[] { failing, empty, wrongName, oversized }) registry.Register(agent);

        foreach (var name in new[] { "failing", "empty", "wrong-name", "oversized" })
        {
            var result = await coordinator.DelegateAsync(name, "task", ChatMode.Agent);
            Assert.False(result.Success);
            Assert.DoesNotContain("secret detail", result.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DisposedRegistryAndCoordinatorRejectWork()
    {
        var registry = new AgentRegistry();
        using var agent = new ScriptedAgent("helper");
        var coordinator = new DelegationCoordinator(registry);
        registry.Register(agent);
        coordinator.Dispose();
        Assert.Equal("registry_disposed", (await Assert.ThrowsAsync<AgentException>(() =>
            coordinator.DelegateAsync("helper", "task", ChatMode.Agent))).Code);
        registry.Dispose();
        Assert.True(agent.IsDisposed);
        Assert.Equal("registry_disposed", Assert.Throws<AgentException>(() => registry.Names()).Code);
        Assert.Equal("registry_disposed", Assert.Throws<AgentException>(() => registry.Register(agent)).Code);
    }
}
