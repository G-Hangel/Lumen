using System;
using System.Collections.Generic;
using System.IO;
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

public sealed class NativeProviderTests
{
    private const string EmptyRegistry = "{\"version\":1,\"models\":[]}";
    private const string FakeKey = "offline-secret-never-echo";
    private static readonly CapabilityResolver Resolver = new CapabilityResolver(EmptyRegistry);

    [Fact]
    public async Task AnthropicCatalogPaginatesAndNativeFalseOverridesRegistry()
    {
        var registry = Registry("anthropic", ".*", "\"vision\":true,\"reasoning\":{\"kind\":\"Effort\",\"efforts\":[\"High\"],\"mandatory\":true}");
        using var handler = new FakeHandler((_, count) => Response(count == 0
            ? "{\"data\":[{\"id\":\"claude-negative\",\"capabilities\":{\"image_input\":{\"supported\":false},\"thinking\":{\"supported\":false}}}],\"has_more\":true,\"last_id\":\"cursor /+\"}"
            : "{\"data\":[{\"id\":\"claude-positive\",\"display_name\":\"Positive\",\"max_input_tokens\":200000,\"max_tokens\":8192,\"capabilities\":{\"image_input\":{\"supported\":true},\"thinking\":{\"supported\":false},\"pdf_input\":{\"supported\":true},\"structured_outputs\":{\"supported\":false}}}],\"has_more\":false}"));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(new HttpTransport(client), new CapabilityResolver(registry));
        var result = await provider.DiscoverAsync(Configuration(ProviderKind.Anthropic), FakeKey, CancellationToken.None);
        Assert.True(result.CredentialValid);
        Assert.Null(result.ErrorCode);
        Assert.Equal(2, result.Models.Count);
        var negative = result.Models[0];
        Assert.False(negative.Has(ModelCapabilities.Vision));
        Assert.False(negative.Has(ModelCapabilities.Reasoning));
        Assert.Null(negative.Reasoning);
        Assert.Equal(CapabilitySource.Native, negative.SourceOf(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.Native, negative.SourceOf(ModelCapabilities.Reasoning));
        var positive = result.Models[1];
        Assert.True(positive.Has(ModelCapabilities.Vision));
        Assert.True(positive.Has(ModelCapabilities.Pdf));
        Assert.False(positive.Has(ModelCapabilities.StructuredOutput));
        Assert.Null(positive.SourceOf(ModelCapabilities.Tools));
        Assert.Equal(200000, positive.ContextWindowTokens);
        Assert.Equal(8192, positive.MaxOutputTokens);
        Assert.Equal("Positive", positive.DisplayName);
        Assert.Equal("/v1/models?limit=1000", handler.Requests[0].Uri.PathAndQuery);
        Assert.Equal("/v1/models?limit=1000&after_id=cursor%20%2F%2B", handler.Requests[1].Uri.PathAndQuery);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(FakeKey, request.ApiKey);
            Assert.Equal("2026-10-21", request.AnthropicVersion);
            Assert.Null(request.Body);
        });
    }

    [Theory]
    [InlineData("claude-sonnet-5-5", true, false, "between_tools")]
    [InlineData("claude-opus-5-5", false, false, "adaptive")]
    [InlineData("claude-sonnet-4-6", false, true, "disabled")]
    public async Task AnthropicUsesNativeAdaptiveTypesEffortsAndMandatoryRules(string id, bool betweenTools, bool disabled, string offType)
    {
        using var handler = new FakeHandler((_, _) => Response(AnthropicCatalog(id, adaptive: true, betweenTools: betweenTools, disabled: disabled)));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(new HttpTransport(client), Resolver);
        var configuration = Configuration(ProviderKind.Anthropic);
        var catalog = await provider.DiscoverAsync(configuration, FakeKey, CancellationToken.None);
        var model = Assert.Single(catalog.Models);
        Assert.Equal(!betweenTools && !disabled, model.Reasoning!.Mandatory);
        using var off = JsonDocument.Parse(provider.BuildPayload(Request(configuration, model)));
        Assert.Equal(offType, off.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("low", off.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(off.RootElement.GetProperty("thinking").TryGetProperty("budget_tokens", out _));
        using var maximum = JsonDocument.Parse(provider.BuildPayload(Request(configuration, model, ReasoningIntent.Max)));
        Assert.Equal("adaptive", maximum.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("max", maximum.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(maximum.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task AnthropicMaxNeverMapsToXhighOrUnsupportedMax()
    {
        using var handler = new FakeHandler((_, _) => Response(AnthropicCatalog("claude-opus-5-5", adaptive: true, max: false)));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(new HttpTransport(client), Resolver);
        var configuration = Configuration(ProviderKind.Anthropic);
        var model = Assert.Single((await provider.DiscoverAsync(configuration, null, CancellationToken.None)).Models);
        Assert.DoesNotContain(ReasoningIntent.Max, model.Reasoning!.Efforts);
        using var payload = JsonDocument.Parse(provider.BuildPayload(Request(configuration, model, ReasoningIntent.Max)));
        Assert.Equal("high", payload.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnthropicOmitsUnsupportedEffortDespiteNestedLevels(bool reasoning)
    {
        var catalogJson = "{\"data\":[{\"id\":\"claude-opaque\",\"capabilities\":{\"thinking\":{\"supported\":" + reasoning.ToString().ToLowerInvariant() + ",\"types\":{\"adaptive\":{\"supported\":true}}},\"effort\":{\"supported\":false,\"high\":{\"supported\":true}}}}]}";
        using var handler = new FakeHandler((_, _) => Response(catalogJson));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(new HttpTransport(client), Resolver);
        var configuration = Configuration(ProviderKind.Anthropic);
        var model = Assert.Single((await provider.DiscoverAsync(configuration, null, CancellationToken.None)).Models);
        using var payload = JsonDocument.Parse(provider.BuildPayload(Request(configuration, model, ReasoningIntent.High)));
        Assert.False(payload.RootElement.TryGetProperty("output_config", out _));
        Assert.False(payload.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.Equal(reasoning, payload.RootElement.TryGetProperty("thinking", out _));
    }

    [Theory]
    [InlineData(1025, 1024)]
    [InlineData(3000, 2999)]
    [InlineData(20000, 8192)]
    public async Task AnthropicLegacyBudgetsMeetBothBounds(int maxTokens, int expectedBudget)
    {
        using var handler = new FakeHandler((_, _) => Response(AnthropicCatalog("claude-sonnet-4-5", adaptive: false, enabled: true, disabled: true, effort: false)));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(new HttpTransport(client), Resolver);
        var configuration = Configuration(ProviderKind.Anthropic);
        var model = Assert.Single((await provider.DiscoverAsync(configuration, null, CancellationToken.None)).Models);
        Assert.Equal(ReasoningKind.BudgetTokens, model.Reasoning!.Kind);
        using var payload = JsonDocument.Parse(provider.BuildPayload(Request(configuration, model, ReasoningIntent.High, maxTokens)));
        var thinking = payload.RootElement.GetProperty("thinking");
        Assert.Equal("enabled", thinking.GetProperty("type").GetString());
        Assert.Equal(expectedBudget, thinking.GetProperty("budget_tokens").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("output_config", out _));
        var tooSmall = Assert.Throws<ProviderException>(() => provider.BuildPayload(Request(configuration, model, ReasoningIntent.High, 1024)));
        Assert.Equal("reasoning_budget_too_small", tooSmall.Code);
    }

    [Theory]
    [InlineData("claude-sonnet-4-20250514", ReasoningIntent.Minimal, 1024)]
    [InlineData("claude-sonnet-4-5-20250929", ReasoningIntent.Low, 1024)]
    [InlineData("claude-sonnet-4-5", ReasoningIntent.Medium, 4096)]
    [InlineData("claude-sonnet-4-5", ReasoningIntent.Max, 16384)]
    public async Task AnthropicLegacyBudgetIntentsDoNotAccidentallyRoundToOff(string id, ReasoningIntent intent, int expected)
    {
        using var handler = new FakeHandler((_, _) => Response(AnthropicCatalog(id, adaptive: false, enabled: true, disabled: true, effort: false)));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(new HttpTransport(client), Resolver);
        var configuration = Configuration(ProviderKind.Anthropic);
        var model = Assert.Single((await provider.DiscoverAsync(configuration, null, CancellationToken.None)).Models);
        Assert.Equal(ReasoningKind.BudgetTokens, model.Reasoning!.Kind);
        using var payload = JsonDocument.Parse(provider.BuildPayload(Request(configuration, model, intent, 20000)));
        Assert.Equal("enabled", payload.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(expected, payload.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
    }

    [Fact]
    public void AnthropicWithoutNativeSubcapabilitiesOmitsAdaptiveAndEffortKnobs()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(new HttpTransport(client), Resolver);
        using var payload = JsonDocument.Parse(provider.BuildPayload(Request(Configuration(ProviderKind.Anthropic), ReasoningModel("anthropic", "claude-opus-5-5", ReasoningKind.Effort, true), ReasoningIntent.Max)));
        Assert.False(payload.RootElement.TryGetProperty("thinking", out _));
        Assert.False(payload.RootElement.TryGetProperty("output_config", out _));
        using var staleBudget = JsonDocument.Parse(provider.BuildPayload(Request(Configuration(ProviderKind.Anthropic), ReasoningModel("anthropic", "claude-opus-4-7", ReasoningKind.BudgetTokens, true), ReasoningIntent.High)));
        Assert.False(staleBudget.RootElement.TryGetProperty("thinking", out _));
    }

    [Theory]
    [InlineData("claude-opus-4-7")]
    [InlineData("claude-opus-4.8")]
    [InlineData("claude-sonnet-5-5")]
    public async Task AnthropicModernModelsNeverReceiveLegacyBudgets(string id)
    {
        using var handler = new FakeHandler((_, _) => Response(AnthropicCatalog(id, adaptive: true, enabled: true)));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(new HttpTransport(client), Resolver);
        var configuration = Configuration(ProviderKind.Anthropic);
        var model = Assert.Single((await provider.DiscoverAsync(configuration, null, CancellationToken.None)).Models);
        using var payload = JsonDocument.Parse(provider.BuildPayload(Request(configuration, model, ReasoningIntent.High)));
        Assert.Equal("adaptive", payload.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(payload.RootElement.GetProperty("thinking").TryGetProperty("budget_tokens", out _));
    }

    [Fact]
    public async Task AnthropicFixtureReassemblesFiveArgumentDeltasAndEveryThinkingBlock()
    {
        var fixture = Fixture("anthropic.sse");
        using var handler = new FakeHandler((_, _) => Response(fixture, mediaType: "text/event-stream"));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(new HttpTransport(client), Resolver);
        var request = Request(Configuration(ProviderKind.Anthropic), PlainModel("anthropic", "claude-test", ModelCapabilities.Tools));
        var events = await Stream(provider, request);
        Assert.Empty(events.OfType<StreamError>());
        Assert.Equal("Hello world", string.Concat(events.OfType<TextDelta>().Select(delta => delta.Text)));
        Assert.Equal("checking files", string.Concat(events.OfType<ReasoningDelta>().Select(delta => delta.Text)));
        var start = Assert.Single(events.OfType<ToolCallStart>());
        var deltas = events.OfType<ToolCallArgsDelta>().ToArray();
        Assert.Equal(5, deltas.Length);
        Assert.All(deltas, delta => Assert.Equal(start.Id, delta.Id));
        Assert.Equal("{\"path\":\"a.cs\",\"line\":7}", string.Concat(deltas.Select(delta => delta.Chunk)));
        var call = Assert.Single(events.OfType<ToolCallEnd>());
        Assert.Equal("a.cs", call.ParsedArgs.GetProperty("path").GetString());
        Assert.Equal(7, call.ParsedArgs.GetProperty("line").GetInt32());
        Assert.Equal(FinishReason.ToolUse, Assert.Single(events.OfType<Done>()).FinishReason);
        Assert.Equal(19, events.OfType<Usage>().Last().InputTokens);
        Assert.Equal(42, events.OfType<Usage>().Last().OutputTokens);
        Assert.Equal(12, events.OfType<Usage>().Last().ReasoningTokens);
        var states = events.OfType<ReasoningDelta>().Where(delta => delta.ProviderState is not null).ToArray();
        Assert.Equal(3, states.Length);
        using var first = JsonDocument.Parse(states[0].ProviderState!);
        Assert.Equal("checking files", first.RootElement.GetProperty("thinking").GetString());
        Assert.Equal("sig-one", first.RootElement.GetProperty("signature").GetString());
        using var empty = JsonDocument.Parse(states[1].ProviderState!);
        Assert.Equal("", empty.RootElement.GetProperty("thinking").GetString());
        Assert.Equal("empty-signature", empty.RootElement.GetProperty("signature").GetString());
        using var redacted = JsonDocument.Parse(states[2].ProviderState!);
        Assert.Equal("opaque-redacted-data", redacted.RootElement.GetProperty("data").GetString());

        var replay = ReplayEvents(events);
        using var payload = JsonDocument.Parse(provider.BuildPayload(new ChatRequest(request.Configuration, request.Model, new[] { replay })));
        var blocks = payload.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(new[] { "thinking", "text", "thinking", "redacted_thinking", "tool_use" }, blocks.EnumerateArray().Select(block => block.GetProperty("type").GetString()));
        Assert.Equal(first.RootElement.GetRawText(), blocks[0].GetRawText());
        Assert.Equal(empty.RootElement.GetRawText(), blocks[2].GetRawText());
        Assert.Equal(redacted.RootElement.GetRawText(), blocks[3].GetRawText());
        Assert.Equal("Hello world", blocks[1].GetProperty("text").GetString());
        Assert.Equal("/v1/messages", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task GeminiCatalogPaginatesFiltersEmbeddingsAndUsesRegistryNotVisionGuessing()
    {
        var registry = Registry("gemini", "^gemma-.*", "\"vision\":false,\"tools\":false,\"reasoning\":false");
        using var handler = new FakeHandler((_, count) => Response(count == 0
            ? "{\"models\":[{\"name\":\"models/embedding-1\",\"supportedGenerationMethods\":[\"embedContent\"]},{\"name\":\"models/gemma-vision\",\"thinking\":false,\"supportedGenerationMethods\":[\"generateContent\"]}],\"nextPageToken\":\"cursor /+\"}"
            : "{\"models\":[{\"name\":\"models/gemini-3-pro-preview\",\"displayName\":\"Gemini Pro\",\"inputTokenLimit\":1000000,\"outputTokenLimit\":8192,\"thinking\":true,\"supportedGenerationMethods\":[\"generateContent\",\"countTokens\"]}]}"));
        using var client = new HttpClient(handler);
        var provider = new GeminiProvider(new HttpTransport(client), new CapabilityResolver(registry));
        var result = await provider.DiscoverAsync(Configuration(ProviderKind.Gemini), FakeKey, CancellationToken.None);
        Assert.Null(result.ErrorCode);
        Assert.Equal(2, result.Models.Count);
        Assert.False(result.Models[0].Has(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.Registry, result.Models[0].SourceOf(ModelCapabilities.Vision));
        Assert.False(result.Models[0].Has(ModelCapabilities.Reasoning));
        Assert.Equal(CapabilitySource.Native, result.Models[0].SourceOf(ModelCapabilities.Reasoning));
        var pro = result.Models[1];
        Assert.Equal("gemini-3-pro-preview", pro.ModelId);
        Assert.Equal(1000000, pro.ContextWindowTokens);
        Assert.Equal(8192, pro.MaxOutputTokens);
        Assert.True(pro.Has(ModelCapabilities.Reasoning));
        Assert.Equal(CapabilitySource.Native, pro.SourceOf(ModelCapabilities.Reasoning));
        Assert.Null(pro.SourceOf(ModelCapabilities.Vision));
        Assert.Null(pro.SourceOf(ModelCapabilities.Tools));
        Assert.Equal("/v1beta/models?pageSize=1000", handler.Requests[0].Uri.PathAndQuery);
        Assert.Equal("/v1beta/models?pageSize=1000&pageToken=cursor%20%2F%2B", handler.Requests[1].Uri.PathAndQuery);
        Assert.All(handler.Requests, request => Assert.Equal(FakeKey, request.ApiKey));
    }

    [Theory]
    [InlineData("gemini-3-flash-preview", ReasoningKind.Effort, ReasoningIntent.Medium, "thinkingLevel", "MEDIUM")]
    [InlineData("gemini-3-flash-preview", ReasoningKind.Effort, ReasoningIntent.Max, "thinkingLevel", "HIGH")]
    [InlineData("gemini-2.5-pro", ReasoningKind.BudgetTokens, ReasoningIntent.Medium, "thinkingBudget", "4096")]
    public void GeminiUsesOnlyVersionAppropriateReasoningControls(string id, ReasoningKind kind, ReasoningIntent intent, string field, string value)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("BuildPayload must not send HTTP."));
        using var client = new HttpClient(handler);
        var provider = new GeminiProvider(new HttpTransport(client), Resolver);
        var model = ReasoningModel("gemini", id, kind, false);
        using var payload = JsonDocument.Parse(provider.BuildPayload(Request(Configuration(ProviderKind.Gemini), model, intent)));
        var thinking = payload.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.Single(thinking.EnumerateObject());
        Assert.Equal(value, thinking.GetProperty(field).ToString());
        Assert.False(thinking.TryGetProperty("includeThoughts", out _));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("gemini-2.5-pro", ReasoningKind.Effort)]
    [InlineData("gemini-3-pro-preview", ReasoningKind.BudgetTokens)]
    [InlineData("unknown-thinking-model", ReasoningKind.Effort)]
    public void GeminiNeverGuessesUnsupportedReasoningSwitches(string id, ReasoningKind kind)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var provider = new GeminiProvider(new HttpTransport(client), Resolver);
        using var payload = JsonDocument.Parse(provider.BuildPayload(Request(Configuration(ProviderKind.Gemini), ReasoningModel("gemini", id, kind, false), ReasoningIntent.High)));
        Assert.False(payload.RootElement.GetProperty("generationConfig").TryGetProperty("thinkingConfig", out _));
    }

    [Fact]
    public void GeminiOffBudgetRequiresExplicitNonmandatorySupport()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var provider = new GeminiProvider(new HttpTransport(client), Resolver);
        var configuration = Configuration(ProviderKind.Gemini);
        using var flash = JsonDocument.Parse(provider.BuildPayload(Request(configuration, ReasoningModel("gemini", "gemini-2.5-flash", ReasoningKind.BudgetTokens, false))));
        using var pro = JsonDocument.Parse(provider.BuildPayload(Request(configuration, ReasoningModel("gemini", "gemini-2.5-pro", ReasoningKind.BudgetTokens, true))));
        Assert.Equal(0, flash.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
        Assert.True(pro.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32() > 0);
    }

    [Fact]
    public async Task GeminiFixturePreservesSignedPartsEmptyThoughtsAndToolReplay()
    {
        using var handler = new FakeHandler((_, _) => Response(Fixture("gemini.sse"), mediaType: "text/event-stream"));
        using var client = new HttpClient(handler);
        var provider = new GeminiProvider(new HttpTransport(client), Resolver);
        var request = Request(Configuration(ProviderKind.Gemini), PlainModel("gemini", "gemini-3-pro-preview", ModelCapabilities.Tools));
        var events = await Stream(provider, request);
        Assert.Empty(events.OfType<StreamError>());
        Assert.Equal("Hello world", string.Concat(events.OfType<TextDelta>().Select(delta => delta.Text)));
        Assert.Equal("checking files", string.Concat(events.OfType<ReasoningDelta>().Select(delta => delta.Text)));
        var states = events.OfType<ReasoningDelta>().ToArray();
        Assert.Equal(4, states.Length);
        Assert.All(states, delta => Assert.NotNull(delta.ProviderState));
        Assert.Equal("", states[0].Text);
        Assert.Equal("", states[1].Text);
        var call = Assert.Single(events.OfType<ToolCallEnd>());
        Assert.Equal("gemini-call-0", call.Id);
        Assert.Equal("{\"path\":\"a.cs\",\"line\":7}", call.ParsedArgs.GetRawText());
        using var callState = JsonDocument.Parse(call.ProviderState!);
        Assert.Equal("function-signature", callState.RootElement.GetProperty("thoughtSignature").GetString());
        Assert.False(callState.RootElement.GetProperty("functionCall").TryGetProperty("id", out _));
        Assert.Equal(FinishReason.ToolUse, Assert.Single(events.OfType<Done>()).FinishReason);
        var usage = Assert.Single(events.OfType<Usage>());
        Assert.Equal(19, usage.InputTokens);
        Assert.Equal(42, usage.OutputTokens);
        Assert.Equal(12, usage.ReasoningTokens);

        var replay = ReplayEvents(events);
        var toolResult = new Message(MessageRole.Tool, new ContentPart[] { new ToolResultPart(call.Id, false, "file contents") });
        using var payload = JsonDocument.Parse(provider.BuildPayload(new ChatRequest(request.Configuration, request.Model, new[] { replay, toolResult })));
        var contents = payload.RootElement.GetProperty("contents");
        var parts = contents[0].GetProperty("parts");
        Assert.Equal(6, parts.GetArrayLength());
        Assert.Equal("empty-one", parts[0].GetProperty("thoughtSignature").GetString());
        Assert.Equal("empty-two", parts[1].GetProperty("thoughtSignature").GetString());
        Assert.Equal("", parts[0].GetProperty("text").GetString());
        Assert.Equal("", parts[1].GetProperty("text").GetString());
        Assert.Equal("Hello ", parts[3].GetProperty("text").GetString());
        Assert.Equal("answer-signature", parts[3].GetProperty("thoughtSignature").GetString());
        Assert.Equal("world", parts[4].GetProperty("text").GetString());
        Assert.Equal(callState.RootElement.GetRawText(), parts[5].GetRawText());
        var response = contents[1].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("read_file", response.GetProperty("name").GetString());
        Assert.False(response.TryGetProperty("id", out _));
        Assert.Equal("file contents", response.GetProperty("response").GetProperty("output").GetString());
        Assert.Equal("/v1beta/models/gemini-3-pro-preview:streamGenerateContent?alt=sse", Assert.Single(handler.Requests).Uri.PathAndQuery);
    }

    [Fact]
    public async Task GeminiLocalIdsAreStableDistinctAndNativeIdIsReplayed()
    {
        const string data = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"name\":\"read_file\"}},{\"functionCall\":{\"name\":\"read_file\",\"args\":{}}},{\"functionCall\":{\"name\":\"read_file\",\"id\":\"native-id\",\"args\":{}},\"thoughtSignature\":\"signed\"}]},\"finishReason\":\"STOP\"}]}\n\n";
        using var handler = new FakeHandler((_, _) => Response(data));
        using var client = new HttpClient(handler);
        var provider = new GeminiProvider(new HttpTransport(client), Resolver);
        var request = Request(Configuration(ProviderKind.Gemini), PlainModel("gemini", "gemini-3-pro-preview", ModelCapabilities.Tools));
        var first = await Stream(provider, request);
        var second = await Stream(provider, request);
        Assert.Equal(new[] { "gemini-call-0", "gemini-call-1", "native-id" }, first.OfType<ToolCallStart>().Select(call => call.Id));
        Assert.Equal(first.OfType<ToolCallStart>().Select(call => call.Id), second.OfType<ToolCallStart>().Select(call => call.Id));
        Assert.Equal(first.OfType<ToolCallStart>().Select(call => call.Id), first.OfType<ToolCallEnd>().Select(call => call.Id));
        using var payload = JsonDocument.Parse(provider.BuildPayload(new ChatRequest(request.Configuration, request.Model, new[] { ReplayEvents(first), new Message(MessageRole.Tool, new ContentPart[] { new ToolResultPart("native-id", true, "not found") }) })));
        var response = payload.RootElement.GetProperty("contents")[1].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("native-id", response.GetProperty("id").GetString());
        Assert.Equal("not found", response.GetProperty("response").GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.Gemini)]
    public async Task UnknownEventsAndTruncatedTailPreserveTextButNeverReportCompletion(ProviderKind kind)
    {
        var data = kind == ProviderKind.Anthropic
            ? Fixture("anthropic.sse").Split("event: message_stop", StringSplitOptions.None)[0] + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":"
            : Fixture("gemini.sse") + "data: {\"candidates\":[";
        using var handler = new FakeHandler((_, _) => Response(data, mediaType: "text/event-stream"));
        using var client = new HttpClient(handler);
        var provider = Provider(kind, new HttpTransport(client));
        var events = await Stream(provider, Request(Configuration(kind), PlainModel(ProviderId(kind), "test")));
        Assert.Equal("Hello world", string.Concat(events.OfType<TextDelta>().Select(delta => delta.Text)));
        Assert.Equal("invalid_stream", Assert.Single(events.OfType<StreamError>()).Code);
        Assert.Empty(events.OfType<Done>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnthropicFrameBoundaryEofWithoutMessageStopIsInvalidStream(bool includeMessageDelta)
    {
        var fixture = Fixture("anthropic.sse");
        var boundary = includeMessageDelta ? "event: message_stop" : "event: message_delta";
        Assert.Contains(boundary, fixture);
        var data = fixture.Split(boundary, StringSplitOptions.None)[0];
        Assert.EndsWith("\n\n", data.Replace("\r\n", "\n"));
        using var handler = new FakeHandler((_, _) => Response(data, mediaType: "text/event-stream"));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(new HttpTransport(client), Resolver);
        var events = await Stream(provider, Request(Configuration(ProviderKind.Anthropic), PlainModel("anthropic", "claude-test", ModelCapabilities.Tools)));
        Assert.Equal("Hello world", string.Concat(events.OfType<TextDelta>().Select(delta => delta.Text)));
        Assert.Equal("checking files", string.Concat(events.OfType<ReasoningDelta>().Select(delta => delta.Text)));
        Assert.Equal(3, events.OfType<ReasoningDelta>().Count(delta => delta.ProviderState is not null));
        Assert.Equal(5, events.OfType<ToolCallArgsDelta>().Count());
        Assert.Equal("{\"path\":\"a.cs\",\"line\":7}", Assert.Single(events.OfType<ToolCallEnd>()).ParsedArgs.GetRawText());
        var usage = events.OfType<Usage>().Last();
        Assert.Equal(19, usage.InputTokens);
        Assert.Equal(includeMessageDelta ? 42 : 0, usage.OutputTokens);
        Assert.Equal(includeMessageDelta ? 12 : (int?)null, usage.ReasoningTokens);
        Assert.Empty(events.OfType<Done>());
        var error = Assert.Single(events.OfType<StreamError>());
        Assert.Equal("invalid_stream", error.Code);
        Assert.Equal(error.Code, error.Message);
        Assert.Same(error, events.Last());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeminiFrameBoundaryEofWithoutFinishReasonIsInvalidStream(bool includeUsage)
    {
        var fixture = Fixture("gemini.sse");
        const string terminalFrame = "data: {\"candidates\":[{\"index\":0,\"finishReason\"";
        Assert.Contains(terminalFrame, fixture);
        var data = includeUsage ? fixture.Replace(",\"finishReason\":\"STOP\"", "") : fixture.Split(terminalFrame, StringSplitOptions.None)[0];
        Assert.DoesNotContain("finishReason", data);
        Assert.EndsWith("\n\n", data.Replace("\r\n", "\n"));
        using var handler = new FakeHandler((_, _) => Response(data, mediaType: "text/event-stream"));
        using var client = new HttpClient(handler);
        var provider = new GeminiProvider(new HttpTransport(client), Resolver);
        var events = await Stream(provider, Request(Configuration(ProviderKind.Gemini), PlainModel("gemini", "gemini-3-pro-preview", ModelCapabilities.Tools)));
        Assert.Equal("Hello world", string.Concat(events.OfType<TextDelta>().Select(delta => delta.Text)));
        Assert.Equal("checking files", string.Concat(events.OfType<ReasoningDelta>().Select(delta => delta.Text)));
        Assert.Equal(4, events.OfType<ReasoningDelta>().Count(delta => delta.ProviderState is not null));
        var call = Assert.Single(events.OfType<ToolCallEnd>());
        Assert.Equal("{\"path\":\"a.cs\",\"line\":7}", call.ParsedArgs.GetRawText());
        using var state = JsonDocument.Parse(call.ProviderState!);
        Assert.Equal("function-signature", state.RootElement.GetProperty("thoughtSignature").GetString());
        if (includeUsage)
        {
            var usage = Assert.Single(events.OfType<Usage>());
            Assert.Equal(19, usage.InputTokens);
            Assert.Equal(42, usage.OutputTokens);
            Assert.Equal(12, usage.ReasoningTokens);
        }
        else Assert.Empty(events.OfType<Usage>());
        Assert.Empty(events.OfType<Done>());
        var error = Assert.Single(events.OfType<StreamError>());
        Assert.Equal("invalid_stream", error.Code);
        Assert.Equal(error.Code, error.Message);
        Assert.Same(error, events.Last());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\" \"")]
    public async Task GeminiNullOrBlankFinishReasonIsNotTerminalEvidence(string reason)
    {
        var fixture = Fixture("gemini.sse");
        Assert.Contains("\"finishReason\":\"STOP\"", fixture);
        var data = fixture.Replace("\"finishReason\":\"STOP\"", "\"finishReason\":" + reason);
        using var handler = new FakeHandler((_, _) => Response(data, mediaType: "text/event-stream"));
        using var client = new HttpClient(handler);
        var provider = new GeminiProvider(new HttpTransport(client), Resolver);
        var events = await Stream(provider, Request(Configuration(ProviderKind.Gemini), PlainModel("gemini", "gemini-3-pro-preview", ModelCapabilities.Tools)));
        Assert.Equal("Hello world", string.Concat(events.OfType<TextDelta>().Select(delta => delta.Text)));
        Assert.Empty(events.OfType<Done>());
        Assert.Equal("invalid_stream", Assert.Single(events.OfType<StreamError>()).Code);
    }

    [Fact]
    public async Task GeminiFinishReasonDoesNotDiscardTrailingUsageFrame()
    {
        const string usage = "{\"promptTokenCount\":19,\"candidatesTokenCount\":42,\"thoughtsTokenCount\":12}";
        var fixture = Fixture("gemini.sse");
        Assert.Contains(",\"usageMetadata\":" + usage, fixture);
        var data = fixture.Replace(",\"usageMetadata\":" + usage, "") + "data: {\"usageMetadata\":" + usage + "}\n\n";
        using var handler = new FakeHandler((_, _) => Response(data, mediaType: "text/event-stream"));
        using var client = new HttpClient(handler);
        var provider = new GeminiProvider(new HttpTransport(client), Resolver);
        var events = await Stream(provider, Request(Configuration(ProviderKind.Gemini), PlainModel("gemini", "gemini-3-pro-preview", ModelCapabilities.Tools)));
        Assert.Empty(events.OfType<StreamError>());
        var done = Assert.Single(events.OfType<Done>());
        Assert.Equal(FinishReason.ToolUse, done.FinishReason);
        Assert.Same(done, events.Last());
        var counts = Assert.Single(events.OfType<Usage>());
        Assert.Equal(19, counts.InputTokens);
        Assert.Equal(42, counts.OutputTokens);
        Assert.Equal(12, counts.ReasoningTokens);
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic, "{\"type\":\"content_block_delta\",\"index\":4,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"[1]\"}}")]
    [InlineData(ProviderKind.Gemini, "{\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"name\":\"bad\",\"args\":[1]}}]}}]}")]
    public async Task InvalidToolArgumentsAreRejectedWithoutToolEnd(ProviderKind kind, string invalid)
    {
        var data = kind == ProviderKind.Anthropic
            ? "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":4,\"content_block\":{\"type\":\"tool_use\",\"id\":\"bad\",\"name\":\"bad\",\"input\":{}}}\n\nevent: content_block_delta\ndata: " + invalid + "\n\nevent: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":4}\n\n"
            : "data: " + invalid + "\n\n";
        using var handler = new FakeHandler((_, _) => Response(data));
        using var client = new HttpClient(handler);
        var events = await Stream(Provider(kind, new HttpTransport(client)), Request(Configuration(kind), PlainModel(ProviderId(kind), "test")));
        Assert.Equal("invalid_stream", Assert.Single(events.OfType<StreamError>()).Code);
        Assert.Empty(events.OfType<ToolCallEnd>());
        Assert.Empty(events.OfType<Done>());
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.Gemini)]
    public void PayloadUsesNativeImageAndToolSchemasAndSystemRole(ProviderKind kind)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var model = PlainModel(ProviderId(kind), "test", ModelCapabilities.Vision | ModelCapabilities.Tools);
        var request = new ChatRequest(Configuration(kind), model, new[]
        {
            new Message(MessageRole.System, new ContentPart[] { new TextPart("system instruction") }),
            new Message(MessageRole.User, new ContentPart[] { new TextPart("describe"), new ImagePart("image/png", "AQID") })
        }, ReasoningIntent.High, new[] { new ToolSpec("read_file", "Read a file", Json("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}")) });
        using var payload = JsonDocument.Parse(Build(Provider(kind, new HttpTransport(client)), request));
        var root = payload.RootElement;
        if (kind == ProviderKind.Anthropic)
        {
            Assert.Equal("system instruction", root.GetProperty("system")[0].GetProperty("text").GetString());
            var source = root.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("source");
            Assert.Equal("base64", source.GetProperty("type").GetString());
            Assert.Equal("image/png", source.GetProperty("media_type").GetString());
            Assert.Equal("AQID", source.GetProperty("data").GetString());
            Assert.Equal("object", root.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString());
            Assert.False(root.TryGetProperty("thinking", out _));
            Assert.False(root.TryGetProperty("output_config", out _));
        }
        else
        {
            Assert.Equal("system instruction", root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
            var inline = root.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inlineData");
            Assert.Equal("image/png", inline.GetProperty("mimeType").GetString());
            Assert.Equal("AQID", inline.GetProperty("data").GetString());
            Assert.Equal("object", root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("parametersJsonSchema").GetProperty("type").GetString());
            Assert.False(root.GetProperty("generationConfig").TryGetProperty("thinkingConfig", out _));
        }
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.Gemini)]
    public void ImagesOnExplicitlyNonVisionModelsAreRejected(ProviderKind kind)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var request = new ChatRequest(Configuration(kind), PlainModel(ProviderId(kind), "negative"), new[] { new Message(MessageRole.User, new ContentPart[] { new ImagePart("image/png", "AQID") }) });
        Assert.Equal("vision_not_supported", Assert.Throws<ProviderException>(() => Build(Provider(kind, new HttpTransport(client)), request)).Code);
    }

    [Fact]
    public void AnthropicRejectsImagesOverPerImageLimit()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var image = new ImagePart("image/png", Convert.ToBase64String(new byte[10 * 1024 * 1024 + 1]));
        var request = new ChatRequest(Configuration(ProviderKind.Anthropic), PlainModel("anthropic", "test", ModelCapabilities.Vision), new[] { new Message(MessageRole.User, new ContentPart[] { image }) });
        Assert.Equal("image_too_large", Assert.Throws<ProviderException>(() => new AnthropicProvider(new HttpTransport(client), Resolver).BuildPayload(request)).Code);
    }

    [Fact]
    public void GeminiCountsBase64ExpansionAndAllSerializedRequestBytes()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var provider = new GeminiProvider(new HttpTransport(client), Resolver);
        var configuration = Configuration(ProviderKind.Gemini);
        var model = PlainModel("gemini", "test", ModelCapabilities.Vision);
        var largeImage = new ImagePart("image/png", Convert.ToBase64String(new byte[16 * 1024 * 1024]));
        var large = new ChatRequest(configuration, model, new[] { new Message(MessageRole.User, new ContentPart[] { largeImage }) });
        Assert.Equal("request_too_large", Assert.Throws<ProviderException>(() => provider.BuildPayload(large)).Code);
        var smallImage = new ImagePart("image/png", Convert.ToBase64String(new byte[1024]));
        var fits = new ChatRequest(configuration, model, new[] { new Message(MessageRole.User, new ContentPart[] { smallImage }) });
        Assert.True(Encoding.UTF8.GetByteCount(provider.BuildPayload(fits)) < 20 * 1024 * 1024);
        var largeSystem = new ChatRequest(configuration, model, new[]
        {
            new Message(MessageRole.System, new ContentPart[] { new TextPart(new string('x', 20 * 1024 * 1024)) }),
            new Message(MessageRole.User, new ContentPart[] { smallImage })
        });
        Assert.Equal("request_too_large", Assert.Throws<ProviderException>(() => provider.BuildPayload(largeSystem)).Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic, 401, "invalid_key", false)]
    [InlineData(ProviderKind.Anthropic, 404, "invalid_url", null)]
    [InlineData(ProviderKind.Anthropic, 429, "rate_limited", true)]
    [InlineData(ProviderKind.Gemini, 401, "invalid_key", false)]
    [InlineData(ProviderKind.Gemini, 404, "invalid_url", null)]
    [InlineData(ProviderKind.Gemini, 429, "rate_limited", true)]
    public async Task CatalogErrorsAreFixedCodesWithoutEchoingBodyOrKey(ProviderKind kind, int status, string code, bool? valid)
    {
        using var handler = new FakeHandler((_, _) => Response("body echoed " + FakeKey, (HttpStatusCode)status));
        using var client = new HttpClient(handler);
        var provider = Provider(kind, new HttpTransport(client, (_, _) => Task.CompletedTask));
        var result = await ((IModelCatalog)provider).DiscoverAsync(Configuration(kind), FakeKey, CancellationToken.None);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(valid, result.CredentialValid);
        Assert.DoesNotContain(FakeKey, result.ErrorCode!);
        Assert.Empty(result.Models);
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.Gemini)]
    public async Task StreamErrorsNeverEchoProviderBodyAndPostIsNeverRetried(ProviderKind kind)
    {
        using var handler = new FakeHandler((_, _) => Response("body echoed " + FakeKey, HttpStatusCode.TooManyRequests));
        using var client = new HttpClient(handler);
        var events = await Stream(Provider(kind, new HttpTransport(client)), Request(Configuration(kind), PlainModel(ProviderId(kind), "test")));
        var error = Assert.Single(events.OfType<StreamError>());
        Assert.Equal("rate_limited", error.Code);
        Assert.Equal(error.Code, error.Message);
        Assert.DoesNotContain(FakeKey, error.Message);
        Assert.Single(handler.Requests);
        Assert.Empty(events.OfType<Done>());
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic, "event: error\ndata: {\"type\":\"error\",\"error\":{\"message\":\"offline-secret-never-echo\"}}\n\n")]
    [InlineData(ProviderKind.Gemini, "data: {\"error\":{\"message\":\"offline-secret-never-echo\"}}\n\n")]
    public async Task SseProviderErrorsAreRedacted(ProviderKind kind, string data)
    {
        using var handler = new FakeHandler((_, _) => Response(data));
        using var client = new HttpClient(handler);
        var events = await Stream(Provider(kind, new HttpTransport(client)), Request(Configuration(kind), PlainModel(ProviderId(kind), "test")));
        var error = Assert.Single(events.OfType<StreamError>());
        Assert.Equal("provider_error", error.Code);
        Assert.Equal("provider_error", error.Message);
        Assert.DoesNotContain(FakeKey, error.Message);
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic, "{\"data\":[{\"id\":\"offline-secret-never-echo\",\"capabilities\":{\"thinking\":{\"supported\":\"invalid\"}}}]}")]
    [InlineData(ProviderKind.Gemini, "{\"models\":[{\"name\":\"models/offline-secret-never-echo\",\"thinking\":\"invalid\",\"supportedGenerationMethods\":[\"generateContent\"]}]}")]
    [InlineData(ProviderKind.Anthropic, "{\"data\":[")]
    [InlineData(ProviderKind.Gemini, "{\"models\":[")]
    public async Task MalformedCatalogReturnsSafeError(ProviderKind kind, string data)
    {
        using var handler = new FakeHandler((_, _) => Response(data));
        using var client = new HttpClient(handler);
        var result = await ((IModelCatalog)Provider(kind, new HttpTransport(client))).DiscoverAsync(Configuration(kind), FakeKey, CancellationToken.None);
        Assert.Equal("invalid_response", result.ErrorCode);
        Assert.Null(result.CredentialValid);
        Assert.Empty(result.Models);
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic, "{\"data\":[],\"has_more\":true,\"last_id\":\"repeat\"}")]
    [InlineData(ProviderKind.Gemini, "{\"models\":[],\"nextPageToken\":\"repeat\"}")]
    public async Task RepeatedCatalogCursorIsRejectedWithoutAnInfiniteLoop(ProviderKind kind, string data)
    {
        using var handler = new FakeHandler((_, _) => Response(data));
        using var client = new HttpClient(handler);
        var result = await ((IModelCatalog)Provider(kind, new HttpTransport(client))).DiscoverAsync(Configuration(kind), null, CancellationToken.None);
        Assert.Equal("invalid_response", result.ErrorCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic, "https://offline.invalid/v1/", "/v1/messages")]
    [InlineData(ProviderKind.Gemini, "https://offline.invalid/v1beta/", "/v1beta/models/test:streamGenerateContent")]
    public async Task VersionedBaseUrlDoesNotDuplicateApiVersion(ProviderKind kind, string baseUrl, string expectedPath)
    {
        using var handler = new FakeHandler((_, _) => Response(kind == ProviderKind.Anthropic ? "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n" : "data: {}\n\n"));
        using var client = new HttpClient(handler);
        await Stream(Provider(kind, new HttpTransport(client)), Request(Configuration(kind, baseUrl), PlainModel(ProviderId(kind), "test")));
        Assert.Equal(expectedPath, Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.Gemini)]
    public void InvalidOpaqueReplayStateIsRejectedWithSafeCode(ProviderKind kind)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var request = new ChatRequest(Configuration(kind), PlainModel(ProviderId(kind), "test"), new[] { new Message(MessageRole.Assistant, new ContentPart[] { new ReasoningPart("", "[\"" + FakeKey + "\"]") }) });
        var error = Assert.Throws<ProviderException>(() => Build(Provider(kind, new HttpTransport(client)), request));
        Assert.Equal("invalid_provider_state", error.Code);
        Assert.DoesNotContain(FakeKey, error.Message);
    }

    private static IChatProvider Provider(ProviderKind kind, HttpTransport transport) => kind == ProviderKind.Anthropic ? new AnthropicProvider(transport, Resolver) : new GeminiProvider(transport, Resolver);
    private static string Build(IChatProvider provider, ChatRequest request) => provider is AnthropicProvider anthropic ? anthropic.BuildPayload(request) : ((GeminiProvider)provider).BuildPayload(request);
    private static string ProviderId(ProviderKind kind) => kind == ProviderKind.Anthropic ? "anthropic" : "gemini";
    private static ProviderConfiguration Configuration(ProviderKind kind, string baseUrl = "https://offline.invalid/") => new ProviderConfiguration("11111111-1111-1111-1111-111111111111", ProviderId(kind), kind, baseUrl);
    private static ModelInfo PlainModel(string provider, string id, ModelCapabilities extras = ModelCapabilities.None) => new ModelInfo(provider, id, ModelCapabilities.Text | ModelCapabilities.Streaming | extras);
    private static ModelInfo ReasoningModel(string provider, string id, ReasoningKind kind, bool mandatory) => new ModelInfo(provider, id, ModelCapabilities.Text | ModelCapabilities.Reasoning, reasoning: new ReasoningSupport(kind, mandatory ? new[] { ReasoningIntent.Low, ReasoningIntent.Medium, ReasoningIntent.High, ReasoningIntent.Max } : new[] { ReasoningIntent.Off, ReasoningIntent.Minimal, ReasoningIntent.Low, ReasoningIntent.Medium, ReasoningIntent.High, ReasoningIntent.Max }, mandatory));
    private static ChatRequest Request(ProviderConfiguration configuration, ModelInfo model, ReasoningIntent intent = ReasoningIntent.Off, int maxTokens = 1024) => new ChatRequest(configuration, model, new[] { new Message(MessageRole.User, new ContentPart[] { new TextPart("hello") }) }, intent, maxOutputTokens: maxTokens);
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static string Registry(string provider, string pattern, string fields) => "{\"version\":1,\"models\":[{\"providerId\":\"" + provider + "\",\"modelIdPattern\":\"" + pattern + "\"," + fields + ",\"source\":\"https://example.com/documented\",\"confidence\":\"High\",\"verifiedAt\":\"2020-01-01\"}]}";
    private static string AnthropicCatalog(string id, bool adaptive, bool enabled = false, bool disabled = false, bool betweenTools = false, bool effort = true, bool max = true) => JsonSerializer.Serialize(new
    {
        data = new[] { new { id, capabilities = new
        {
            thinking = new { supported = true, types = new { adaptive = new { supported = adaptive }, enabled = new { supported = enabled }, disabled = new { supported = disabled }, between_tools = new { supported = betweenTools } } },
            effort = new { supported = effort, low = new { supported = true }, medium = new { supported = true }, high = new { supported = true }, xhigh = new { supported = true }, max = new { supported = max } }
        } } }
    });
    private static string Fixture(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "Fixtures", name);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("Owned native provider fixture was not found.", name);
    }
    private static async Task<List<StreamEvent>> Stream(IChatProvider provider, ChatRequest request)
    {
        var events = new List<StreamEvent>();
        await provider.StreamAsync(request, FakeKey, value => { events.Add(value); return Task.CompletedTask; }, CancellationToken.None);
        return events;
    }
    private static Message ReplayEvents(IEnumerable<StreamEvent> events)
    {
        var parts = new List<ContentPart>();
        var calls = new Dictionary<string, string>();
        var thought = new StringBuilder();
        foreach (var value in events)
        {
            switch (value)
            {
                case TextDelta text:
                    if (parts.LastOrDefault() is TextPart previous) parts[parts.Count - 1] = new TextPart(previous.Text + text.Text);
                    else parts.Add(new TextPart(text.Text));
                    break;
                case ReasoningDelta reasoning:
                    thought.Append(reasoning.Text);
                    if (reasoning.ProviderState is not null)
                    {
                        parts.Add(new ReasoningPart(thought.ToString(), reasoning.ProviderState));
                        thought.Clear();
                    }
                    break;
                case ToolCallStart call:
                    calls[call.Id] = call.Name;
                    break;
                case ToolCallEnd call:
                    parts.Add(new ToolCallPart(call.Id, calls[call.Id], call.ParsedArgs, call.ProviderState));
                    break;
            }
        }
        return new Message(MessageRole.Assistant, parts);
    }
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class Snapshot
    {
        internal HttpMethod Method = HttpMethod.Get;
        internal Uri Uri = new Uri("https://offline.invalid/");
        internal string? ApiKey;
        internal string? AnthropicVersion;
        internal string? Body;
    }
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> respond;
        internal List<Snapshot> Requests { get; } = new List<Snapshot>();
        internal FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) { this.respond = respond; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new Snapshot
            {
                Method = request.Method,
                Uri = request.RequestUri!,
                ApiKey = request.Headers.TryGetValues("x-api-key", out var anthropic) ? anthropic.Single() : request.Headers.TryGetValues("x-goog-api-key", out var gemini) ? gemini.Single() : null,
                AnthropicVersion = request.Headers.TryGetValues("anthropic-version", out var version) ? version.Single() : null,
                Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)
            };
            Requests.Add(snapshot);
            return respond(request, Requests.Count - 1);
        }
    }
}
