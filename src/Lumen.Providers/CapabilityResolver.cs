using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public sealed class CapabilityResolver
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly Regex VisionHint = new Regex(
        @"(?:^|[-_/])(?:vision|vl)(?:$|[-_.:/])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    private readonly IReadOnlyList<RegistryEntry> entries;

    public CapabilityResolver(string? registryJson = null)
    {
        if (registryJson is null)
        {
            using var stream = typeof(CapabilityResolver).Assembly.GetManifestResourceStream(
                "Lumen.Providers.capabilities.registry.json")
                ?? throw new InvalidOperationException("The embedded capability registry is missing.");
            using var reader = new StreamReader(stream);
            registryJson = reader.ReadToEnd();
        }

        entries = ReadRegistry(registryJson);
    }

    public ModelInfo Resolve(string providerId, NativeModel native,
        IReadOnlyDictionary<ModelCapabilities, bool>? overrides = null)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("Provider id is required.", nameof(providerId));
        if (native is null) throw new ArgumentNullException(nameof(native));
        if (string.IsNullOrWhiteSpace(native.Id) || native.Id.Length > 1024)
            throw new ArgumentException("Model id must contain between 1 and 1024 characters.", nameof(native));

        var values = new Dictionary<ModelCapabilities, bool>();
        var origins = new Dictionary<ModelCapabilities, CapabilityOrigin>();
        ReasoningSupport? registryReasoning = null;
        int? contextWindow = null;
        int? maxOutput = null;

        foreach (var entry in entries)
        {
            if (!string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal)) continue;
            try
            {
                if (!entry.Pattern.IsMatch(native.Id)) continue;
            }
            catch (RegexMatchTimeoutException)
            {
                // An updateable registry is untrusted input; a timed-out rule supplies no evidence.
                continue;
            }

            foreach (var pair in entry.Capabilities)
            {
                // Earlier, more specific registry rules win; native values are applied afterwards.
                if (values.ContainsKey(pair.Key)) continue;
                Set(pair.Key, pair.Value, CapabilitySource.Registry, entry.Confidence);
                if (pair.Key == ModelCapabilities.Reasoning) registryReasoning = entry.Reasoning;
            }
            contextWindow ??= entry.ContextWindow;
            maxOutput ??= entry.MaxOutput;
        }

        if (!values.ContainsKey(ModelCapabilities.Vision) && VisionHint.IsMatch(native.Id))
            Set(ModelCapabilities.Vision, true, CapabilitySource.Heuristic, Confidence.Low);

        // A name suggesting reasoning does not establish its wire control mechanism.
        // In particular, never synthesize effort levels for an unfamiliar "reasoner".
        foreach (var pair in native.Capabilities)
        {
            ValidateCapability(pair.Key);
            Set(pair.Key, pair.Value, CapabilitySource.Native, Confidence.High);
        }
        if (native.Reasoning is not null && !native.Capabilities.ContainsKey(ModelCapabilities.Reasoning))
            Set(ModelCapabilities.Reasoning, true, CapabilitySource.Native, Confidence.High);

        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                ValidateCapability(pair.Key);
                Set(pair.Key, pair.Value, CapabilitySource.UserOverride, Confidence.High);
            }
        }

        var flags = ModelCapabilities.None;
        foreach (var pair in values)
            if (pair.Value) flags |= pair.Key;

        ReasoningSupport? reasoning = null;
        if ((flags & ModelCapabilities.Reasoning) != 0)
        {
            reasoning = native.Reasoning ?? registryReasoning;
            if (reasoning is null)
                throw new ArgumentException("Reasoning requires a documented control mechanism in native metadata or the registry.", nameof(native));
        }

        // A missing origin means unknown, not unsupported. Explicit false retains its origin.
        return new ModelInfo(providerId, native.Id, flags, native.DisplayName, reasoning,
            native.ContextWindowTokens ?? contextWindow, native.MaxOutputTokens ?? maxOutput,
            origins.Values.OrderBy(origin => (int)origin.Capability).ToArray());

        void Set(ModelCapabilities capability, bool supported, CapabilitySource source, Confidence confidence)
        {
            values[capability] = supported;
            origins[capability] = new CapabilityOrigin(capability, source, confidence);
        }
    }

    internal static void ValidateCapability(ModelCapabilities capability)
    {
        if (capability == ModelCapabilities.None || !Enum.IsDefined(typeof(ModelCapabilities), capability))
            throw new ArgumentException("Specify exactly one known capability.", nameof(capability));
    }

    private static IReadOnlyList<RegistryEntry> ReadRegistry(string json)
    {
        try
        {
            if (json.Length > 1_048_576) throw new FormatException();
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            ValidateFields(root, "version", "models");
            if (root.GetProperty("version").GetInt32() != 1) throw new FormatException();
            var models = root.GetProperty("models");
            if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > 1000) throw new FormatException();
            var result = new List<RegistryEntry>();
            foreach (var model in models.EnumerateArray())
            {
                ValidateFields(model, "providerId", "modelIdPattern", "source", "confidence", "verifiedAt",
                    "text", "vision", "tools", "parallelTools", "reasoning", "streaming", "structuredOutput", "pdf",
                    "contextWindow", "maxOutput");
                var provider = RequiredString(model, "providerId");
                var pattern = RequiredString(model, "modelIdPattern");
                if (provider.Length > 256 || pattern.Length > 512) throw new FormatException();
                var source = RequiredString(model, "source");
                if (!Uri.TryCreate(source, UriKind.Absolute, out var url) || url.Scheme != "https" ||
                    string.IsNullOrEmpty(url.Host) || !string.IsNullOrEmpty(url.UserInfo)) throw new FormatException();
                var confidence = ReadEnum<Confidence>(RequiredString(model, "confidence"));
                var date = RequiredString(model, "verifiedAt");
                if (!DateTimeOffset.TryParseExact(date, new[] { "yyyy-MM-dd", "O", "yyyy-MM-dd'T'HH:mm:ss'Z'" },
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var verifiedAt) || verifiedAt > DateTimeOffset.UtcNow) throw new FormatException();

                var entry = new RegistryEntry(provider,
                    new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout), confidence);
                ReadFlag("text", ModelCapabilities.Text);
                ReadFlag("vision", ModelCapabilities.Vision);
                ReadFlag("tools", ModelCapabilities.Tools);
                ReadFlag("parallelTools", ModelCapabilities.ParallelTools);
                ReadFlag("streaming", ModelCapabilities.Streaming);
                ReadFlag("structuredOutput", ModelCapabilities.StructuredOutput);
                ReadFlag("pdf", ModelCapabilities.Pdf);
                if (model.TryGetProperty("reasoning", out var reasoning))
                {
                    if (reasoning.ValueKind == JsonValueKind.False)
                        entry.Capabilities.Add(ModelCapabilities.Reasoning, false);
                    else
                    {
                        ValidateFields(reasoning, "kind", "efforts", "mandatory");
                        var kind = ReadEnum<ReasoningKind>(RequiredString(reasoning, "kind"));
                        var efforts = reasoning.GetProperty("efforts").EnumerateArray()
                            .Select(value => ReadEnum<ReasoningIntent>(value.GetString() ?? throw new FormatException())).ToArray();
                        var mandatory = reasoning.GetProperty("mandatory").GetBoolean();
                        if (efforts.Length == 0 || efforts.Distinct().Count() != efforts.Length ||
                            (mandatory && efforts.Contains(ReasoningIntent.Off)) || efforts.All(effort => effort == ReasoningIntent.Off))
                            throw new FormatException();
                        entry.Reasoning = new ReasoningSupport(kind, efforts, mandatory);
                        entry.Capabilities.Add(ModelCapabilities.Reasoning, true);
                    }
                }
                entry.ContextWindow = ReadLimit(model, "contextWindow");
                entry.MaxOutput = ReadLimit(model, "maxOutput");
                if (entry.Capabilities.Count == 0 && entry.ContextWindow is null && entry.MaxOutput is null)
                    throw new FormatException();
                result.Add(entry);

                void ReadFlag(string name, ModelCapabilities capability)
                {
                    if (model.TryGetProperty(name, out var value)) entry.Capabilities.Add(capability, value.GetBoolean());
                }
            }
            return result;
        }
        catch (Exception exception) when (exception is JsonException || exception is FormatException ||
            exception is InvalidOperationException || exception is KeyNotFoundException || exception is ArgumentException ||
            exception is OverflowException)
        {
            // Do not echo an externally supplied registry (which may contain credentials) in errors.
            throw new ArgumentException("Invalid capability registry.", nameof(json));
        }
    }

    internal static void ValidateFields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new FormatException();
    }

    internal static string RequiredString(JsonElement value, string name)
    {
        var text = value.GetProperty(name).GetString();
        return string.IsNullOrWhiteSpace(text) ? throw new FormatException() : text!;
    }

    private static T ReadEnum<T>(string text) where T : struct, Enum
    {
        if (!Enum.TryParse<T>(text, true, out var value) || !Enum.IsDefined(typeof(T), value) ||
            !string.Equals(value.ToString(), text, StringComparison.OrdinalIgnoreCase)) throw new FormatException();
        return value;
    }

    private static int? ReadLimit(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var limit)) return null;
        var number = limit.GetInt32();
        return number > 0 ? number : throw new FormatException();
    }

    private sealed class RegistryEntry
    {
        public RegistryEntry(string providerId, Regex pattern, Confidence confidence)
        {
            ProviderId = providerId;
            Pattern = pattern;
            Confidence = confidence;
        }
        public string ProviderId { get; }
        public Regex Pattern { get; }
        public Confidence Confidence { get; }
        public Dictionary<ModelCapabilities, bool> Capabilities { get; } = new Dictionary<ModelCapabilities, bool>();
        public ReasoningSupport? Reasoning { get; set; }
        public int? ContextWindow { get; set; }
        public int? MaxOutput { get; set; }
    }
}
