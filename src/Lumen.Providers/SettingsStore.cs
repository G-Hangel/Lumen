using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lumen.Providers;

public sealed class SettingsStore
{
    private readonly string path;
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
