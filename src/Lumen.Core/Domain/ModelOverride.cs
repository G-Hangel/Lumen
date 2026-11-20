using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumen.Core.Domain;

public sealed class ModelOverride
{
    public ModelOverride(string displayName, bool tools, bool reasoning, IReadOnlyList<string> efforts)
    {
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        Tools = tools;
        Reasoning = reasoning;
        Efforts = efforts ?? Array.Empty<string>();
    }
    public string? DisplayName { get; }
    public bool Tools { get; }
    public bool Reasoning { get; }
    public IReadOnlyList<string> Efforts { get; }
}