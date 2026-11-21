using System;
using System.Collections.Generic;

namespace Lumen.Core.Domain;

[Flags]
public enum ModelCapabilities
{
    None = 0,
    Text = 1,
    Vision = 2,
    Tools = 4,
    ParallelTools = 8,
    Reasoning = 16,
    Streaming = 32,
    StructuredOutput = 64,
    Pdf = 128
}

public enum CapabilitySource
{
    Native,
    Registry,
    Heuristic,
    Probe,
    UserOverride
}

public enum Confidence
{
    Low,
    Medium,
    High
}

public enum ReasoningIntent
{
    Off,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max
}

// Mechanisms observed on the wire: effort enums, thinking token budgets,
// picking a separate reasoner model, and chat-template boolean flags.
public enum ReasoningKind
{
    Effort,
    BudgetTokens,
    ModelSelection,
    TemplateFlag
}

public sealed class ReasoningSupport
{
    public ReasoningSupport(ReasoningKind kind, IReadOnlyList<ReasoningIntent> efforts, bool mandatory = false)
    {
        if (!Enum.IsDefined(typeof(ReasoningKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown reasoning kind.");
        }

        if (efforts is null)
        {
            throw new ArgumentNullException(nameof(efforts));
        }

        if (efforts.Count == 0)
        {
            throw new ArgumentException("A reasoning-capable model must expose at least one effort level.", nameof(efforts));
        }

        foreach (var effort in efforts)
        {
            if (!Enum.IsDefined(typeof(ReasoningIntent), effort))
            {
                throw new ArgumentOutOfRangeException(nameof(efforts), effort, "Unknown reasoning intent.");
            }
        }

        Kind = kind;
        Efforts = efforts;
        Mandatory = mandatory;
    }

    public ReasoningKind Kind { get; }
    public IReadOnlyList<ReasoningIntent> Efforts { get; }

    // True when reasoning cannot be turned off, so ReasoningIntent.Off is not offerable.
    public bool Mandatory { get; }
}

public sealed class CapabilityOrigin
{
    public CapabilityOrigin(ModelCapabilities capability, CapabilitySource source, Confidence confidence)
    {
        if (!IsSingleCapability(capability))
        {
            throw new ArgumentException("A capability origin must describe exactly one capability.", nameof(capability));
        }

        if (!Enum.IsDefined(typeof(CapabilitySource), source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown capability source.");
        }

        if (!Enum.IsDefined(typeof(Confidence), confidence))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Unknown confidence level.");
        }

        Capability = capability;
        Source = source;
        Confidence = confidence;
    }

    public ModelCapabilities Capability { get; }
    public CapabilitySource Source { get; }
    public Confidence Confidence { get; }

    private static bool IsSingleCapability(ModelCapabilities capability) =>
        capability != ModelCapabilities.None && (capability & (capability - 1)) == 0;
}

public sealed class ModelInfo
{
    private const ModelCapabilities AllCapabilities =
        ModelCapabilities.Text | ModelCapabilities.Vision | ModelCapabilities.Tools |
        ModelCapabilities.ParallelTools | ModelCapabilities.Reasoning | ModelCapabilities.Streaming |
        ModelCapabilities.StructuredOutput | ModelCapabilities.Pdf;

    public ModelInfo(
        string providerId,
        string modelId,
        ModelCapabilities capabilities,
        string? displayName = null,
        ReasoningSupport? reasoning = null,
        int? contextWindowTokens = null,
        int? maxOutputTokens = null,
        IReadOnlyList<CapabilityOrigin>? origins = null,
        bool enabled = true)
    {
        ProviderId = string.IsNullOrEmpty(providerId) ? throw new ArgumentNullException(nameof(providerId)) : providerId;
        ModelId = string.IsNullOrEmpty(modelId) ? throw new ArgumentNullException(nameof(modelId)) : modelId;

        if ((capabilities & ~AllCapabilities) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capabilities), capabilities, "Unknown capability flag.");
        }

        if (contextWindowTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextWindowTokens), contextWindowTokens, "Context window must be positive.");
        }

        if (maxOutputTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), maxOutputTokens, "Max output tokens must be positive.");
        }

        if (reasoning is not null && (capabilities & ModelCapabilities.Reasoning) == 0)
        {
            throw new ArgumentException("Reasoning support requires the Reasoning capability.", nameof(reasoning));
        }

        if ((capabilities & ModelCapabilities.Reasoning) != 0 && reasoning is null)
        {
            throw new ArgumentException("A reasoning model must declare how reasoning is controlled.", nameof(reasoning));
        }

        Capabilities = capabilities;
        DisplayName = displayName;
        Reasoning = reasoning;
        ContextWindowTokens = contextWindowTokens;
        MaxOutputTokens = maxOutputTokens;
        Origins = origins ?? Array.Empty<CapabilityOrigin>();
        Enabled = enabled;
    }

    public string ProviderId { get; }
    public string ModelId { get; }
    public ModelCapabilities Capabilities { get; }
    public string? DisplayName { get; }
    public ReasoningSupport? Reasoning { get; }
    public int? ContextWindowTokens { get; }
    public int? MaxOutputTokens { get; }
    public IReadOnlyList<CapabilityOrigin> Origins { get; }
    public bool Enabled { get; }

    public bool Has(ModelCapabilities capability) => (Capabilities & capability) == capability && capability != ModelCapabilities.None;

    public CapabilitySource? SourceOf(ModelCapabilities capability)
    {
        foreach (var origin in Origins)
        {
            if (origin.Capability == capability)
            {
                return origin.Source;
            }
        }

        return null;
    }
}
