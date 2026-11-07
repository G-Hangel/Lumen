using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Xunit;

namespace Lumen.Tests;

public sealed class ProviderFactoryTests
{
    private const string ConfigurationId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string OtherConfigurationId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string ModelId = "native-model";
    private const string AnthropicOptions = """
        {"data":[{"id":"native-model","capabilities":{
          "thinking":{"supported":true,"types":{"adaptive":{"supported":true}}},
          "effort":{"supported":true,"low":{"supported":true},"high":{"supported":true},"max":{"supported":true}}
        }}],"has_more":false}
        """;
    private const string OllamaOptions = """
        {"capabilities":["completion","thinking"],"thinking":{"values":[false,true]}}
        """;

    [Theory]
    [InlineData(ProviderKind.OpenAI, typeof(OpenAIProvider))]
    [InlineData(ProviderKind.OpenRouter, typeof(OpenRouterProvider))]
    [InlineData(ProviderKind.Anthropic, typeof(AnthropicProvider))]
    [InlineData(ProviderKind.Gemini, typeof(GeminiProvider))]
    [InlineData(ProviderKind.Ollama, typeof(OllamaProvider))]
    [InlineData(ProviderKind.Azure, typeof(AzureProvider))]
    [InlineData(ProviderKind.LMStudio, typeof(LMStudioProvider))]
    public async Task ConcurrentCatalogAndChatShareOneAdapterPerFactory(ProviderKind kind, Type expectedType)
    {
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No HTTP expected."));
        using var client = new HttpClient(handler);
        var factory = Factory(client);
        var adapters = await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
            index % 2 == 0 ? (object)factory.Catalog(kind) : factory.Chat(kind))));
        Assert.All(adapters, adapter =>
        {
            Assert.IsType(expectedType, adapter);
            Assert.Same(adapters[0], adapter);
        });
        Assert.NotSame(adapters[0], Factory(client).Catalog(kind));
        Assert.NotSame(factory.Catalog(kind), factory.Catalog(kind == ProviderKind.OpenAI ? ProviderKind.Anthropic : ProviderKind.OpenAI));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task AnthropicNativeAdaptiveAndEffortSurviveFactoryCatalogThenChat()
    {
        using var handler = new FakeHandler(_ => AnthropicOptions);
        using var client = new HttpClient(handler);
        var factory = Factory(client);
        var configuration = Configuration(ProviderKind.Anthropic);
        var model = await Discover(factory, configuration);
        var selected = new ProviderConfiguration(configuration.Id, configuration.ProviderId, configuration.Kind, configuration.BaseUrl, ModelId);
        using var payload = await Chat(factory, handler, selected, model);
        AssertAdaptiveMax(payload.RootElement);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic, "configuration")]
    [InlineData(ProviderKind.Anthropic, "endpoint")]
    [InlineData(ProviderKind.Anthropic, "path")]
    [InlineData(ProviderKind.Anthropic, "provider")]
    [InlineData(ProviderKind.Anthropic, "kind")]
    [InlineData(ProviderKind.Ollama, "configuration")]
    [InlineData(ProviderKind.Ollama, "endpoint")]
    [InlineData(ProviderKind.Ollama, "path")]
    [InlineData(ProviderKind.Ollama, "provider")]
    [InlineData(ProviderKind.Ollama, "kind")]
    public async Task NativeMetadataDoesNotCrossConfigurationIdentity(ProviderKind kind, string difference)
    {
        var secondDiscovery = false;
        using var handler = new FakeHandler(_ => kind == ProviderKind.Anthropic
            ? secondDiscovery ? AnthropicOptions.Replace("\"max\":{\"supported\":true}", "\"max\":{\"supported\":false}") : AnthropicOptions
            : secondDiscovery ? OllamaOptions.Replace("false,true", "false,\"low\",\"high\"") : OllamaOptions);
        using var client = new HttpClient(handler);
        var factory = Factory(client);
        var first = Configuration(kind);
        var second = DifferentConfiguration(first, difference);
        var firstModel = await Discover(factory, first);
        secondDiscovery = true;
        // Use the same adapter directly for the kind boundary; real factory kinds are isolated too.
        var catalog = factory.Catalog(kind);
        var secondResult = await catalog.DiscoverAsync(second, null, CancellationToken.None);
        Assert.Null(secondResult.ErrorCode);
        var secondModel = Assert.Single(secondResult.Models);
        using var firstPayload = await Chat(factory, handler, first, firstModel);
        if (kind == ProviderKind.Anthropic) AssertAdaptiveMax(firstPayload.RootElement);
        else Assert.True(firstPayload.RootElement.GetProperty("think").GetBoolean());
        using var secondPayload = await Chat(factory, handler, second, secondModel, adapterKind: kind);
        if (kind == ProviderKind.Anthropic)
            Assert.Equal("high", secondPayload.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        else Assert.Equal("high", secondPayload.RootElement.GetProperty("think").GetString());
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("endpoint")]
    public async Task AnthropicUndiscoveredConfigurationCannotBorrowNativeKnobs(string difference)
    {
        using var handler = new FakeHandler(_ => AnthropicOptions);
        using var client = new HttpClient(handler);
        var factory = Factory(client);
        var configuration = Configuration(ProviderKind.Anthropic);
        var model = await Discover(factory, configuration);
        using var payload = await Chat(factory, handler, DifferentConfiguration(configuration, difference), model);
        AssertNoAnthropicKnobs(payload.RootElement);
        using var otherSession = await Chat(Factory(client), handler, configuration, model);
        AssertNoAnthropicKnobs(otherSession.RootElement);
    }

    [Fact]
    public async Task NativeMetadataIsAlsoIsolatedByModel()
    {
        using var handler = new FakeHandler(_ => AnthropicOptions.Replace("}],\"has_more\"", "},{\"id\":\"other-model\",\"capabilities\":{\"thinking\":{\"supported\":true,\"types\":{\"adaptive\":{\"supported\":true}}},\"effort\":{\"supported\":false}}}],\"has_more\""));
        using var client = new HttpClient(handler);
        var factory = Factory(client);
        var configuration = Configuration(ProviderKind.Anthropic);
        var result = await factory.Catalog(configuration.Kind).DiscoverAsync(configuration, null, CancellationToken.None);
        Assert.Null(result.ErrorCode);
        Assert.Equal(2, result.Models.Count);
        using var first = await Chat(factory, handler, configuration, result.Models[0]);
        AssertAdaptiveMax(first.RootElement);
        using var second = await Chat(factory, handler, configuration, result.Models[1]);
        Assert.Equal("adaptive", second.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(second.RootElement.TryGetProperty("output_config", out _));
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.Ollama)]
    public async Task SuccessfulRediscoveryReplacesRemovedAndChangedNativeMetadata(ProviderKind kind)
    {
        var generation = 0;
        using var handler = new FakeHandler(_ => kind == ProviderKind.Anthropic
            ? generation switch
            {
                0 => AnthropicOptions,
                1 => AnthropicOptions.Replace("\"supported\":true", "\"supported\":false"),
                _ => "{\"data\":[],\"has_more\":false}"
            }
            : generation switch
            {
                0 => OllamaOptions.Replace("false,true", "\"off\",\"low\""),
                1 => "{\"capabilities\":[\"completion\"]}",
                _ => "{\"models\":[]}"
            });
        using var client = new HttpClient(handler);
        var factory = Factory(client);
        var configuration = Configuration(kind);
        var oldModel = await Discover(factory, configuration);
        generation = 1;
        await Discover(factory, configuration);
        using var changed = await Chat(factory, handler, configuration, oldModel);
        if (kind == ProviderKind.Anthropic) AssertNoAnthropicKnobs(changed.RootElement);
        else Assert.False(changed.RootElement.TryGetProperty("think", out _));
        generation = 0;
        await Discover(factory, configuration);
        generation = 2;
        var empty = await factory.Catalog(kind).DiscoverAsync(configuration, null, CancellationToken.None);
        Assert.Null(empty.ErrorCode);
        Assert.Empty(empty.Models);
        using var removed = await Chat(factory, handler, configuration, oldModel, ReasoningIntent.Off);
        if (kind == ProviderKind.Anthropic) AssertNoAnthropicKnobs(removed.RootElement);
        else Assert.False(removed.RootElement.GetProperty("think").GetBoolean());
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.Ollama)]
    public async Task FailedRediscoveryDoesNotPublishPartialNativeMetadata(ProviderKind kind)
    {
        var rediscover = false;
        using var handler = new FakeHandler(uri => kind == ProviderKind.Anthropic
            ? rediscover ? AnthropicOptions.Replace("\"supported\":true", "\"supported\":false").Replace("}],\"has_more\"", "},{\"id\":\"\"}],\"has_more\"") : AnthropicOptions
            : rediscover && uri.AbsolutePath.EndsWith("/api/show", StringComparison.Ordinal) ? "{\"capabilities\":[\"completion\"]}" : OllamaOptions);
        using var client = new HttpClient(handler);
        var factory = Factory(client);
        var configuration = Configuration(kind);
        var model = await Discover(factory, configuration);
        rediscover = true;
        if (kind == ProviderKind.Ollama) handler.FailSecondShow = true;
        var failed = await factory.Catalog(kind).DiscoverAsync(configuration, null, CancellationToken.None);
        Assert.Equal("invalid_response", failed.ErrorCode);
        Assert.Empty(failed.Models);
        using var payload = await Chat(factory, handler, configuration, model);
        if (kind == ProviderKind.Anthropic) AssertAdaptiveMax(payload.RootElement);
        else Assert.True(payload.RootElement.GetProperty("think").GetBoolean());
    }

    private static ProviderFactory Factory(HttpClient client) => new ProviderFactory(new HttpTransport(client), new CapabilityResolver("{\"version\":1,\"models\":[]}"));

    private static ProviderConfiguration Configuration(ProviderKind kind) => new ProviderConfiguration(ConfigurationId,
        kind == ProviderKind.Anthropic ? "anthropic" : "ollama", kind, "https://first.example.test/api/");

    private static ProviderConfiguration DifferentConfiguration(ProviderConfiguration first, string difference) => new ProviderConfiguration(
        difference == "configuration" ? OtherConfigurationId : first.Id,
        difference == "provider" ? "other-provider" : first.ProviderId,
        difference == "kind" ? ProviderKind.OpenAI : first.Kind,
        difference == "endpoint" ? "https://second.example.test/api/" : difference == "path" ? "https://first.example.test/other/" : first.BaseUrl);

    private static async Task<ModelInfo> Discover(ProviderFactory factory, ProviderConfiguration configuration)
    {
        var result = await factory.Catalog(configuration.Kind).DiscoverAsync(configuration, null, CancellationToken.None);
        Assert.Null(result.ErrorCode);
        return Assert.Single(result.Models);
    }

    private static async Task<JsonDocument> Chat(ProviderFactory factory, FakeHandler handler, ProviderConfiguration configuration,
        ModelInfo model, ReasoningIntent intent = ReasoningIntent.Max, ProviderKind? adapterKind = null)
    {
        var events = new ConcurrentQueue<StreamEvent>();
        await factory.Chat(adapterKind ?? configuration.Kind).StreamAsync(new ChatRequest(configuration, model,
            new[] { new Message(MessageRole.User, new ContentPart[] { new TextPart("Offline regression") }) }, intent, maxOutputTokens: 8192),
            null, value => { events.Enqueue(value); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Empty(events.OfType<StreamError>());
        Assert.Single(events.OfType<Done>());
        Assert.True(handler.ChatBodies.TryDequeue(out var body));
        return JsonDocument.Parse(body!);
    }

    private static void AssertAdaptiveMax(JsonElement payload)
    {
        var thinking = payload.GetProperty("thinking");
        Assert.Equal("adaptive", thinking.GetProperty("type").GetString());
        Assert.False(thinking.TryGetProperty("budget_tokens", out _));
        Assert.Equal("max", payload.GetProperty("output_config").GetProperty("effort").GetString());
    }

    private static void AssertNoAnthropicKnobs(JsonElement payload)
    {
        Assert.False(payload.TryGetProperty("thinking", out _));
        Assert.False(payload.TryGetProperty("output_config", out _));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<Uri, string> catalog;
        private int requests;
        public FakeHandler(Func<Uri, string> catalog) { this.catalog = catalog; }
        public ConcurrentQueue<string> ChatBodies { get; } = new ConcurrentQueue<string>();
        public int RequestCount => requests;
        public bool FailSecondShow { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal) || uri.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal))
            {
                ChatBodies.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
                var anthropic = uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal);
                return Response(anthropic ? "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n" : "{\"done\":true,\"done_reason\":\"stop\"}\n",
                    anthropic ? "text/event-stream" : "application/x-ndjson");
            }
            var json = catalog(uri);
            if (uri.AbsolutePath.EndsWith("/api/tags", StringComparison.Ordinal))
                json = json == "{\"models\":[]}" ? json : FailSecondShow ? "{\"models\":[{\"name\":\"native-model\"},{\"name\":\"broken-model\"}]}" : "{\"models\":[{\"name\":\"native-model\"}]}";
            if (FailSecondShow && uri.AbsolutePath.EndsWith("/api/show", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (body.RootElement.GetProperty("model").GetString() == "broken-model") json = "{";
            }
            return Response(json, "application/json");
        }
        private static HttpResponseMessage Response(string body, string mediaType) => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    }
}
