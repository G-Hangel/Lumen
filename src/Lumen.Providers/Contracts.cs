using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public enum ProviderKind { OpenAI, OpenRouter, Anthropic, Gemini, Ollama, Azure, LMStudio }

public sealed class ProviderConfiguration
{
    public ProviderConfiguration(string id, string providerId, ProviderKind kind, string baseUrl,
        string? selectedModelId = null, IReadOnlyList<string>? deploymentNames = null)
    {
        if (!Guid.TryParseExact(id, "D", out var guid) || guid == Guid.Empty) throw new ArgumentException("Invalid configuration id.", nameof(id));
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("Provider id is required.", nameof(providerId));
        if (!Enum.IsDefined(typeof(ProviderKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use HTTPS, or HTTP on loopback, without credentials or query parameters.", nameof(baseUrl));
        Id = guid.ToString("D");
        ProviderId = providerId;
        Kind = kind;
        BaseUrl = uri.AbsoluteUri;
        SelectedModelId = selectedModelId;
        DeploymentNames = deploymentNames?.ToArray() ?? Array.Empty<string>();
    }
    public string Id { get; }
    public string ProviderId { get; }
    public ProviderKind Kind { get; }
    public string BaseUrl { get; }
    public string? SelectedModelId { get; }
    public IReadOnlyList<string> DeploymentNames { get; }
}

public sealed class CatalogResult
{
    public CatalogResult(IReadOnlyList<ModelInfo> models, bool? credentialValid = true,
        string? errorCode = null, TimeSpan? retryAfter = null)
    {
        Models = models;
        CredentialValid = credentialValid;
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
    }
    public IReadOnlyList<ModelInfo> Models { get; }
    public bool? CredentialValid { get; }
    public string? ErrorCode { get; }
    public TimeSpan? RetryAfter { get; }
}

public sealed class ChatRequest
{
    public ChatRequest(ProviderConfiguration configuration, ModelInfo model, IReadOnlyList<Message> messages,
        ReasoningIntent reasoningIntent = ReasoningIntent.Off, IReadOnlyList<ToolSpec>? tools = null,
        int maxOutputTokens = 1024)
    {
        if (model.ProviderId != configuration.ProviderId) throw new ArgumentException("Provider mismatch.", nameof(model));
        if (maxOutputTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        Configuration = configuration;
        Model = model;
        Messages = messages;
        ReasoningIntent = reasoningIntent;
        Tools = tools ?? Array.Empty<ToolSpec>();
        MaxOutputTokens = Math.Min(maxOutputTokens, model.MaxOutputTokens ?? maxOutputTokens);
    }
    public ProviderConfiguration Configuration { get; }
    public ModelInfo Model { get; }
    public IReadOnlyList<Message> Messages { get; }
    public ReasoningIntent ReasoningIntent { get; }
    public IReadOnlyList<ToolSpec> Tools { get; }
    public int MaxOutputTokens { get; }
}

public interface IModelCatalog
{
    Task<CatalogResult> DiscoverAsync(ProviderConfiguration configuration, string? credential, CancellationToken cancellationToken);
}

public interface IChatProvider
{
    Task StreamAsync(ChatRequest request, string? credential, Func<StreamEvent, Task> onEvent, CancellationToken cancellationToken);
}

public interface ICredentialStore
{
    Task<string?> ReadAsync(string configurationId, CancellationToken cancellationToken);
    Task WriteAsync(string configurationId, string secret, CancellationToken cancellationToken);
    Task DeleteAsync(string configurationId, CancellationToken cancellationToken);
}

public sealed class NativeModel
{
    public NativeModel(string id, string? displayName = null) { Id = id; DisplayName = displayName; }
    public string Id { get; }
    public string? DisplayName { get; }
    public Dictionary<ModelCapabilities, bool> Capabilities { get; } = new Dictionary<ModelCapabilities, bool>();
    public ReasoningSupport? Reasoning { get; set; }
    public int? ContextWindowTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
}
