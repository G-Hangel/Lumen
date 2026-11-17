using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Agents;

namespace Lumen.Tools;

public sealed class AgentLocation
{
    public AgentLocation(string path, string source, AgentScope scope) { Path = path; Source = source; Scope = scope; }
    public string Path { get; }
    public string Source { get; }
    public AgentScope Scope { get; }
}

public interface IAgentSource
{
    Task<AgentSnapshot> DiscoverAsync(CancellationToken cancellationToken);
}

// Discovers AGENT.md files in per-project and per-user folders. An agent's body
// is its instruction text; it never grants tools or approvals by itself.
public static class AgentCatalog
{
    public const int MaxAgents = 64;
    public const int MaxFileChars = 60000;
    public const int MaxDescriptionChars = 1024;

    public static IReadOnlyList<AgentLocation> Locations(string? projectRoot, string? userHome)
    {
        var locations = new List<AgentLocation>();
        if (!string.IsNullOrEmpty(projectRoot)) locations.Add(new AgentLocation(Path.Combine(projectRoot, ".lumen", "agents"), ".lumen/agents", AgentScope.Project));
        if (!string.IsNullOrEmpty(userHome)) locations.Add(new AgentLocation(Path.Combine(userHome, ".lumen", "agents"), "~/.lumen/agents", AgentScope.User));
        return locations;
    }

    public static AgentSnapshot Discover(IEnumerable<AgentLocation> locations, CancellationToken cancellationToken = default)
    {
        var agents = new List<AgentDefinition>();
        var diagnostics = new List<AgentDiagnostic>();
        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(location.Path)) continue;
            WorkspaceGuard guard;
            try { guard = new WorkspaceGuard(location.Path); }
            catch (ToolException)
            {
                diagnostics.Add(new AgentDiagnostic(location.Source, "path_unavailable", "The agents folder passes through a link or junction, so it is ignored."));
                continue;
            }
            string[] folders;
            try { folders = Directory.EnumerateDirectories(guard.Root).Take(MaxAgents + 1).OrderBy(value => value, StringComparer.Ordinal).ToArray(); }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
            {
                diagnostics.Add(new AgentDiagnostic(location.Source, "path_unavailable", "The agents folder could not be listed."));
                continue;
            }
            if (folders.Length > MaxAgents)
            {
                diagnostics.Add(new AgentDiagnostic(location.Source, "too_many_agents", "Only the first " + MaxAgents + " folders are read."));
                folders = folders.Take(MaxAgents).ToArray();
            }
            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(folder);
                var label = location.Source + "/" + name + "/AGENT.md";
                try
                {
                    string text;
                    using (var stream = guard.OpenRead(name + "\\AGENT.md")) text = SkillText.Read(stream, MaxFileChars);
                    var (description, body) = Parse(text, name);
                    if (agents.Any(agent => agent.Name == name))
                    {
                        diagnostics.Add(new AgentDiagnostic(label, "duplicate_name", "An agent with this name is already defined in a higher-precedence location."));
                        continue;
                    }
                    agents.Add(new AgentDefinition(name, description, body, location.Source, location.Scope, guard.Root + "\\" + name));
                }
                catch (AgentValidationException exception) { diagnostics.Add(new AgentDiagnostic(label, exception.Code, exception.Message)); }
                catch (ToolException) { diagnostics.Add(new AgentDiagnostic(label, "missing_agent_file", "AGENT.md is missing or unreadable.")); }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
                {
                    diagnostics.Add(new AgentDiagnostic(label, "path_unavailable", "AGENT.md could not be read."));
                }
            }
        }
        return new AgentSnapshot(agents, diagnostics);
    }

    private static (string Description, string Body) Parse(string text, string folder)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsControl(character) && character != '\n' && character != '\r' && character != '\t')
                throw new AgentValidationException("invalid_text", "AGENT.md contains a control character.");
        }
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != "---") throw new AgentValidationException("missing_frontmatter", "AGENT.md must start with '---'.");
        var end = Array.FindIndex(lines, 1, value => value.TrimEnd() == "---");
        if (end < 0) throw new AgentValidationException("unterminated_frontmatter", "AGENT.md has no closing '---' line.");
        string? name = null, description = null;
        for (var index = 1; index < end; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new AgentValidationException("invalid_field", "Frontmatter line " + (index + 1) + " is not 'key: value'.");
            var key = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim().Trim('"');
            if (key == "name") name = value;
            else if (key == "description") description = value;
            else throw new AgentValidationException("invalid_field", "Unknown field '" + key + "' in AGENT.md.");
        }
        if (!AgentRegistry.IsValidName(name ?? string.Empty)) throw new AgentValidationException("invalid_name", "Agent name must be lowercase letters, digits and single hyphens.");
        if (!string.Equals(name, folder, StringComparison.Ordinal)) throw new AgentValidationException("name_mismatch", "name '" + name + "' must match folder '" + folder + "'.");
        if (string.IsNullOrWhiteSpace(description) || description!.Length > MaxDescriptionChars)
            throw new AgentValidationException("invalid_description", "description must have 1-" + MaxDescriptionChars + " characters.");
        var body = string.Join("\n", lines.Skip(end + 1)).Trim();
        if (body.Length == 0) throw new AgentValidationException("missing_body", "AGENT.md needs instructions after the frontmatter.");
        return (description!, body);
    }
}

public sealed class AgentValidationException : Exception
{
    public AgentValidationException(string code, string message) : base(message) { Code = code; }
    public string Code { get; }
}

// A folder-defined agent returns its own instructions when delegated. It has no
// tool or approval authority of its own; the main model decides what to do with them.
public sealed class MarkdownAgent : AgentBase
{
    private readonly AgentDefinition definition;
    public MarkdownAgent(AgentDefinition definition) : base(definition.Name, definition.Scope) { this.definition = definition; }
    public AgentDefinition Definition => definition;
    public override Task<AgentResult> ExecuteAsync(string taskDescription, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        return Task.FromResult(CreateResult(definition.Body, true, new Dictionary<string, object>
        {
            ["source"] = definition.Source,
            ["task"] = taskDescription
        }));
    }
}
