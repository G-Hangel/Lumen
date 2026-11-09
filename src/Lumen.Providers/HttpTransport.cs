using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public sealed class ProviderException : Exception
{
    public ProviderException(string code) : base(code) { Code = code; }
    public string Code { get; }
}

public sealed class HttpTransport
{
    private readonly HttpClient client;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    public HttpTransport(HttpClient client, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.client = client;
        this.delay = delay ?? Task.Delay;
    }

    public async Task<HttpResponseMessage> SendAsync(ProviderConfiguration configuration, string path,
        string? credential, HttpMethod method, string? body, CancellationToken cancellationToken)
    {
        var uri = new Uri(new Uri(configuration.BaseUrl), path);
        if (!SameOrigin(new Uri(configuration.BaseUrl), uri)) throw new ProviderException("unsafe_url");
        if (credential?.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ProviderException("invalid_key");
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            if (!string.IsNullOrEmpty(credential))
            {
                var header = configuration.Kind == ProviderKind.Anthropic ? "x-api-key" :
                    configuration.Kind == ProviderKind.Gemini ? "x-goog-api-key" :
                    configuration.Kind == ProviderKind.Azure ? "api-key" : "Authorization";
                request.Headers.TryAddWithoutValidation(header, header == "Authorization" ? "Bearer " + credential : credential);
            }
            if (configuration.Kind == ProviderKind.Anthropic) request.Headers.TryAddWithoutValidation("anthropic-version", "2026-10-21");
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException) { throw new ProviderException("network_error"); }
            if ((int)response.StatusCode != 429 || method != HttpMethod.Get || attempt == 2) return response;
            var wait = RetryAfter(response) ?? TimeSpan.FromMilliseconds(500 * (1 << attempt) + new Random().Next(100, 300));
            response.Dispose();
            await delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    public static bool SameOrigin(Uri left, Uri right) => left.Scheme == right.Scheme && left.Host == right.Host && left.Port == right.Port && string.IsNullOrEmpty(right.UserInfo);
    public static string ErrorCode(HttpStatusCode status) => (int)status switch
    {
        401 => "invalid_key", 403 => "permission_denied", 404 => "invalid_url", 429 => "rate_limited",
        402 => "insufficient_credit", >= 300 and < 400 => "redirect_rejected", _ => "provider_error"
    };
    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        var wait = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
        return wait is null ? null : wait.Value < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }
    // The wire contract carries whole seconds; round up so the UI never under-reports the provider's wait.
    public static long? RetryAfterSeconds(HttpResponseMessage response) => RetryAfter(response) is TimeSpan wait ? (long)Math.Ceiling(wait.TotalSeconds) : null;
    public static CatalogResult CatalogError(HttpResponseMessage response) => new CatalogResult(Array.Empty<ModelInfo>(),
        (int)response.StatusCode == 429 || (int)response.StatusCode == 402 ? true :
        (int)response.StatusCode == 401 || (int)response.StatusCode == 403 ? false : (bool?)null,
        ErrorCode(response.StatusCode), RetryAfter(response));

    internal const int MaxResponseBytes = 16 * 1024 * 1024;
    internal const int MaxCatalogEntries = 10_000;

    internal static int CheckCatalogEntries(JsonElement entries, int totalEntries = 0)
    {
        if (entries.ValueKind != JsonValueKind.Array) throw new ProviderException("invalid_response");
        var count = entries.GetArrayLength();
        if (count > MaxCatalogEntries - totalEntries) throw new ProviderException("response_too_large");
        return totalEntries + count;
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new ProviderException("response_too_large");
            using var responseCancellation = cancellationToken.Register(response.Dispose);
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var streamCancellation = cancellationToken.Register(stream.Dispose);
            using var payload = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Read one sentinel byte beyond the limit, even when Content-Length is absent or false.
                var remaining = MaxResponseBytes - (int)payload.Length;
                var count = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining + 1), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0) break;
                if (count > remaining) throw new ProviderException("response_too_large");
                payload.Write(buffer, 0, count);
            }
            payload.Position = 0;
            return await JsonDocument.ParseAsync(payload, new JsonDocumentOptions { MaxDepth = 64 }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception exception) when (exception is JsonException || exception is IOException || exception is HttpRequestException || exception is ObjectDisposedException)
        { throw new ProviderException("invalid_response"); }
    }
}

internal static class JsonWire
{
    internal static JsonElement Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    internal static string? String(JsonElement value, string name) => Get(value, name) is var found && found.ValueKind == JsonValueKind.String ? found.GetString() : null;
    internal static int? Int(JsonElement value, string name) => Get(value, name) is var found && found.TryGetInt32Safe(out var number) ? number : (int?)null;
    internal static bool TryGetInt32Safe(this JsonElement element, out int value) { value = 0; return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value); }
    internal static IEnumerable<JsonElement> Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : System.Array.Empty<JsonElement>();
    internal static string Serialize(object value) => JsonSerializer.Serialize(value);
    internal static FinishReason Finish(string? reason) => reason switch
    {
        "stop" or "end_turn" or "STOP" => FinishReason.Stop,
        "length" or "max_tokens" or "MAX_TOKENS" => FinishReason.MaxTokens,
        "tool_calls" or "tool_use" => FinishReason.ToolUse,
        "content_filter" or "SAFETY" => FinishReason.ContentFiltered,
        _ => FinishReason.Other
    };
}

internal static class StreamFrames
{
    internal static async Task ReadAsync(Stream stream, bool ndjson, Func<string?, string, Task> onFrame, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(stream.Dispose);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var data = new StringBuilder();
        string? eventName = null;
        async Task FlushLine()
        {
            var text = line.ToString().TrimEnd('\r');
            line.Clear();
            if (ndjson) { if (text.Length > 0) await onFrame(null, text).ConfigureAwait(false); return; }
            if (text.Length == 0)
            {
                if (data.Length > 0) await onFrame(eventName, data.ToString().TrimEnd('\n')).ConfigureAwait(false);
                data.Clear(); eventName = null;
            }
            else if (text.StartsWith("data:", StringComparison.Ordinal)) data.Append(text.Substring(5).TrimStart(' ')).Append('\n');
            else if (text.StartsWith("event:", StringComparison.Ordinal)) eventName = text.Substring(6).TrimStart(' ');
            if (data.Length > 1024 * 1024) throw new ProviderException("stream_too_large");
        }
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n') await FlushLine().ConfigureAwait(false);
                    else line.Append(buffer[i]);
                    if (line.Length > 1024 * 1024) throw new ProviderException("stream_too_large");
                }
            }
            if (line.Length > 0) await FlushLine().ConfigureAwait(false);
            if (!ndjson && data.Length > 0) await onFrame(eventName, data.ToString().TrimEnd('\n')).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }
}
