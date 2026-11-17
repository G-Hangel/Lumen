using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Agents;
using Lumen.Core.Domain;
using Lumen.Tools;
using Xunit;

namespace Lumen.Tests;

public sealed class AgentCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lumen-agent-catalog-" + Guid.NewGuid().ToString("N"));

    public AgentCatalogTests()
    {
        Directory.CreateDirectory(root);
    }

    public void Dispose() => Directory.Delete(root, true);

    private static string Agent(string name, string description = "Help with a task.", string body = "Follow these steps.") =>
        "---\nname: " + name + "\ndescription: " + description + "\n---\n\n" + body + "\n";

    private string Write(string parent, string name, string content)
    {
        var folder = Path.Combine(parent, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "AGENT.md"), content);
        return folder;
    }

    [Fact]
    public void DiscoversAgentsAndReportsActionableDiagnostics()
    {
        var project = Path.Combine(root, "project");
        var user = Path.Combine(root, "user");
        var agents = Path.Combine(project, ".lumen", "agents");
        Write(agents, "review-helper", Agent("review-helper", "Reviews code.", "Read the diff first."));
        Write(agents, "bad-name", Agent("wrong-name"));
        Directory.CreateDirectory(Path.Combine(agents, "no-frontmatter"));
        File.WriteAllText(Path.Combine(agents, "no-frontmatter", "AGENT.md"), "not an agent");
        var userAgents = Path.Combine(user, ".lumen", "agents");
        Write(userAgents, "review-helper", Agent("review-helper", "User copy.", "User body."));
        Write(userAgents, "extra-helper", Agent("extra-helper", "User only.", "User body."));

        var snapshot = AgentCatalog.Discover(new[] { new AgentLocation(agents, ".lumen/agents", AgentScope.Project), new AgentLocation(userAgents, "~/.lumen/agents", AgentScope.User) });
        Assert.Equal(new[] { "review-helper", "extra-helper" }, snapshot.Agents.Select(agent => agent.Name));
        Assert.Equal("Reviews code.", snapshot.Agents[0].Description);
        Assert.Equal(AgentScope.Project, snapshot.Agents[0].Scope);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Location.Contains("bad-name") && diagnostic.Code == "name_mismatch");
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Location.Contains("no-frontmatter") && diagnostic.Code == "missing_frontmatter");
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Location.Contains("review-helper") && diagnostic.Code == "duplicate_name");
    }

    [Fact]
    public async Task DiscoveredAgentCanBeRegisteredAndDelegated()
    {
        var agents = Path.Combine(root, ".lumen", "agents");
        Write(agents, "review-helper", Agent("review-helper", "Reviews code.", "Read the diff first."));
        var snapshot = AgentCatalog.Discover(new[] { new AgentLocation(agents, ".lumen/agents", AgentScope.Project) });
        using var registry = new AgentRegistry();
        using var coordinator = new DelegationCoordinator(registry);
        registry.Replace(snapshot.Agents.Select(agent => (AgentBase)new MarkdownAgent(agent)));
        var result = await coordinator.DelegateAsync("review-helper", "check this", ChatMode.Agent);
        Assert.True(result.Success);
        Assert.Equal("Read the diff first.", result.Text);
        Assert.Equal(".lumen/agents", result.Metadata!["source"]);
    }

}
