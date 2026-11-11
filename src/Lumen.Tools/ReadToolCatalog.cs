using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Lumen.Core.Domain;

namespace Lumen.Tools;

public static class ReadToolCatalog
{
    public static IReadOnlyList<ToolSpec> Specs { get; } = Array.AsReadOnly(new[]
    {
        Spec("read_file", "Read UTF-8 or UTF-16 text from a workspace file, optionally by one-based line range.",
            "{\"path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":4096},\"startLine\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000000},\"endLine\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000000}}", "[\"path\"]"),
        Spec("list_dir", "List immediate workspace directory entries. Results are bounded and report truncation.",
            PathAndLimit, "[\"path\"]"),
        Spec("find_files", "Find workspace files with a relative glob (*, ?, **). Results are bounded and report truncation.",
            PathAndLimit.Substring(0, PathAndLimit.Length - 1) + ",\"glob\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256}}", "[\"path\",\"glob\"]"),
        Spec("grep", "Search workspace text files for a literal, case-sensitive string, not a regular expression.",
            PathAndLimit.Substring(0, PathAndLimit.Length - 1) + ",\"pattern\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":1024},\"glob\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256}}", "[\"path\",\"pattern\"]")
    });

    private const string PathAndLimit = "{\"path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":4096},\"maxResults\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":256}}";

    private static ToolSpec Spec(string name, string description, string properties, string required)
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":" + properties + ",\"required\":" + required + "}");
        return new ToolSpec(name, description, document.RootElement.Clone());
    }

    internal static JsonElement Validate(ToolInvocation invocation)
    {
        var spec = Specs.FirstOrDefault(item => item.Name == invocation.Name) ?? throw new ToolException("unknown_tool");
        if (invocation.Status != ToolCallStatus.Pending) throw new ToolException("invalid_arguments");
        var arguments = invocation.Arguments;
        if (arguments.ValueKind != JsonValueKind.Object) throw new ToolException("invalid_arguments");
        var properties = spec.ParametersSchema.GetProperty("properties");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in arguments.EnumerateObject())
        {
            if (!seen.Add(field.Name) || !properties.TryGetProperty(field.Name, out var constraint)) throw new ToolException("invalid_arguments");
            if (constraint.GetProperty("type").GetString() == "string")
            {
                if (field.Value.ValueKind != JsonValueKind.String) throw new ToolException("invalid_arguments");
                var value = field.Value.GetString()!;
                if (value.Length < constraint.GetProperty("minLength").GetInt32() || value.Length > constraint.GetProperty("maxLength").GetInt32())
                    throw new ToolException("invalid_arguments");
                ValidateText(value);
            }
            else if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var number) || number < constraint.GetProperty("minimum").GetInt32() || number > constraint.GetProperty("maximum").GetInt32())
                throw new ToolException("invalid_arguments");
        }
        foreach (var required in spec.ParametersSchema.GetProperty("required").EnumerateArray())
            if (!seen.Contains(required.GetString()!)) throw new ToolException("invalid_arguments");
        if (invocation.Name == "read_file" && arguments.TryGetProperty("endLine", out var end) &&
            end.GetInt32() < (arguments.TryGetProperty("startLine", out var start) ? start.GetInt32() : 1))
            throw new ToolException("invalid_arguments");
        if (arguments.TryGetProperty("glob", out var glob)) ValidateGlob(glob.GetString()!);
        return arguments.Clone();
    }

    internal static void ValidateText(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\0') throw new ToolException("invalid_arguments");
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index == text.Length || !char.IsLowSurrogate(text[index])) throw new ToolException("invalid_arguments");
            }
            else if (char.IsLowSurrogate(text[index])) throw new ToolException("invalid_arguments");
        }
    }

    private static void ValidateGlob(string glob)
    {
        if (glob.IndexOf(':') >= 0 || glob.StartsWith("/", StringComparison.Ordinal) || glob.StartsWith("\\", StringComparison.Ordinal))
            throw new ToolException("invalid_arguments");
        foreach (var segment in glob.Replace('\\', '/').Split('/'))
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal))
                throw new ToolException("invalid_arguments");
        foreach (var character in glob)
            if (char.IsControl(character) || character == '"' || character == '<' || character == '>' || character == '|')
                throw new ToolException("invalid_arguments");
    }
}
