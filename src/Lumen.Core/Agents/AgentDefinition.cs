using System.Collections.Generic;

namespace Lumen.Core.Agents;

public sealed class AgentDefinition
{
    public AgentDefinition(string name, string description, string body, string source, AgentScope scope, string directory,
        string? providerKind = null, string? modelId = null, int? maxTokens = null, int? maxTurns = null)
    {
        Name = name; Description = description; Body = body; Source = source; Scope = scope; Directory = directory;
        ProviderKind = providerKind; ModelId = modelId; MaxTokens = maxTokens; MaxTurns = maxTurns;
    }

    public string Name { get; }
    public string Description { get; }
    public string Body { get; }
    public string Source { get; }
    public AgentScope Scope { get; }
    public string Directory { get; }
    public string? ProviderKind { get; }
    public string? ModelId { get; }
    public int? MaxTokens { get; }
    public int? MaxTurns { get; }
    public bool HasModelConfig => !string.IsNullOrEmpty(ProviderKind) && !string.IsNullOrEmpty(ModelId);
}

public sealed class AgentDiagnostic
{
    public AgentDiagnostic(string location, string code, string detail) { Location = location; Code = code; Detail = detail; }
    public string Location { get; }
    public string Code { get; }
    public string Detail { get; }
}

public sealed class AgentSnapshot
{
    public static AgentSnapshot Empty { get; } = new AgentSnapshot(new List<AgentDefinition>(), new List<AgentDiagnostic>());
    public AgentSnapshot(IReadOnlyList<AgentDefinition> agents, IReadOnlyList<AgentDiagnostic> diagnostics)
    {
        Agents = agents; Diagnostics = diagnostics;
    }
    public IReadOnlyList<AgentDefinition> Agents { get; }
    public IReadOnlyList<AgentDiagnostic> Diagnostics { get; }
}
