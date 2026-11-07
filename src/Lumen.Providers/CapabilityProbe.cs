using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public sealed class CapabilityProbe
{
    public const int MaxProbesPerSession = 5;
    private readonly Func<string, string, ModelCapabilities, CancellationToken, Task<bool?>> callback;
    private readonly CapabilityProbeStore store;
    private readonly object gate = new object();
    private readonly HashSet<(string Provider, string Model, ModelCapabilities Capability)> attempted =
        new HashSet<(string, string, ModelCapabilities)>();

    public CapabilityProbe(
        Func<string, string, ModelCapabilities, CancellationToken, Task<bool?>> callback,
        CapabilityProbeStore? store = null)
    {
        this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
        this.store = store ?? new CapabilityProbeStore();
    }

    public CapabilityProbeStore Store => store;
    public int RemainingBudget
    {
        get { lock (gate) return MaxProbesPerSession - attempted.Count; }
    }

    // One instance represents one UI session. The caller supplies only its selected model,
    // after explicit consent. False must mean capability rejection, not auth/quota failure;
    // the injected callback returns null when support could not be established.
    public async Task<bool?> ProbeAsync(string providerId, string selectedModelId, ModelCapabilities capability,
        bool optedIn, CancellationToken cancellationToken = default)
    {
        CapabilityProbeStore.ValidateKey(providerId, selectedModelId, capability);
        cancellationToken.ThrowIfCancellationRequested();
        if (!optedIn || capability == ModelCapabilities.Reasoning) return null;

        lock (gate)
        {
            var cached = store.Get(providerId, selectedModelId, capability);
            if (cached.HasValue) return cached;
            var key = (providerId, selectedModelId, capability);
            if (attempted.Count >= MaxProbesPerSession || !attempted.Add(key)) return null;
        }

        // Failed, unknown, and cancelled attempts still consume budget: retrying them could cost money.
        var result = await callback(providerId, selectedModelId, capability, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.HasValue) store.Record(providerId, selectedModelId, capability, result.Value);
        return result;
    }
}

public sealed class CapabilityProbeStore
{
    private readonly object gate = new object();
    private readonly TimeSpan positiveTtl;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Dictionary<(string Provider, string Model, ModelCapabilities Capability), CacheEntry> entries =
        new Dictionary<(string, string, ModelCapabilities), CacheEntry>();

    public CapabilityProbeStore(TimeSpan? positiveTtl = null, Func<DateTimeOffset>? utcNow = null)
    {
        this.positiveTtl = positiveTtl ?? TimeSpan.FromHours(24);
        if (this.positiveTtl <= TimeSpan.Zero || this.positiveTtl > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(positiveTtl));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public bool? Get(string providerId, string modelId, ModelCapabilities capability)
    {
        ValidateKey(providerId, modelId, capability);
        lock (gate)
        {
            var key = (providerId, modelId, capability);
            if (!entries.TryGetValue(key, out var entry)) return null;
            if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value <= utcNow())
            {
                entries.Remove(key);
                return null;
            }
            return entry.Supported;
        }
    }

    internal void RemoveProvider(string providerId)
    {
        lock (gate)
            foreach (var key in entries.Keys.Where(key => key.Provider == providerId).ToArray()) entries.Remove(key);
    }

    public string ExportJson()
    {
        lock (gate)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteStartArray("entries");
                var now = utcNow();
                foreach (var pair in entries.OrderBy(pair => pair.Key.Provider, StringComparer.Ordinal)
                    .ThenBy(pair => pair.Key.Model, StringComparer.Ordinal).ThenBy(pair => (int)pair.Key.Capability))
                {
                    if (pair.Value.ExpiresAt.HasValue && pair.Value.ExpiresAt.Value <= now) continue;
                    writer.WriteStartObject();
                    writer.WriteString("providerId", pair.Key.Provider);
                    writer.WriteString("modelId", pair.Key.Model);
                    writer.WriteString("capability", pair.Key.Capability.ToString());
                    writer.WriteBoolean("supported", pair.Value.Supported);
                    if (pair.Value.ExpiresAt.HasValue) writer.WriteString("expiresAt", pair.Value.ExpiresAt.Value);
                    else writer.WriteNull("expiresAt");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    public void ImportJson(string json)
    {
        var imported = new Dictionary<(string Provider, string Model, ModelCapabilities Capability), CacheEntry>();
        try
        {
            if (json is null || json.Length > 1_048_576) throw new FormatException();
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            CapabilityResolver.ValidateFields(root, "version", "entries");
            if (root.GetProperty("version").GetInt32() != 1) throw new FormatException();
            var items = root.GetProperty("entries");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 10000) throw new FormatException();
            var now = utcNow();
            foreach (var item in items.EnumerateArray())
            {
                CapabilityResolver.ValidateFields(item, "providerId", "modelId", "capability", "supported", "expiresAt");
                var provider = CapabilityResolver.RequiredString(item, "providerId");
                var model = CapabilityResolver.RequiredString(item, "modelId");
                var name = CapabilityResolver.RequiredString(item, "capability");
                if (!Enum.TryParse<ModelCapabilities>(name, out var capability) || capability.ToString() != name)
                    throw new FormatException();
                ValidateKey(provider, model, capability);
                if (capability == ModelCapabilities.Reasoning) throw new FormatException();
                var supported = item.GetProperty("supported").GetBoolean();
                var expiry = item.GetProperty("expiresAt");
                DateTimeOffset? expiresAt = expiry.ValueKind == JsonValueKind.Null ? null : expiry.GetDateTimeOffset();
                if (supported != expiresAt.HasValue) throw new FormatException();
                if (expiresAt > now + positiveTtl) expiresAt = now + positiveTtl;
                var key = (provider, model, capability);
                if (imported.ContainsKey(key)) throw new FormatException();
                imported.Add(key, new CacheEntry(supported, expiresAt));
            }
        }
        catch (Exception exception) when (exception is JsonException || exception is FormatException ||
            exception is InvalidOperationException || exception is KeyNotFoundException || exception is ArgumentException ||
            exception is OverflowException)
        {
            throw new ArgumentException("Invalid capability probe cache.", nameof(json));
        }

        // Validate the entire import before touching live state. A permanent negative cannot
        // be replaced by a later positive from another session or an imported cache.
        lock (gate)
        {
            var now = utcNow();
            foreach (var pair in imported)
            {
                if (pair.Value.ExpiresAt.HasValue && pair.Value.ExpiresAt.Value <= now) continue;
                if (entries.TryGetValue(pair.Key, out var existing) && !existing.Supported) continue;
                entries[pair.Key] = pair.Value;
            }
        }
    }

    internal void Record(string providerId, string modelId, ModelCapabilities capability, bool supported)
    {
        lock (gate)
        {
            var key = (providerId, modelId, capability);
            if (entries.TryGetValue(key, out var existing) && !existing.Supported) return;
            entries[key] = new CacheEntry(supported, supported ? utcNow() + positiveTtl : (DateTimeOffset?)null);
        }
    }

    internal static void ValidateKey(string providerId, string modelId, ModelCapabilities capability)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 256)
            throw new ArgumentException("Provider id is required and must not exceed 256 characters.", nameof(providerId));
        if (string.IsNullOrWhiteSpace(modelId) || modelId.Length > 1024)
            throw new ArgumentException("Model id is required and must not exceed 1024 characters.", nameof(modelId));
        CapabilityResolver.ValidateCapability(capability);
    }

    private sealed class CacheEntry
    {
        public CacheEntry(bool supported, DateTimeOffset? expiresAt)
        {
            Supported = supported;
            ExpiresAt = expiresAt;
        }
        public bool Supported { get; }
        public DateTimeOffset? ExpiresAt { get; }
    }
}
