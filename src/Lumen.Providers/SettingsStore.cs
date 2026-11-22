using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Tools;

namespace Lumen.Providers;

public sealed class SettingsStore
{
    private readonly string path;
    private static readonly SemaphoreSlim ModelsLock = new(1, 1);
    public SettingsStore(string path)
    {
        this.path = path;
        Conversations = new ConversationStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "conversations"));
    }
    public ConversationStore Conversations { get; }
    public async Task<IReadOnlyList<ProviderConfiguration>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return Array.Empty<ProviderConfiguration>();
        using var file = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false);
        var configurations = JsonWire.Array(json.RootElement).Select(item => new ProviderConfiguration(
            JsonWire.String(item, "id")!, JsonWire.String(item, "providerId")!,
            (ProviderKind)Enum.Parse(typeof(ProviderKind), JsonWire.String(item, "kind")!),
            JsonWire.String(item, "baseUrl")!, JsonWire.String(item, "selectedModelId"),
            JsonWire.Array(JsonWire.Get(item, "deploymentNames")).Select(value => value.GetString()!).ToArray())).ToArray();
        if (configurations.Select(configuration => configuration.Id).Distinct(StringComparer.Ordinal).Count() != configurations.Length)
            throw new ProviderException("invalid_configuration");
        return configurations;
    }
    public Task WriteAsync(IReadOnlyList<ProviderConfiguration> configurations, CancellationToken cancellationToken) =>
        WriteJsonAsync(path, JsonWire.Serialize(configurations.Select(configuration => new
        {
            id = configuration.Id, providerId = configuration.ProviderId, kind = configuration.Kind.ToString(),
            baseUrl = configuration.BaseUrl, selectedModelId = configuration.SelectedModelId, deploymentNames = configuration.DeploymentNames
        })), cancellationToken);

    public async Task ReadProbeCacheAsync(CapabilityProbeStore store, CancellationToken cancellationToken)
    {
        var cachePath = Path.ChangeExtension(path, "probes.json");
        if (!File.Exists(cachePath)) return;
        using var file = File.OpenRead(cachePath);
        if (file.Length > 1_048_576) throw new ProviderException("invalid_configuration");
        using var reader = new StreamReader(file);
        cancellationToken.ThrowIfCancellationRequested();
        var json = await reader.ReadToEndAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        store.ImportJson(json);
    }

    public Task WriteProbeCacheAsync(CapabilityProbeStore store, CancellationToken cancellationToken) =>
        WriteJsonAsync(Path.ChangeExtension(path, "probes.json"), store.ExportJson(), cancellationToken);

    public async Task<IReadOnlyCollection<string>> ReadDisabledSkillsAsync(CancellationToken cancellationToken)
    {
        var skillsPath = Path.ChangeExtension(path, "skills.json");
        if (!File.Exists(skillsPath)) return Array.Empty<string>();
        using var file = File.OpenRead(skillsPath);
        if (file.Length > 32768) throw new ProviderException("invalid_configuration");
        using var json = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > SkillDiscovery.MaxSkills)
            throw new ProviderException("invalid_configuration");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (name is null || name.Length > SkillParser.MaxNameLength || SkillCatalog.ExplicitName("/" + name) != name || !names.Add(name))
                throw new ProviderException("invalid_configuration");
        }
        return names.ToArray();
    }

    public Task WriteDisabledSkillsAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken) =>
        WriteJsonAsync(Path.ChangeExtension(path, "skills.json"), JsonWire.Serialize(names.OrderBy(name => name, StringComparer.Ordinal)), cancellationToken);

    public async Task<ThemePreset?> ReadThemeAsync(CancellationToken cancellationToken)
    {
        var themePath = Path.ChangeExtension(path, "theme.json");
        if (!File.Exists(themePath)) return null;
        using var file = File.OpenRead(themePath);
        if (file.Length > 16384) throw new ProviderException("invalid_configuration");
        try
        {
            using var json = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ThemeCatalog.Parse(json.RootElement);
        }
        catch (ThemeValidationException exception) { throw new ProviderException(exception.Code); }
        catch (JsonException) { throw new ProviderException("invalid_configuration"); }
    }

    public Task WriteThemeAsync(ThemePreset theme, CancellationToken cancellationToken) =>
        WriteJsonAsync(Path.ChangeExtension(path, "theme.json"), JsonWire.Serialize(new
        {
            name = theme.Name,
            background = theme.Background,
            foreground = theme.Foreground,
            accent = theme.Accent,
            chatFont = theme.ChatFont,
            codeFont = theme.CodeFont,
            fontSize = theme.FontSize,
            density = theme.Density,
            borderRadius = theme.BorderRadius,
            modeAccent = theme.ModeAccent
        }), cancellationToken);

    public ThemePreset ValidateThemeJson(JsonElement json)
    {
        try { return ThemeCatalog.Parse(json); }
        catch (ThemeValidationException exception) { throw new ProviderException(exception.Code); }
    }

    public async Task<string> ReadLogLevelAsync(CancellationToken cancellationToken)
    {
        var logPath = Path.ChangeExtension(path, "log.json");
        if (!File.Exists(logPath)) return "Error";
        using var file = File.OpenRead(logPath);
        if (file.Length > 4096) throw new ProviderException("invalid_configuration");
        using var json = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ProviderException("invalid_configuration");
        var level = JsonWire.String(json.RootElement, "level");
        if (level is null || !DiagnosticLevels.IsValid(level) || json.RootElement.EnumerateObject().Count() != 1)
            throw new ProviderException("invalid_configuration");
        return level;
    }

    public Task WriteLogLevelAsync(string level, CancellationToken cancellationToken)
    {
        if (!DiagnosticLevels.IsValid(level)) throw new ProviderException("invalid_configuration");
        return WriteJsonAsync(Path.ChangeExtension(path, "log.json"), JsonWire.Serialize(new { level }), cancellationToken);
    }

    public Task ClearThemeAsync(CancellationToken cancellationToken)
    {
        var themePath = Path.ChangeExtension(path, "theme.json");
        return Task.Run(() => { if (File.Exists(themePath)) File.Delete(themePath); }, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, ModelOverride>> ReadModelOverridesAsync(CancellationToken cancellationToken)
    {
        var modelPath = Path.ChangeExtension(path, "models.json");
        if (!File.Exists(modelPath)) return new Dictionary<string, ModelOverride>(StringComparer.Ordinal);
        using var file = File.OpenRead(modelPath);
        if (file.Length == 0) return new Dictionary<string, ModelOverride>(StringComparer.Ordinal);
        if (file.Length > 262144) throw new ProviderException("invalid_configuration");
        JsonDocument json;
        try { json = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { throw new ProviderException("invalid_configuration"); }
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ProviderException("invalid_configuration");
        var result = new Dictionary<string, ModelOverride>(StringComparer.Ordinal);
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) throw new ProviderException("invalid_configuration");
            var displayName = JsonWire.String(property.Value, "displayName");
            var tools = JsonWire.Get(property.Value, "tools");
            var vision = JsonWire.Get(property.Value, "vision");
            var reasoning = JsonWire.Get(property.Value, "reasoning");
            var enabled = JsonWire.Get(property.Value, "enabled");
            var efforts = JsonWire.Array(JsonWire.Get(property.Value, "efforts"));
            if (vision.ValueKind != JsonValueKind.True && vision.ValueKind != JsonValueKind.False) throw new ProviderException("invalid_configuration");
            if (tools.ValueKind != JsonValueKind.True && tools.ValueKind != JsonValueKind.False) throw new ProviderException("invalid_configuration");
            if (reasoning.ValueKind != JsonValueKind.True && reasoning.ValueKind != JsonValueKind.False) throw new ProviderException("invalid_configuration");
            if (enabled.ValueKind != JsonValueKind.Undefined && enabled.ValueKind != JsonValueKind.True && enabled.ValueKind != JsonValueKind.False) throw new ProviderException("invalid_configuration");
            foreach (var effort in efforts)
                if (effort.ValueKind != JsonValueKind.String || !new[] { "off", "minimal", "low", "medium", "high", "xhigh", "max" }.Contains(effort.GetString()!, StringComparer.OrdinalIgnoreCase)) throw new ProviderException("invalid_configuration");
            result[property.Name] = new ModelOverride(displayName ?? string.Empty, vision.GetBoolean(), tools.GetBoolean(), reasoning.GetBoolean(), efforts.Select(value => value.GetString()!).ToArray(), enabled.ValueKind == JsonValueKind.Undefined ? true : enabled.GetBoolean());
        }
        json.Dispose();
        return result;
    }
    public Task WriteModelOverrideAsync(string key, ModelOverride overrideValue, CancellationToken cancellationToken)
    {
        return WriteModelOverrideEntryAsync(key, overrideValue, cancellationToken);
    }

    private async Task WriteModelOverrideEntryAsync(string key, ModelOverride overrideValue, CancellationToken cancellationToken)
    {
        await ModelsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var modelPath = Path.ChangeExtension(path, "models.json");
            var overrides = new Dictionary<string, ModelOverride>(StringComparer.Ordinal);
            if (File.Exists(modelPath))
            {
                var existing = await ReadModelOverridesAsync(cancellationToken).ConfigureAwait(false);
                foreach (var pair in existing) overrides[pair.Key] = pair.Value;
            }
            overrides[key] = overrideValue;
            var payload = overrides.ToDictionary(pair => pair.Key, pair => new
            {
                displayName = pair.Value.DisplayName,
                vision = pair.Value.Vision,
                tools = pair.Value.Tools,
                reasoning = pair.Value.Reasoning,
                efforts = pair.Value.Efforts,
                enabled = pair.Value.Enabled
            });
            await WriteJsonAsync(modelPath, JsonWire.Serialize(payload), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ModelsLock.Release();
        }
    }

    public async Task RemoveModelOverridesAsync(string configurationId, CancellationToken cancellationToken)
    {
        await ModelsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var modelPath = Path.ChangeExtension(path, "models.json");
            if (!File.Exists(modelPath)) return;
            var existing = await ReadModelOverridesAsync(cancellationToken).ConfigureAwait(false);
            var filtered = existing.Where(pair => !pair.Key.StartsWith(configurationId + "/", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (filtered.Count == existing.Count) return;
            var payload = filtered.ToDictionary(pair => pair.Key, pair => new
            {
                displayName = pair.Value.DisplayName,
                vision = pair.Value.Vision,
                tools = pair.Value.Tools,
                reasoning = pair.Value.Reasoning,
                efforts = pair.Value.Efforts,
                enabled = pair.Value.Enabled
            });
            await WriteJsonAsync(modelPath, JsonWire.Serialize(payload), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ModelsLock.Release();
        }
    }

    private static async Task WriteJsonAsync(string path, string json, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteAsync(json).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
