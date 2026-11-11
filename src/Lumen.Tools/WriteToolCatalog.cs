using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Lumen.Core.Domain;

namespace Lumen.Tools;

public static class WriteToolCatalog
{
    public static IReadOnlyList<ToolSpec> Specs { get; } = Array.AsReadOnly(new[]
    {
        Spec("create_file", "Create a new workspace text file with full UTF-8 content. Fails if the file already exists.",
            ContentProperties, "[\"path\",\"content\"]"),
        Spec("edit_file", "Replace a workspace text file's full content with UTF-8 content; returns a bounded line diff for display.",
            ContentProperties, "[\"path\",\"content\"]")
    });

    private const string ContentProperties =
        "{\"path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":4096}," +
        "\"content\":{\"type\":\"string\",\"minLength\":0,\"maxLength\":1048576}}";

    private static ToolSpec Spec(string name, string description, string properties, string required)
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":" + properties + ",\"required\":" + required + "}");
        return new ToolSpec(name, description, document.RootElement.Clone());
    }

    // net48 and netstandard resolve different JsonElement identities, so callers receive only strings.
    public static (string Path, string Content) Validate(ToolInvocation invocation)
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
            if (constraint.GetProperty("type").GetString() != "string" || field.Value.ValueKind != JsonValueKind.String)
                throw new ToolException("invalid_arguments");
            var value = field.Value.GetString()!;
            if (value.Length < constraint.GetProperty("minLength").GetInt32() || value.Length > constraint.GetProperty("maxLength").GetInt32())
                throw new ToolException("invalid_arguments");
            ReadToolCatalog.ValidateText(value);
        }
        foreach (var required in spec.ParametersSchema.GetProperty("required").EnumerateArray())
            if (!seen.Contains(required.GetString()!)) throw new ToolException("invalid_arguments");
        return (arguments.GetProperty("path").GetString()!, arguments.GetProperty("content").GetString()!);
    }
}
