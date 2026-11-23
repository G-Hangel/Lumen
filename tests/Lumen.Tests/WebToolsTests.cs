using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Tools;
using Xunit;

namespace Lumen.Tests;

public class WebToolsTests
{
    private const string SearchHtml = """
        <html><body>
        <div class="result">
          <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage&amp;rut=abc">Example &amp; Page</a>
          <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage">A <b>snippet</b> about the page.</a>
        </div>
        <div class="result">
          <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fother.org%2Fdoc">Other Doc</a>
          <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fother.org%2Fdoc">Second snippet.</a>
        </div>
        <div class="result">
          <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=javascript%3Aalert(1)">Bad</a>
          <a class="result__snippet" href="#">bad snippet</a>
        </div>
        </body></html>
        """;

    private const string PageHtml = """
        <html><head><title>My &amp; Title</title><style>body{color:red}</style></head>
        <body><script>alert('x')</script><h1>Heading</h1><p>Hello <b>world</b>.</p><p>Second line.</p></body></html>
        """;

    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<string> Urls = new();
        public Func<string, HttpResponseMessage> Responder { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SearchHtml, Encoding.UTF8, "text/html")
        };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Responder(request.RequestUri!.AbsoluteUri));
        }
    }

    private static ToolInvocation Call(string name, string arguments) =>
        new ToolInvocation(Guid.NewGuid().ToString("D"), name, JsonDocument.Parse(arguments).RootElement.Clone());

    private static JsonElement Content(ToolResult result) => JsonDocument.Parse(result.Content).RootElement.Clone();

    [Fact]
    public async Task SearchParsesTitlesUrlsAndSnippetsAndUnwrapsRedirects()
    {
        var handler = new FakeHandler();
        var tools = new WebTools(null, new HttpClient(handler));
        var result = await tools.ExecuteAsync(Call("web_search", "{\"query\":\"example query\"}"), CancellationToken.None);
        Assert.False(result.IsError, result.Content);
        using var document = JsonDocument.Parse(result.Content);
        var root = document.RootElement;
        Assert.Equal("example query", root.GetProperty("query").GetString());
        var results = root.GetProperty("results").EnumerateArray().ToArray();
        // The javascript: target must be dropped (negative control), leaving two real results.
        Assert.Equal(2, results.Length);
        Assert.Equal("https://example.com/page", results[0].GetProperty("url").GetString());
        Assert.Equal("Example & Page", results[0].GetProperty("title").GetString());
        Assert.Equal("A snippet about the page.", results[0].GetProperty("snippet").GetString());
        Assert.Equal("https://other.org/doc", results[1].GetProperty("url").GetString());
        Assert.Contains("q=example%20query", handler.Urls.Single());
    }

    [Fact]
    public async Task SearchHonorsMaxResults()
    {
        var tools = new WebTools(null, new HttpClient(new FakeHandler()));
        var result = await tools.ExecuteAsync(Call("web_search", "{\"query\":\"q\",\"maxResults\":1}"), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(1, Content(result).GetProperty("results").GetArrayLength());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"query\":\"\"}")]
    [InlineData("{\"query\":\"ok\",\"maxResults\":0}")]
    [InlineData("{\"query\":\"ok\",\"maxResults\":11}")]
    [InlineData("{\"query\":\"ok\",\"extra\":1}")]
    [InlineData("{\"query\":5}")]
    public async Task SearchRejectsInvalidArguments(string arguments)
    {
        var tools = new WebTools(null, new HttpClient(new FakeHandler()));
        var result = await tools.ExecuteAsync(Call("web_search", arguments), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("invalid_arguments", Content(result).GetProperty("code").GetString());
    }

    [Fact]
    public async Task FetchStripsScriptsStylesAndReturnsTitleAndText()
    {
        var handler = new FakeHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PageHtml, Encoding.UTF8, "text/html") } };
        var tools = new WebTools(null, new HttpClient(handler));
        var result = await tools.ExecuteAsync(Call("web_fetch", "{\"url\":\"https://example.com/page\"}"), CancellationToken.None);
        Assert.False(result.IsError, result.Content);
        var root = Content(result);
        Assert.Equal("My & Title", root.GetProperty("title").GetString());
        var text = root.GetProperty("text").GetString()!;
        Assert.Contains("Heading", text);
        Assert.Contains("Hello world.", text);
        Assert.Contains("Second line.", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("color:red", text);
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task FetchTruncatesToMaxChars()
    {
        var handler = new FakeHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PageHtml, Encoding.UTF8, "text/html") } };
        var tools = new WebTools(null, new HttpClient(handler));
        var result = await tools.ExecuteAsync(Call("web_fetch", "{\"url\":\"https://example.com/\",\"maxChars\":5}"), CancellationToken.None);
        Assert.False(result.IsError);
        var root = Content(result);
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(5, root.GetProperty("text").GetString()!.Length);
    }

    [Theory]
    [InlineData("http://localhost/x")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://10.0.0.5/x")]
    [InlineData("http://192.168.1.1/x")]
    [InlineData("http://172.16.0.1/x")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/x")]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("ftp://example.com/x")]
    [InlineData("http://metadata.google.internal/x")]
    public async Task FetchBlocksPrivateAndNonHttpTargets(string url)
    {
        var handler = new FakeHandler();
        var tools = new WebTools(null, new HttpClient(handler));
        var result = await tools.ExecuteAsync(Call("web_fetch", JsonSerializer.Serialize(new { url })), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("unsafe_url", Content(result).GetProperty("code").GetString());
        Assert.Empty(handler.Urls); // never reached the network
    }

    [Fact]
    public async Task FetchMapsHttpFailureToNetworkError()
    {
        var handler = new FakeHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var tools = new WebTools(null, new HttpClient(handler));
        var result = await tools.ExecuteAsync(Call("web_fetch", "{\"url\":\"https://example.com/missing\"}"), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("network_error", Content(result).GetProperty("code").GetString());
    }

    [Fact]
    public async Task DecoratorDelegatesNonWebToolsToInner()
    {
        var inner = new StubRead();
        var tools = new WebTools(inner, new HttpClient(new FakeHandler()));
        var result = await tools.ExecuteAsync(Call("read_file", "{\"path\":\"a.txt\"}"), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal("inner", Content(result).GetProperty("from").GetString());
        Assert.Contains(tools.Specs, spec => spec.Name == "web_search");
        Assert.Contains(tools.Specs, spec => spec.Name == "web_fetch");
        Assert.Contains(tools.Specs, spec => spec.Name == "read_file");
    }

    [Fact]
    public async Task WithoutInnerNonWebToolIsUnknown()
    {
        var tools = new WebTools(null, new HttpClient(new FakeHandler()));
        var result = await tools.ExecuteAsync(Call("read_file", "{\"path\":\"a.txt\"}"), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("unknown_tool", Content(result).GetProperty("code").GetString());
    }

    private sealed class StubRead : IReadToolExecutor
    {
        public IReadOnlyList<ToolSpec> Specs { get; } = new[]
        {
            new ToolSpec("read_file", "stub", JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}").RootElement.Clone())
        };
        public Task<IReadToolExecutor> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult<IReadToolExecutor>(this);
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolResult(invocation.Id, false, "{\"from\":\"inner\"}"));
    }
}