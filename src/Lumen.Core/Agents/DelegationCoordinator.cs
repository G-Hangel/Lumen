using System;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Core.Agents;

// Entry point for delegating work to a subagent. This slice deliberately keeps
// delegation inside the Agent mode: Plan/Ask stay read-only, so they cannot
// spawn agents that perform work. Depth is bounded to prevent runaway chains.
public sealed class DelegationCoordinator : IDisposable
{
    public const int MaxTaskChars = 4096;
    public const int MaxDepth = 2;
    public const int MaxConcurrent = 4;
    public const int MaxResultChars = 65536;

    private readonly AgentRegistry registry;
    private readonly SemaphoreSlim slots = new(MaxConcurrent, MaxConcurrent);
    private bool disposed;

    public DelegationCoordinator(AgentRegistry registry)
    {
        this.registry = registry ?? throw new AgentException("unknown_agent");
    }

    public async Task<AgentResult> DelegateAsync(string name, string task, ChatMode mode,
        int depth = 0, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (mode != ChatMode.Agent) throw new AgentException("mode_denied");
        if (depth < 0 || depth >= MaxDepth) throw new AgentException("depth_exceeded");
        ValidateTask(task);
        var agent = registry.Require(name);
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            AgentResult? result;
            try { result = await agent.ExecuteAsync(task, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Never echo the model's or the agent's exception text to the caller.
                return Failure(agent.Name, "delegation_failed");
            }
            if (result is null || result.Text is null || result.Text.Length > MaxResultChars ||
                !string.Equals(result.AgentName, agent.Name, StringComparison.Ordinal))
                return Failure(agent.Name, "invalid_result");
            return result;
        }
        finally { slots.Release(); }
    }

    private static void ValidateTask(string task)
    {
        if (string.IsNullOrWhiteSpace(task) || task.Length > MaxTaskChars) throw new AgentException("invalid_task");
        foreach (var character in task)
            if (char.IsControl(character) && character != '\n' && character != '\r' && character != '\t')
                throw new AgentException("invalid_task");
    }

    private static AgentResult Failure(string agentName, string text) => new()
    {
        Success = false, Text = text, AgentName = agentName, GeneratedAt = DateTime.UtcNow
    };

    private void EnsureActive()
    {
        if (disposed) throw new AgentException("registry_disposed");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        slots.Dispose();
    }
}
