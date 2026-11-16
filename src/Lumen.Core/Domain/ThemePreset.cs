using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Lumen.Core.Domain;

// A custom visual preset that overrides the VS-derived theme. All fields are
// optional — null means "inherit from the VS theme or the default". Persisted
// per-user next to the provider settings, never inside the solution.
public sealed class ThemePreset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [JsonPropertyName("foreground")]
    public string? Foreground { get; set; }

    [JsonPropertyName("accent")]
    public string? Accent { get; set; }

    [JsonPropertyName("chatFont")]
    public string? ChatFont { get; set; }

    [JsonPropertyName("codeFont")]
    public string? CodeFont { get; set; }

    [JsonPropertyName("fontSize")]
    public int? FontSize { get; set; }

    [JsonPropertyName("density")]
    public string? Density { get; set; }

    [JsonPropertyName("borderRadius")]
    public int? BorderRadius { get; set; }

    // Accent per chat mode (ask, plan, agent) so the composer can be tinted per mode.
    [JsonPropertyName("modeAccent")]
    public Dictionary<string, string>? ModeAccent { get; set; }
}
