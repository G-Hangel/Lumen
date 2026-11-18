using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lumen.Core.Domain;

public enum ToolCallStatus
{
    Pending,
    AwaitingApproval,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed class ToolSpec
{
    // Providers reject names outside this shape, so the constraint is enforced here
    // instead of surfacing as a 400 from whichever backend is called.
    private static readonly Regex NamePattern = new("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ToolSpec(string name, string description, JsonElement parametersSchema)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (!NamePattern.IsMatch(name))
        {
            throw new ArgumentException("Tool names must match ^[a-zA-Z0-9_-]{1,64}$.", nameof(name));
        }

        if (parametersSchema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A tool parameter schema must be a JSON object.", nameof(parametersSchema));
        }

        Name = name;
        Description = description ?? throw new ArgumentNullException(nameof(description));
        ParametersSchema = parametersSchema;
    }

    public string Name { get; }
    public string Description { get; }
    public JsonElement ParametersSchema { get; }
}

public sealed class ToolInvocation
{
    public ToolInvocation(string id, string name, JsonElement arguments, ToolCallStatus status = ToolCallStatus.Pending)
    {
        Id = string.IsNullOrEmpty(id) ? throw new ArgumentNullException(nameof(id)) : id;
        Name = string.IsNullOrEmpty(name) ? throw new ArgumentNullException(nameof(name)) : name;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool arguments must be a JSON object.", nameof(arguments));
        }

        if (!Enum.IsDefined(typeof(ToolCallStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown tool call status.");
        }

        Arguments = arguments;
        Status = status;
    }

    public string Id { get; }
    public string Name { get; }
    public JsonElement Arguments { get; }
    public ToolCallStatus Status { get; }

    public bool IsTerminal => Status is ToolCallStatus.Completed or ToolCallStatus.Failed or ToolCallStatus.Cancelled;

    public ToolInvocation WithStatus(ToolCallStatus status) => new(Id, Name, Arguments, status);
}

public sealed class ToolResult
{
    public ToolResult(string invocationId, bool isError, string content)
    {
        InvocationId = string.IsNullOrEmpty(invocationId) ? throw new ArgumentNullException(nameof(invocationId)) : invocationId;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        IsError = isError;
    }

    public string InvocationId { get; }
    public bool IsError { get; }
    public string Content { get; }

    public ToolResultPart ToContentPart() => new(InvocationId, IsError, Content);
}

public sealed class ToolBudget
{
    public static ToolBudget Default { get; } = new(64, 262144, 256);

    public ToolBudget(int maxRounds, int maxOutputTokens, int maxInvocations)
    {
        if (maxRounds < 1 || maxRounds > 64) throw new ArgumentOutOfRangeException(nameof(maxRounds));
        if (maxOutputTokens < 1024 || maxOutputTokens > 262144) throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        if (maxInvocations < 1 || maxInvocations > 256) throw new ArgumentOutOfRangeException(nameof(maxInvocations));
        MaxRounds = maxRounds;
        MaxOutputTokens = maxOutputTokens;
        MaxInvocations = maxInvocations;
    }

    public int MaxRounds { get; }
    public int MaxOutputTokens { get; }
    public int MaxInvocations { get; }
}
