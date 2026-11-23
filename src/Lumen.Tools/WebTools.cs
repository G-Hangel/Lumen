using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Tools;

// Workspace-independent read tools: web_search (DuckDuckGo HTML, no API key) and web_fetch (page text).
// Decorates an optional inner read executor so workspace tools keep working unchanged.
public sealed class WebTools : IReadToolExecutor
{
    public const string SearchTool = "web_search";
    public const string FetchTool = "web_fetch";
    private const int MaxSearchResults = 10;
    private const int MaxFetchChars = 65536;
    private const int MaxHtmlBytes = 2 * 1024 * 1024;
    private const int MaxSnippetChars = 512;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly Regex ResultLink = new Regex("<a[^>]*class=\"result__a\"[^>]*href=\"([^\"]*)\"[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex ResultSnippet = new Regex("<a[^>]*class=\"result__snippet\"[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex TitleTag = new Regex("<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ScriptOrStyle = new Regex("<(script|style|noscript|template)\\b[^>]*>.*?</\\1\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Comment = new Regex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex BlockTag = new Regex("</?(p|div|br|li|ul|ol|h[1-6]|tr|td|th|section|article|header|footer|nav|blockquote|pre)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AnyTag = new Regex("<[^>]+>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new Regex("[ \\t\\r\\n\\f\\v]+", RegexOptions.CultureInvariant);

    // Shared across instances so per-turn decorators do not exhaust sockets.
    private static readonly HttpClient SharedHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = RequestTimeout };
    private readonly HttpClient http;
    private readonly IReadToolExecutor? inner;

    public WebTools(IReadToolExecutor? inner = null, HttpClient? http = null)
    {
        this.inner = inner;
        this.http = http ?? SharedHttp;
    }

    public IReadOnlyList<ToolSpec> Specs
    {
        get
        {
            var specs = new List<ToolSpec>
            {
                Spec(SearchTool, "Search the public web (DuckDuckGo) and return titles, URLs and snippets. Read-only; no API key required.",
                    "{\"query\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"maxResults\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10}}", "[\"query\"]"),
                Spec(FetchTool, "Fetch a public http/https page and return its readable text (scripts/styles removed). Read-only.",
                    "{\"url\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":2048},\"maxChars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":65536}}", "[\"url\"]")
            };
            if (inner is not null) specs.AddRange(inner.Specs);
            return specs;
        }
    }

    public Task<IReadToolExecutor> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult<IReadToolExecutor>(this);

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        try
        {
            if (invocation.Name == SearchTool) return await SearchAsync(invocation, cancellationToken).ConfigureAwait(false);
            if (invocation.Name == FetchTool) return await FetchAsync(invocation, cancellationToken).ConfigureAwait(false);
            if (inner is null) return Error(invocation, "unknown_tool");
            return await inner.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolException exception) { return Error(invocation, exception.Code); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException || exception is UriFormatException || exception is ArgumentException)
        {
            return Error(invocation, "network_error");
        }
    }

    private async Task<ToolResult> SearchAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var arguments = Validate(invocation);
        var query = arguments.GetProperty("query").GetString()!;
        var maxResults = arguments.TryGetProperty("maxResults", out var limit) ? limit.GetInt32() : MaxSearchResults;
        var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
        var html = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var links = ResultLink.Matches(html);
        var snippets = ResultSnippet.Matches(html);
        var results = new List<object>();
        for (var index = 0; index < links.Count && results.Count < maxResults; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var link = links[index];
            var resolved = ResolveResultUrl(link.Groups[1].Value);
            if (resolved is null) continue;
            var snippet = index < snippets.Count ? CleanText(snippets[index].Groups[1].Value) : string.Empty;
            if (snippet.Length > MaxSnippetChars) snippet = snippet.Substring(0, MaxSnippetChars);
            results.Add(new { title = CleanText(link.Groups[2].Value), url = resolved, snippet });
        }
        return new ToolResult(invocation.Id, false, JsonSerializer.Serialize(new { query, results }));
    }

    private async Task<ToolResult> FetchAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var arguments = Validate(invocation);
        var urlText = arguments.GetProperty("url").GetString()!;
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri)) throw new ToolException("invalid_arguments");
        GuardUrl(uri);
        var maxChars = arguments.TryGetProperty("maxChars", out var limit) ? limit.GetInt32() : 20000;
        var html = await GetStringAsync(uri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
        var titleMatch = TitleTag.Match(html);
        var title = titleMatch.Success ? CleanText(titleMatch.Groups[1].Value) : string.Empty;
        var text = ExtractText(html);
        var truncated = text.Length > maxChars;
        if (truncated) text = text.Substring(0, maxChars);
        return new ToolResult(invocation.Id, false, JsonSerializer.Serialize(new { url = uri.AbsoluteUri, title, text, truncated }));
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ToolException("network_error");
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxHtmlBytes) throw new ToolException("output_too_large");
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonElement Validate(ToolInvocation invocation)
    {
        if (invocation.Status != ToolCallStatus.Pending) throw new ToolException("invalid_arguments");
        var arguments = invocation.Arguments;
        if (arguments.ValueKind != JsonValueKind.Object) throw new ToolException("invalid_arguments");
        var isSearch = invocation.Name == SearchTool;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in arguments.EnumerateObject())
        {
            if (!seen.Add(field.Name)) throw new ToolException("invalid_arguments");
            if (field.Name == (isSearch ? "query" : "url"))
            {
                if (field.Value.ValueKind != JsonValueKind.String) throw new ToolException("invalid_arguments");
                var value = field.Value.GetString()!;
                if (value.Length < 1 || value.Length > (isSearch ? 512 : 2048)) throw new ToolException("invalid_arguments");
                ReadToolCatalog.ValidateText(value);
            }
            else if (field.Name == (isSearch ? "maxResults" : "maxChars"))
            {
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var number) ||
                    number < 1 || number > (isSearch ? MaxSearchResults : MaxFetchChars)) throw new ToolException("invalid_arguments");
            }
            else throw new ToolException("invalid_arguments");
        }
        if (!seen.Contains(isSearch ? "query" : "url")) throw new ToolException("invalid_arguments");
        return arguments.Clone();
    }

    // DuckDuckGo wraps targets in /l/?uddg=<encoded>; unwrap to the real URL and reject non-http(s) targets.
    private static string? ResolveResultUrl(string href)
    {
        var decoded = WebUtility.HtmlDecode(href);
        if (decoded.StartsWith("//", StringComparison.Ordinal)) decoded = "https:" + decoded;
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri)) return null;
        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/l/", StringComparison.Ordinal))
        {
            var query = uri.Query.TrimStart('?');
            foreach (var pair in query.Split('&'))
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0 || pair.Substring(0, equals) != "uddg") continue;
                var target = Uri.UnescapeDataString(pair.Substring(equals + 1));
                if (Uri.TryCreate(target, UriKind.Absolute, out var real) && (real.Scheme == Uri.UriSchemeHttp || real.Scheme == Uri.UriSchemeHttps))
                    return real.AbsoluteUri;
            }
            return null;
        }
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;
    }

    // SSRF defense: only public http/https; block loopback, private, link-local and metadata endpoints.
    private static void GuardUrl(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) throw new ToolException("unsafe_url");
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("unsafe_url");
        if (IPAddress.TryParse(host, out var address) && IsPrivateAddress(address)) throw new ToolException("unsafe_url");
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            var bytes6 = address.GetAddressBytes();
            if ((bytes6[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique-local
            if (address.IsIPv4MappedToIPv6) return IsPrivateAddress(address.MapToIPv4());
            return false;
        }
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127) return true;
        if (bytes[0] == 169 && bytes[1] == 254) return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true; // CGNAT
        if (bytes[0] >= 224) return true; // multicast + reserved
        return false;
    }

    private static string ExtractText(string html)
    {
        var text = ScriptOrStyle.Replace(html, " ");
        text = Comment.Replace(text, " ");
        text = BlockTag.Replace(text, "\n");
        // Inline tags carry no word boundary; dropping them keeps "Hello <b>world</b>." readable.
        text = AnyTag.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Whitespace.Replace(text, " ");
        // Preserve paragraph breaks introduced by block tags before collapsing spaces.
        text = Regex.Replace(text, " ?\n ?", "\n");
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string CleanText(string fragment)
    {
        var text = AnyTag.Replace(fragment, " ");
        text = WebUtility.HtmlDecode(text);
        return Whitespace.Replace(text, " ").Trim();
    }

    private static ToolSpec Spec(string name, string description, string properties, string required)
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":" + properties + ",\"required\":" + required + "}");
        return new ToolSpec(name, description, document.RootElement.Clone());
    }

    private static ToolResult Error(ToolInvocation invocation, string code) =>
        new ToolResult(invocation.Id, true, JsonSerializer.Serialize(new { code }));
}