using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lumen.Core.Agents;

public sealed class AgentInfo
{
    public AgentInfo(string name, AgentScope scope) { Name = name; Scope = scope; }
    public string Name { get; }
    public AgentScope Scope { get; }
}

public sealed class AgentException : Exception
{
    public AgentException(string code) : base(code) { Code = code; }
    public string Code { get; }
}

// Holds the subagents available to a session. Names are validated because they
// cross the bridge and appear in prompts/results, so they must not be free-form.
public sealed class AgentRegistry : IDisposable
{
    private static readonly Regex NamePattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    public const int MaxNameLength = 64;

    public static bool IsValidName(string name) => !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && NamePattern.IsMatch(name);
    private readonly Dictionary<string, AgentBase> agents = new(StringComparer.Ordinal);
    private bool disposed;

    public int Count { get { lock (agents) return agents.Count; } }

    public void Register(AgentBase agent)
    {
        EnsureActive();
        if (agent is null) throw new AgentException("unknown_agent");
        var name = agent.Name;
        if (!IsValidName(name))
            throw new AgentException("invalid_agent_name");
        lock (agents)
        {
            if (agents.ContainsKey(name)) throw new AgentException("duplicate_agent");
            agents.Add(name, agent);
        }
    }

    public bool TryGet(string name, out AgentBase agent)
    {
        EnsureActive();
        if (string.IsNullOrEmpty(name)) { agent = null!; return false; }
        lock (agents) return agents.TryGetValue(name, out agent!);
    }

    public AgentBase Require(string name) =>
        TryGet(name, out var agent) ? agent : throw new AgentException("unknown_agent");

    public IReadOnlyList<string> Names()
    {
        EnsureActive();
        lock (agents) return new List<string>(agents.Keys).AsReadOnly();
    }

    public IReadOnlyList<AgentInfo> List()
    {
        EnsureActive();
        lock (agents) return agents.Values.Select(agent => new AgentInfo(agent.Name, agent.Scope)).ToArray();
    }

    // Swaps the full agent set atomically. Previous agents are disposed so a
    // folder rename cannot leave a stale agent delegated by the model.
    public void Replace(IEnumerable<AgentBase> nextAgents)
    {
        EnsureActive();
        var next = new List<AgentBase>(nextAgents ?? throw new AgentException("unknown_agent"));
        List<AgentBase> previous;
        lock (agents)
        {
            previous = new List<AgentBase>(agents.Values);
            agents.Clear();
            foreach (var agent in next)
            {
                if (!IsValidName(agent.Name)) throw new AgentException("invalid_agent_name");
                if (agents.ContainsKey(agent.Name)) throw new AgentException("duplicate_agent");
                agents.Add(agent.Name, agent);
            }
        }
        foreach (var agent in previous) agent.Dispose();
    }

    private void EnsureActive()
    {
        if (disposed) throw new AgentException("registry_disposed");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        List<AgentBase> snapshot;
        lock (agents)
        {
            snapshot = new List<AgentBase>(agents.Values);
            agents.Clear();
        }
        foreach (var agent in snapshot) agent.Dispose();
    }
}
