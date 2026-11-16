using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Lumen.Core.Domain;

namespace Lumen.Tools;

public sealed class ThemeValidationException : Exception
{
    public ThemeValidationException(string code, string detail) : base(detail) { Code = code; Detail = detail; }
    public string Code { get; }
    public string Detail { get; }
}

// Validation, contrast math and the limits of a custom visual preset.
// The same rules are mirrored in wwwroot/byok-bridge.mjs, so both sides reject
// the same malformed theme before it reaches the CSS variables.
public static class ThemeCatalog
{
    public const int MaxFontSize = 24;
    public const int MinFontSize = 10;
    public const int MaxBorderRadius = 24;
    public const int MaxNameLength = 64;
    public const int MaxFontLength = 256;
    public const double TextContrastRatio = 4.5;
    public const double UiContrastRatio = 3;

    public static readonly string[] Densities = { "compact", "normal", "spacious" };
    public static readonly string[] Modes = { "ask", "plan", "agent" };

    public static ThemePreset Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) throw Invalid("theme must be a JSON object.");
        var preset = new ThemePreset();
        foreach (var field in json.EnumerateObject())
        {
            switch (field.Name)
            {
                case "name": preset.Name = OptionalText(field.Value, MaxNameLength, "name"); break;
                case "background": preset.Background = OptionalColor(field.Value, "background"); break;
                case "foreground": preset.Foreground = OptionalColor(field.Value, "foreground"); break;
                case "accent": preset.Accent = OptionalColor(field.Value, "accent"); break;
                case "chatFont": preset.ChatFont = OptionalFont(field.Value, "chatFont"); break;
                case "codeFont": preset.CodeFont = OptionalFont(field.Value, "codeFont"); break;
                case "fontSize":
                    if (field.Value.ValueKind == JsonValueKind.Null) break;
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var size) || size < MinFontSize || size > MaxFontSize)
                        throw Invalid("fontSize must be an integer between " + MinFontSize + " and " + MaxFontSize + ".");
                    preset.FontSize = size;
                    break;
                case "density":
                    var density = OptionalText(field.Value, 16, "density");
                    if (density is not null && !Densities.Contains(density, StringComparer.Ordinal)) throw Invalid("density must be one of " + string.Join(", ", Densities) + ".");
                    preset.Density = density;
                    break;
                case "borderRadius":
                    if (field.Value.ValueKind == JsonValueKind.Null) break;
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var radius) || radius < 0 || radius > MaxBorderRadius)
                        throw Invalid("borderRadius must be an integer between 0 and " + MaxBorderRadius + ".");
                    preset.BorderRadius = radius;
                    break;
                case "modeAccent": preset.ModeAccent = ModeAccents(field.Value); break;
                default: throw Invalid("unknown theme field '" + field.Name + "'.");
            }
        }
        return preset;
    }

    // WCAG 2.1 relative luminance contrast ratio between two #rgb/#rrggbb colors.
    public static double Contrast(string foreground, string background)
    {
        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    // Warnings never block a theme; they tell the UI that a preset is legible
    // but below the WCAG AA targets.
    public static IReadOnlyList<string> Warnings(ThemePreset preset)
    {
        var warnings = new List<string>();
        var background = preset.Background;
        if (background is null) return warnings;
        if (preset.Foreground is not null && Contrast(preset.Foreground, background) < TextContrastRatio) warnings.Add("low_contrast_text");
        if (preset.Accent is not null && Contrast(preset.Accent, background) < UiContrastRatio) warnings.Add("low_contrast_accent");
        if (preset.ModeAccent is not null && preset.ModeAccent.Values.Any(value => Contrast(value, background) < UiContrastRatio)) warnings.Add("low_contrast_mode_accent");
        return warnings;
    }

    public static bool IsColor(string value)
    {
        if (value is null || value.Length is not (4 or 7) || value[0] != '#') return false;
        for (var index = 1; index < value.Length; index++)
            if (!Uri.IsHexDigit(value[index])) return false;
        return true;
    }

    private static double Luminance(string color)
    {
        var hex = color.Length == 4
            ? new string(new[] { color[1], color[1], color[2], color[2], color[3], color[3] })
            : color.Substring(1);
        var red = Channel(hex[0], hex[1]);
        var green = Channel(hex[2], hex[3]);
        var blue = Channel(hex[4], hex[5]);
        return red * 0.2126 + green * 0.7152 + blue * 0.0722;
    }

    private static double Channel(char high, char low)
    {
        var value = Convert.ToInt32(new string(new[] { high, low }), 16) / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static Dictionary<string, string>? ModeAccents(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("modeAccent must be a JSON object.");
        var accents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            if (!Modes.Contains(field.Name, StringComparer.Ordinal)) throw Invalid("unknown mode '" + field.Name + "' in modeAccent.");
            var color = OptionalColor(field.Value, "modeAccent." + field.Name);
            if (color is null) throw Invalid("modeAccent." + field.Name + " must be a color.");
            accents[field.Name] = color;
        }
        return accents;
    }

    private static string? OptionalColor(JsonElement value, string field)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid(field + " must be a color string.");
        var color = value.GetString();
        if (color is null || !IsColor(color)) throw Invalid(field + " must be #rgb or #rrggbb.");
        return color;
    }

    private static string? OptionalFont(JsonElement value, string field)
    {
        var text = OptionalText(value, MaxFontLength, field);
        if (text is null) return null;
        // Only font-stack characters are accepted: the value ends up in a CSS variable.
        foreach (var character in text)
            if (!char.IsLetterOrDigit(character) && !" -,'\"./".Contains(character)) throw Invalid(field + " has unsupported characters.");
        return text;
    }

    private static string? OptionalText(JsonElement value, int maxLength, string field)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid(field + " must be a string.");
        var text = value.GetString();
        if (text is null || text.Length > maxLength) throw Invalid(field + " is too long.");
        foreach (var character in text)
            if (char.IsControl(character)) throw Invalid(field + " has unsupported characters.");
        return text;
    }

    private static ThemeValidationException Invalid(string detail) =>
        new("invalid_configuration", detail);
}
