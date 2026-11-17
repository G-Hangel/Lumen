using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Core.Agents;

// Base class for subagents specialized in specific task domains.
// Provides common functionality for task execution, result packaging,
// and delegation control within the Lumen ecosystem.
public abstract class AgentBase : IDisposable
{
    public string Name { get; }
    public AgentScope Scope { get; }
    public bool IsDisposed { get; private set; }

    protected AgentBase(string name, AgentScope scope)
    {
        Name = name;
        Scope = scope;
    }

    public virtual void Dispose()
    {
        IsDisposed = true;
    }

    // Core method for executing a task. Subclasses must implement this.
    public abstract Task<AgentResult> ExecuteAsync(string taskDescription, CancellationToken cancellationToken = default);

    // Helper to package results consistently
    protected AgentResult CreateResult(string resultText, bool success = true, 
        Dictionary<string, object>? metadata = null)
    {
        return new AgentResult
        {
            Success = success,
            Text = resultText,
            Metadata = metadata ?? new Dictionary<string, object>(),
            AgentName = Name,
            GeneratedAt = DateTime.UtcNow
        };
    }

    // Helper to package error results
    protected AgentResult CreateErrorResult(string errorMessage, Exception? exception = null)
    {
        return new AgentResult
        {
            Success = false,
            Text = errorMessage,
            Exception = exception,
            AgentName = Name,
            GeneratedAt = DateTime.UtcNow
        };
    }

    public void EnsureNotDisposed()
    {
        if (IsDisposed) throw new ObjectDisposedException(Name);
    }
}

public class AgentResult
{
    public bool Success { get; set; }
    public string Text { get; set; } = "";
    public Dictionary<string, object>? Metadata { get; set; }
    public string AgentName { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public Exception? Exception { get; set; }
}

public enum AgentScope
{
    Project,
    User,
    Global
}