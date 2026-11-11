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
            ContentProperties, "[\"path\",\"content\"]"),
        Spec("apply_diff", "Apply 1-32 exact, unique, non-overlapping replacements against the original text of an existing file. base_sha256 is the SHA-256 hex digest of its current text encoded as UTF-8 without BOM. Combined old/new text is limited to 131072 characters.",
            DiffProperties, "[\"path\",\"base_sha256\",\"replacements\"]")
    });

    private const string DiffProperties =
        "{\"path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":1024}," +
        "\"base_sha256\":{\"type\":\"string\",\"minLength\":64,\"maxLength\":64,\"pattern\":\"^[0-9a-fA-F]{64}$\"}," +
        "\"replacements\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":32,\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"old\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":65536},\"new\":{\"type\":\"string\",\"maxLength\":65536}},\"required\":[\"old\",\"new\"]}}}";

    private const string ContentProperties =
        "{\"path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":4096}," +
        "\"content\":{\"type\":\"string\",\"minLength\":0,\"maxLength\":1048576}}";

    private static ToolSpec Spec(string name, string description, string properties, string required)
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":" + properties + ",\"required\":" + required + "}");
        return new ToolSpec(name, description, document.RootElement.Clone());
    }

    public static (string Path, string Hash, (string Old, string New)[] Replacements) ValidateDiff(ToolInvocation invocation)
    {
        if (invocation.Name != "apply_diff" || invocation.Status != ToolCallStatus.Pending) throw new ToolException("invalid_arguments");
        var args = invocation.Arguments;
        if (args.ValueKind != JsonValueKind.Object) throw new ToolException("invalid_arguments");
        string? path = null, hash = null;
        var replacements = new List<(string Old, string New)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in args.EnumerateObject())
        {
            if (!seen.Add(field.Name)) throw new ToolException("invalid_arguments");
            if (field.Name == "path" || field.Name == "base_sha256")
            {
                if (field.Value.ValueKind != JsonValueKind.String) throw new ToolException("invalid_arguments");
                var value = field.Value.GetString()!;
                ReadToolCatalog.ValidateText(value);
                if (field.Name == "path") { if (value.Length < 1 || value.Length > 1024) throw new ToolException("invalid_arguments"); path = value; }
                else { if (value.Length != 64 || value.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))) throw new ToolException("invalid_arguments"); hash = value; }
            }
            else if (field.Name == "replacements")
            {
                if (field.Value.ValueKind != JsonValueKind.Array || field.Value.GetArrayLength() < 1 || field.Value.GetArrayLength() > 32) throw new ToolException("invalid_arguments");
                var total = 0;
                foreach (var item in field.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) throw new ToolException("invalid_arguments");
                    string? old = null, replacement = null;
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var part in item.EnumerateObject())
                    {
                        if (!names.Add(part.Name) || (part.Name != "old" && part.Name != "new") || part.Value.ValueKind != JsonValueKind.String) throw new ToolException("invalid_arguments");
                        var value = part.Value.GetString()!;
                        ReadToolCatalog.ValidateText(value);
                        if (value.Length > 65536) throw new ToolException("invalid_arguments");
                        total += value.Length;
                        if (part.Name == "old") old = value; else replacement = value;
                    }
                    if (string.IsNullOrEmpty(old) || replacement is null || total > 131072) throw new ToolException("invalid_arguments");
                    replacements.Add((old!, replacement));
                }
            }
            else throw new ToolException("invalid_arguments");
        }
        if (path is null || hash is null || replacements.Count == 0) throw new ToolException("invalid_arguments");
        return (path, hash, replacements.ToArray());
    }

    public static string ComputeTextSha256(string text)
    {
        byte[] digest;
        using (var sha = System.Security.Cryptography.SHA256.Create()) digest = sha.ComputeHash(new System.Text.UTF8Encoding(false, true).GetBytes(text));
        return BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
    }

    public static string ApplyDiff(string original, string hash, (string Old, string New)[] replacements)
    {
        var actual = ComputeTextSha256(original);
        if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase)) throw new ToolException("edit_conflict");
        var matches = new List<(int Start, int Length, string New)>();
        foreach (var replacement in replacements)
        {
            var index = original.IndexOf(replacement.Old, StringComparison.Ordinal);
            if (index < 0 || original.IndexOf(replacement.Old, index + 1, StringComparison.Ordinal) >= 0) throw new ToolException("edit_conflict");
            matches.Add((index, replacement.Old.Length, replacement.New));
        }
        matches.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = new System.Text.StringBuilder();
        var position = 0;
        foreach (var match in matches)
        {
            if (match.Start < position) throw new ToolException("edit_conflict");
            result.Append(original, position, match.Start - position).Append(match.New);
            position = match.Start + match.Length;
        }
        return result.Append(original, position, original.Length - position).ToString();
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
