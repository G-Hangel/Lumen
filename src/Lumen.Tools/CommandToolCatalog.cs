using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Lumen.Core.Domain;

namespace Lumen.Tools;

public static class CommandToolCatalog
{
    public static IReadOnlyList<ToolSpec> Specs { get; } = Array.AsReadOnly(new[]
    {
        Spec("run_command", "Execute a command with an explicit executable and argument list. Captures stdout, stderr and exit code. Every command requires user confirmation.",
            "{\"command\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":4096}," +
            "\"args\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":256}," +
            "\"cwd\":{\"type\":\"string\",\"minLength\":0,\"maxLength\":4096}," +
            "\"timeout_ms\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":60000}}",
            "[\"command\",\"args\"]")
    });

    private static ToolSpec Spec(string name, string description, string properties, string required)
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":" + properties + ",\"required\":" + required + "}");
        return new ToolSpec(name, description, document.RootElement.Clone());
    }

    public static (string command, IReadOnlyList<string> args, string? cwd, int timeoutMs) Validate(ToolInvocation invocation)
    {
        var spec = Specs.FirstOrDefault(item => item.Name == invocation.Name) ?? throw new ToolException("unknown_tool");
        if (invocation.Status != ToolCallStatus.Pending) throw new ToolException("invalid_arguments");
        var arguments = invocation.Arguments;
        if (arguments.ValueKind != JsonValueKind.Object) throw new ToolException("invalid_arguments");
        var properties = spec.ParametersSchema.GetProperty("properties");
        var seen = new HashSet<string>(StringComparer.Ordinal);

        string? command = null;
        List<string>? args = null;
        string? cwd = null;
        int timeoutMs = 30000;

        foreach (var field in arguments.EnumerateObject())
        {
            if (!seen.Add(field.Name) || !properties.TryGetProperty(field.Name, out var constraint))
                throw new ToolException("invalid_arguments");
            var type = constraint.GetProperty("type").GetString();
            if (type == "string")
            {
                if (field.Value.ValueKind != JsonValueKind.String) throw new ToolException("invalid_arguments");
                var value = field.Value.GetString()!;
                if (value.Length < constraint.GetProperty("minLength").GetInt32() || value.Length > constraint.GetProperty("maxLength").GetInt32())
                    throw new ToolException("invalid_arguments");
                ReadToolCatalog.ValidateText(value);
                if (field.Name == "command") command = value;
                else if (field.Name == "cwd") cwd = value.Length == 0 ? null : value;
            }
            else if (type == "integer")
            {
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var number) ||
                    number < constraint.GetProperty("minimum").GetInt32() || number > constraint.GetProperty("maximum").GetInt32())
                    throw new ToolException("invalid_arguments");
                if (field.Name == "timeout_ms") timeoutMs = number;
            }
            else if (type == "array")
            {
                if (field.Value.ValueKind != JsonValueKind.Array) throw new ToolException("invalid_arguments");
                var maxItems = constraint.GetProperty("maxItems").GetInt32();
                if (field.Value.GetArrayLength() > maxItems) throw new ToolException("invalid_arguments");
                args = new List<string>(field.Value.GetArrayLength());
                foreach (var item in field.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) throw new ToolException("invalid_arguments");
                    var value = item.GetString()!;
                    if (value.Length > 4096) throw new ToolException("invalid_arguments");
                    ReadToolCatalog.ValidateText(value);
                    args.Add(value);
                }
            }
            else throw new ToolException("invalid_arguments");
        }

        foreach (var required in spec.ParametersSchema.GetProperty("required").EnumerateArray())
            if (!seen.Contains(required.GetString()!)) throw new ToolException("invalid_arguments");

        return (command!, args ?? new List<string>(), cwd, timeoutMs);
    }
}
