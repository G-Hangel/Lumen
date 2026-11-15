using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Lumen.Core.Domain;

namespace Lumen.Tools;

public enum SkillScope
{
    Project,
    User
}

public sealed class SkillDefinition
{
    internal SkillDefinition(string name, string description, string body, string directory, string source, SkillScope scope,
        IReadOnlyList<ChatMode> modes, IReadOnlyList<string> allowedTools, string? license, string? compatibility,
        IReadOnlyDictionary<string, string> metadata, IReadOnlyList<string> warnings)
    {
        Name = name; Description = description; Body = body; Directory = directory; Source = source; Scope = scope;
        Modes = modes; AllowedTools = allowedTools; License = license; Compatibility = compatibility; Metadata = metadata; Warnings = warnings;
    }

    public string Name { get; }
    public string Description { get; }
    public string Body { get; }
    public string Directory { get; }
    public string Source { get; }
    public SkillScope Scope { get; }
    public IReadOnlyList<ChatMode> Modes { get; }
    // Informational only: AGENTS.md §1 approvals are never relaxed by a skill.
    public IReadOnlyList<string> AllowedTools { get; }
    public string? License { get; }
    public string? Compatibility { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
    public IReadOnlyList<string> Warnings { get; }
}

public sealed class SkillValidationException : Exception
{
    public SkillValidationException(string code, string detail) : base(detail) => Code = code;
    public string Code { get; }
}

public static class SkillParser
{
    public const int MaxFileChars = 65536;
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;
    public const int MaxCompatibilityLength = 500;
    public const string ModesKey = "lumen-modes";
    private static readonly Regex NamePattern = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    public static bool IsValidName(string value) => !string.IsNullOrEmpty(value) && value.Length <= MaxNameLength && NamePattern.IsMatch(value);
    private static readonly Regex KeyPattern = new Regex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly string[] KnownFields = { "name", "description", "license", "compatibility", "metadata", "allowed-tools" };
    private static readonly string[] KnownTools =
    {
        "read_file", "list_dir", "find_files", "grep", "get_edit_snapshot", "get_errors", "get_output_logs", "detect_shells",
        "create_file", "edit_file", "apply_diff", "run_command", "run_build", "run_tests", "load_skill"
    };

    public static SkillDefinition Parse(string text, string directoryName, string directory, string source, SkillScope scope)
    {
        if (text.Length > MaxFileChars) Fail("file_too_large", "SKILL.md exceeds " + MaxFileChars + " characters.");
        if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsControl(character) && character != '\n' && character != '\r' && character != '\t')
                Fail("invalid_text", "line " + LineOf(text, index) + ": control character U+" + ((int)character).ToString("X4", CultureInfo.InvariantCulture) + " is not allowed.");
        }
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines[0].TrimEnd() != "---")
            Fail("missing_frontmatter", "line 1: SKILL.md must start with a '---' line, then YAML frontmatter with name and description, then a closing '---' line.");
        var end = -1;
        for (var index = 1; index < lines.Length; index++)
            if (lines[index].TrimEnd() == "---") { end = index; break; }
        if (end < 0) Fail("unterminated_frontmatter", "line 1: the frontmatter has no closing '---' line.");
        var fields = ParseFields(lines, end);
        var warnings = new List<string>();
        foreach (var field in fields.Where(field => !KnownFields.Contains(field.Key)))
            warnings.Add("line " + field.Value.Line + ": unknown field '" + field.Key + "' is ignored.");

        var name = Scalar(fields, "name", true)!.Trim();
        if (name.Length == 0) Fail("missing_name", "line " + fields["name"].Line + ": name must not be empty.");
        if (name.Length > MaxNameLength || !NamePattern.IsMatch(name))
            Fail("invalid_name", "line " + fields["name"].Line + ": name '" + Shorten(name) + "' must be 1-" + MaxNameLength + " lowercase letters, digits or single hyphens, without a leading or trailing hyphen.");
        if (!string.Equals(name, directoryName, StringComparison.Ordinal))
            Fail("name_mismatch", "line " + fields["name"].Line + ": name '" + name + "' must match its folder name '" + Shorten(directoryName) + "'.");

        var description = Scalar(fields, "description", true)!.Trim();
        if (description.Length == 0) Fail("missing_description", "line " + fields["description"].Line + ": description must not be empty.");
        if (description.Length > MaxDescriptionLength)
            Fail("description_too_long", "line " + fields["description"].Line + ": description has " + description.Length + " characters; the limit is " + MaxDescriptionLength + ".");

        var license = Scalar(fields, "license", false)?.Trim();
        var compatibility = Scalar(fields, "compatibility", false)?.Trim();
        if (compatibility is not null && (compatibility.Length == 0 || compatibility.Length > MaxCompatibilityLength))
            Fail("invalid_compatibility", "line " + fields["compatibility"].Line + ": compatibility must have 1-" + MaxCompatibilityLength + " characters.");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (fields.TryGetValue("metadata", out var metadataField))
        {
            if (metadataField.Value is Dictionary<string, string> map) foreach (var pair in map) metadata.Add(pair.Key, pair.Value);
            else if (!(metadataField.Value is string empty && empty.Length == 0))
                Fail("invalid_field", "line " + metadataField.Line + ": metadata must be a map of 'key: value' lines indented below it.");
        }

        var modes = new List<ChatMode> { ChatMode.Ask, ChatMode.Plan, ChatMode.Agent };
        if (metadata.TryGetValue(ModesKey, out var modeText))
        {
            modes.Clear();
            foreach (var token in modeText.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var mode = token.ToLowerInvariant() switch
                {
                    "ask" => ChatMode.Ask,
                    "plan" => ChatMode.Plan,
                    "agent" => ChatMode.Agent,
                    _ => (ChatMode?)null
                };
                if (mode is null) Fail("invalid_modes", "metadata." + ModesKey + ": unknown mode '" + Shorten(token) + "'; use ask, plan and/or agent.");
                if (!modes.Contains(mode!.Value)) modes.Add(mode.Value);
            }
            if (modes.Count == 0) Fail("invalid_modes", "metadata." + ModesKey + ": list at least one of ask, plan, agent.");
        }

        var allowedTools = new List<string>();
        if (fields.TryGetValue("allowed-tools", out var toolsField))
        {
            var items = toolsField.Value is List<string> list ? list
                : toolsField.Value is string toolText ? toolText.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList()
                : throw new SkillValidationException("invalid_field", "line " + toolsField.Line + ": allowed-tools must be a space-separated list.");
            foreach (var item in items.Select(item => item.Trim()).Where(item => item.Length > 0))
            {
                if (item.Length > 128) Fail("invalid_field", "line " + toolsField.Line + ": allowed-tools entry is longer than 128 characters.");
                if (!allowedTools.Contains(item, StringComparer.Ordinal)) allowedTools.Add(item);
                if (!KnownTools.Contains(item, StringComparer.Ordinal))
                    warnings.Add("line " + toolsField.Line + ": allowed-tools entry '" + item + "' is not a Lumen tool and is ignored.");
            }
            if (allowedTools.Count > 64) Fail("invalid_field", "line " + toolsField.Line + ": allowed-tools lists more than 64 entries.");
        }

        var body = string.Join("\n", lines.Skip(end + 1)).Trim('\n', ' ', '\t');
        return new SkillDefinition(name, description, body, directory, source, scope, modes.AsReadOnly(), allowedTools.AsReadOnly(),
            string.IsNullOrEmpty(license) ? null : license, compatibility, metadata, warnings.AsReadOnly());
    }

    private sealed class Field
    {
        internal Field(object value, int line) { Value = value; Line = line; }
        internal object Value { get; }
        internal int Line { get; }
    }

    private static Dictionary<string, Field> ParseFields(string[] lines, int end)
    {
        var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
        var index = 1;
        while (index < end)
        {
            var line = lines[index];
            var number = index + 1;
            if (IsBlankOrComment(line)) { index++; continue; }
            if (line[0] == ' ' || line[0] == '\t') Fail("invalid_yaml", "line " + number + ": unexpected indentation; top-level fields start at column 1.");
            var colon = KeyColon(line);
            if (colon <= 0) Fail("invalid_yaml", "line " + number + ": expected 'key: value'.");
            var key = line.Substring(0, colon).Trim();
            if (!KeyPattern.IsMatch(key)) Fail("invalid_yaml", "line " + number + ": '" + Shorten(key) + "' is not a valid field name.");
            if (fields.ContainsKey(key)) Fail("duplicate_field", "line " + number + ": field '" + key + "' appears more than once.");
            var raw = line.Substring(colon + 1).Trim();
            index++;
            var nested = new List<string>();
            var nestedStart = index;
            while (index < end && (lines[index].Length == 0 || lines[index][0] == ' ' || lines[index][0] == '\t' || lines[index].Trim().Length == 0))
                nested.Add(lines[index++]);
            fields.Add(key, new Field(ParseValue(raw, nested, number, nestedStart + 1), number));
        }
        return fields;
    }

    private static object ParseValue(string raw, List<string> nested, int number, int nestedNumber)
    {
        var content = nested.Where(line => !IsBlankOrComment(line)).ToList();
        if (raw.Length == 0 || raw[0] == '#')
        {
            if (content.Count == 0) return string.Empty;
            var first = content[0].Trim();
            if (first == "-" || first.StartsWith("- ", StringComparison.Ordinal))
            {
                var items = new List<string>();
                foreach (var item in content)
                {
                    var trimmed = item.Trim();
                    if (trimmed != "-" && !trimmed.StartsWith("- ", StringComparison.Ordinal))
                        Fail("unsupported_yaml", "line " + nestedNumber + ": a list may only contain '- item' lines.");
                    items.Add(InlineScalar(trimmed.Substring(1).Trim(), number));
                }
                return items;
            }
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var indent = Indent(content[0]);
            foreach (var entry in content)
            {
                if (Indent(entry) != indent) Fail("unsupported_yaml", "line " + nestedNumber + ": nested maps deeper than one level are not supported.");
                var trimmed = entry.Trim();
                var colon = KeyColon(trimmed);
                if (colon <= 0) Fail("unsupported_yaml", "line " + nestedNumber + ": expected an indented 'key: value' line.");
                var key = trimmed.Substring(0, colon).Trim();
                if (!KeyPattern.IsMatch(key)) Fail("invalid_yaml", "line " + nestedNumber + ": '" + Shorten(key) + "' is not a valid key.");
                if (map.ContainsKey(key)) Fail("duplicate_field", "line " + nestedNumber + ": key '" + key + "' appears more than once.");
                var value = trimmed.Substring(colon + 1).Trim();
                if (value.Length == 0 || value[0] == '|' || value[0] == '>' || value[0] == '[' || value[0] == '{')
                    Fail("unsupported_yaml", "line " + nestedNumber + ": metadata values must be single-line text.");
                map.Add(key, InlineScalar(value, nestedNumber));
            }
            return map;
        }
        if (raw[0] == '|' || raw[0] == '>')
        {
            var header = StripComment(raw).Trim();
            if (!Regex.IsMatch(header, "^[|>][+-]?$", RegexOptions.CultureInvariant))
                Fail("unsupported_yaml", "line " + number + ": block scalar header '" + Shorten(header) + "' is not supported; use '|' or '>'.");
            var lines = nested.Where(line => line.Trim().Length > 0).ToList();
            if (lines.Count == 0) return string.Empty;
            var indent = lines.Min(Indent);
            var block = nested.Select(line => line.Trim().Length == 0 ? string.Empty : line.Substring(Math.Min(indent, line.Length))).ToList();
            if (header[0] == '|') return string.Join("\n", block).Trim('\n');
            var folded = new StringBuilder();
            foreach (var line in block)
            {
                if (line.Length == 0) { folded.Append('\n'); continue; }
                if (folded.Length > 0 && folded[folded.Length - 1] != '\n') folded.Append(' ');
                folded.Append(line.Trim());
            }
            return folded.ToString().Trim('\n', ' ');
        }
        if (raw[0] == '[')
        {
            if (content.Count > 0 || !StripComment(raw).TrimEnd().EndsWith("]", StringComparison.Ordinal))
                Fail("unsupported_yaml", "line " + number + ": a '[...]' list must fit on one line.");
            var inner = StripComment(raw).Trim();
            inner = inner.Substring(1, inner.Length - 2);
            return inner.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0).Select(item => InlineScalar(item, number)).ToList();
        }
        if (raw[0] == '{') Fail("unsupported_yaml", "line " + number + ": '{...}' maps are not supported; use indented 'key: value' lines.");
        if (raw[0] == '"' || raw[0] == '\'')
        {
            if (content.Count > 0) Fail("unsupported_yaml", "line " + number + ": a quoted value must fit on one line.");
            return InlineScalar(raw, number);
        }
        var plain = StripComment(raw).Trim();
        foreach (var line in content) plain += " " + StripComment(line).Trim();
        return plain.Trim();
    }

    private static string InlineScalar(string raw, int number)
    {
        if (raw.Length == 0) return string.Empty;
        if (raw[0] == '\'')
        {
            var value = new StringBuilder();
            for (var index = 1; index < raw.Length; index++)
            {
                if (raw[index] != '\'') { value.Append(raw[index]); continue; }
                if (index + 1 < raw.Length && raw[index + 1] == '\'') { value.Append('\''); index++; continue; }
                RequireTrailing(raw.Substring(index + 1), number);
                return value.ToString();
            }
            Fail("invalid_yaml", "line " + number + ": the single-quoted value is not closed.");
        }
        if (raw[0] == '"')
        {
            var value = new StringBuilder();
            for (var index = 1; index < raw.Length; index++)
            {
                var character = raw[index];
                if (character == '"')
                {
                    RequireTrailing(raw.Substring(index + 1), number);
                    return value.ToString();
                }
                if (character != '\\') { value.Append(character); continue; }
                if (++index == raw.Length) break;
                switch (raw[index])
                {
                    case '"': value.Append('"'); break;
                    case '\\': value.Append('\\'); break;
                    case '/': value.Append('/'); break;
                    case 'n': value.Append('\n'); break;
                    case 't': value.Append('\t'); break;
                    case 'u':
                        if (index + 4 < raw.Length && int.TryParse(raw.Substring(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) &&
                            !char.IsControl((char)code) && !char.IsSurrogate((char)code))
                        {
                            value.Append((char)code);
                            index += 4;
                            break;
                        }
                        Fail("invalid_yaml", "line " + number + ": invalid \\u escape.");
                        break;
                    default:
                        Fail("invalid_yaml", "line " + number + ": unsupported escape '\\" + raw[index] + "'.");
                        break;
                }
            }
            Fail("invalid_yaml", "line " + number + ": the double-quoted value is not closed.");
        }
        return StripComment(raw).Trim();
    }

    private static void RequireTrailing(string rest, int number)
    {
        var trimmed = rest.Trim();
        if (trimmed.Length > 0 && trimmed[0] != '#') Fail("invalid_yaml", "line " + number + ": unexpected text after the closing quote.");
    }

    private static string? Scalar(Dictionary<string, Field> fields, string key, bool required)
    {
        if (!fields.TryGetValue(key, out var field))
        {
            if (required) Fail("missing_" + key, "frontmatter: required field '" + key + "' is missing.");
            return null;
        }
        if (field.Value is string text) return text;
        throw new SkillValidationException("invalid_field", "line " + field.Line + ": '" + key + "' must be text, not a list or map.");
    }

    private static int KeyColon(string line)
    {
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"' || line[index] == '\'' || line[index] == '#') return -1;
            if (line[index] == ':' && (index + 1 == line.Length || line[index + 1] == ' ' || line[index + 1] == '\t')) return index;
        }
        return -1;
    }

    private static string StripComment(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (value[index] == '#' && (index == 0 || value[index - 1] == ' ' || value[index - 1] == '\t')) return value.Substring(0, index);
        return value;
    }

    private static int Indent(string line)
    {
        var count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t')) count++;
        return count;
    }

    private static bool IsBlankOrComment(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length == 0 || trimmed[0] == '#';
    }

    private static int LineOf(string text, int index) => text.Substring(0, index).Count(character => character == '\n') + 1;
    private static string Shorten(string value) => value.Length <= 80 ? value : value.Substring(0, 80) + "...";
    private static void Fail(string code, string detail) => throw new SkillValidationException(code, detail);
}
