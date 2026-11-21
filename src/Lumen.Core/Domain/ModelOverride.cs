using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumen.Core.Domain;

public sealed class ModelOverride
{
    public ModelOverride(string displayName, bool vision, bool tools, bool reasoning, IReadOnlyList<string> efforts, bool enabled = true)
    {
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        Vision = vision;
        Tools = tools;
        Reasoning = reasoning;
        Efforts = efforts ?? Array.Empty<string>();
        Enabled = enabled;
    }
    public string? DisplayName { get; }
    public bool Vision { get; }
    public bool Tools { get; }
    public bool Reasoning { get; }
    public IReadOnlyList<string> Efforts { get; }
    public bool Enabled { get; }
}