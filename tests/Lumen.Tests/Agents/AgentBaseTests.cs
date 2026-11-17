using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Lumen.Core.Agents;

namespace Lumen.Tests;

public sealed class AgentBaseTests
{
    private sealed class EchoAgent : AgentBase
    {
        public EchoAgent() : base("echo", AgentScope.Project) { }

        public override Task<AgentResult> ExecuteAsync(string taskDescription, CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            return Task.FromResult(CreateResult("echo:" + taskDescription, true, new Dictionary<string, object> { ["length"] = taskDescription.Length }));
        }

        public AgentResult Fail() => CreateErrorResult("failed");
    }

    [Fact]
    public async Task AgentExecutesTaskAndPackagesResult()
    {
        using var agent = new EchoAgent();
        Assert.Equal("echo", agent.Name);
        Assert.Equal(AgentScope.Project, agent.Scope);
        var result = await agent.ExecuteAsync("hello");
        Assert.True(result.Success);
        Assert.Equal("echo:hello", result.Text);
        Assert.Equal("echo", result.AgentName);
        Assert.Equal(5, result.Metadata!["length"]);
        Assert.True(result.GeneratedAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task AgentRejectsWorkAfterDisposal()
    {
        var agent = new EchoAgent();
        agent.Dispose();
        Assert.True(agent.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => agent.ExecuteAsync("hello"));
    }

    [Fact]
    public void AgentPackagesErrorsWithoutThrowing()
    {
        using var agent = new EchoAgent();
        var result = agent.Fail();
        Assert.False(result.Success);
        Assert.Equal("failed", result.Text);
        Assert.Equal("echo", result.AgentName);
    }
}
