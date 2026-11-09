using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Xunit;

namespace Lumen.Tests;

public sealed class OpenAIProviderTests
{
    private const string Secret = "synthetic-key-do-not-persist-73ad";
    private static ProviderConfiguration Configuration(string providerId = "openai") => new(Guid.NewGuid().ToString("D"), providerId, ProviderKind.OpenAI, "https://provider.test/v1/");
    private static OpenAIProvider Provider(HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task>? delay = null) => new(new HttpTransport(new HttpClient(handler), delay), new CapabilityResolver());
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        public int Requests { get; private set; }
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) { this.respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Requests++; return Task.FromResult(respond(request)); }
    }

    [Theory]
    [InlineData(401, false, "invalid_key")]
    [InlineData(403, false, "permission_denied")]
    [InlineData(404, null, "invalid_url")]
    [InlineData(429, true, "rate_limited")]
    [InlineData(402, true, "insufficient_credit")]
    [InlineData(302, null, "redirect_rejected")]
    public async Task StatusCodesHaveDistinctSafeCredentialMeaning(int status, bool? valid, string code)
    {
        var waits = new List<TimeSpan>();
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Secret) };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return response;
        });
        var result = await Provider(handler, (wait, _) => { waits.Add(wait); return Task.CompletedTask; }).DiscoverAsync(Configuration(), Secret, CancellationToken.None);
        Assert.Equal(valid, result.CredentialValid);
        Assert.Equal(code, result.ErrorCode);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
        Assert.Equal(status == 429 ? 3 : 1, handler.Requests);
        if (status == 429) Assert.All(waits, wait => Assert.Equal(TimeSpan.FromSeconds(2), wait));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatRateLimitCarriesRetryAfterSecondsOnlyWhenPresent(bool hasHeader)
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("synthetic rejection") };
            if (hasHeader) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        });
        var events = new List<StreamEvent>();
        var configuration = Configuration();
        await Provider(handler).StreamAsync(
            new ChatRequest(configuration, new ModelInfo("openai", "fixture", ModelCapabilities.Text), new[] { new Message(MessageRole.User, new ContentPart[] { new TextPart("hi") }) }), Secret,
            value => { events.Add(value); return Task.CompletedTask; }, CancellationToken.None);
        var error = Assert.Single(events.OfType<StreamError>());
        Assert.Equal("rate_limited", error.Code);
        Assert.Equal(hasHeader ? 90L : (long?)null, error.RetryAfterSeconds);
        Assert.Equal(1, handler.Requests);
        Assert.DoesNotContain(Secret, error.Message);
        Assert.Empty(events.OfType<Done>());
    }

    [Fact]
    public async Task ListingAuthenticatesWithoutChatOrProbesAndKeepsUnknownCapabilitiesUnknown()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://provider.test/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal(Secret, request.Headers.Authorization!.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"unrecognized-2026\"}]}") };
        });
        var result = await Provider(handler).DiscoverAsync(Configuration(), Secret, CancellationToken.None);
        Assert.True(result.CredentialValid);
        Assert.Equal(1, handler.Requests);
        var model = Assert.Single(result.Models);
        Assert.False(model.Has(ModelCapabilities.Vision));
        Assert.NotEqual(CapabilitySource.Native, model.SourceOf(ModelCapabilities.Vision));
    }

    [Fact]
    public void UnsupportedReasoningAndToolsDoNotLeakIntoPayloadAndVisionHasCorrectFormat()
    {
        var configuration = Configuration();
        var model = new ModelInfo("openai", "fixture", ModelCapabilities.Text | ModelCapabilities.Vision);
        var image = new ImagePart("image/png", Convert.ToBase64String(new byte[] { 1, 2, 3 }));
        var request = new ChatRequest(configuration, model, new[] { new Message(MessageRole.User, new ContentPart[] { new TextPart("hi"), image }) }, ReasoningIntent.High);
        using var json = JsonDocument.Parse(Provider(new Handler(_ => throw new InvalidOperationException())).BuildPayload(request));
        Assert.False(json.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(json.RootElement.TryGetProperty("reasoning", out _));
        Assert.False(json.RootElement.TryGetProperty("tools", out _));
        Assert.True(json.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.Equal("data:image/png;base64,AQID", json.RootElement.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
        var blind = new ModelInfo("openai", "blind", ModelCapabilities.Text);
        var denied = new ChatRequest(configuration, blind, request.Messages);
        Assert.Equal("vision_not_supported", Assert.Throws<ProviderException>(() => Provider(new Handler(_ => throw new InvalidOperationException())).BuildPayload(denied)).Code);
    }

    [Fact]
    public async Task SseReconstructsFiveArgumentDeltasAndIgnoresUnknownEvents()
    {
        var data = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "openai.sse"));
        var events = await Stream(data);
        Assert.Equal("Hello world", string.Concat(events.OfType<TextDelta>().Select(value => value.Text)));
        var start = Assert.Single(events.OfType<ToolCallStart>());
        Assert.Equal("call_1", start.Id);
        Assert.Equal("read_file", start.Name);
        var deltas = events.OfType<ToolCallArgsDelta>().ToArray();
        Assert.Equal(5, deltas.Length);
        Assert.All(deltas, delta => Assert.Equal(start.Id, delta.Id));
        var end = Assert.Single(events.OfType<ToolCallEnd>());
        Assert.Equal(start.Id, end.Id);
        Assert.Equal("{\"path\":\"a.cs\"}", string.Concat(deltas.Select(delta => delta.Chunk)));
        Assert.Equal("{\"path\":\"a.cs\"}", end.ParsedArgs.GetRawText());
        Assert.Equal(3, Assert.Single(events.OfType<Usage>()).ReasoningTokens);
        Assert.Equal(FinishReason.ToolUse, Assert.Single(events.OfType<Done>()).FinishReason);
        Assert.Empty(events.OfType<StreamError>());
    }

    [Fact]
    public async Task TruncatedJsonPreservesEarlierTextAndFailsSafely()
    {
        var events = await Stream("data: {\"choices\":[{\"delta\":{\"content\":\"kept\"}}]}\n\ndata: {\"choices\":\n\n");
        Assert.Equal("kept", Assert.Single(events.OfType<TextDelta>()).Text);
        Assert.Equal("invalid_stream", Assert.Single(events.OfType<StreamError>()).Code);
        Assert.Empty(events.OfType<Done>());
        var malformed = await Stream("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"id\",\"function\":{\"name\":\"tool\",\"arguments\":\"[1]\"}}]}}]}\n\ndata: [DONE]\n\n");
        Assert.Single(malformed.OfType<StreamError>());
        Assert.Empty(malformed.OfType<ToolCallEnd>());
    }

    [Theory]
    [InlineData("call_changed", "read_file")]
    [InlineData("call_1", "write_file")]
    [InlineData("", "read_file")]
    [InlineData("call_1", "")]
    public async Task ConflictingToolIdentityPreservesEarlierDeltasAndNeverCompletes(string id, string name)
    {
        var first = ToolFrame("call_1", "read_file", "{\"path\":");
        var conflict = ToolFrame(id, name, "\"a.cs\"}");
        var events = await Stream(TextFrame("kept") + first + conflict + "data: [DONE]\n\n");
        Assert.Equal("kept", Assert.Single(events.OfType<TextDelta>()).Text);
        var start = Assert.Single(events.OfType<ToolCallStart>());
        Assert.Equal("call_1", start.Id);
        Assert.Equal("read_file", start.Name);
        var arguments = Assert.Single(events.OfType<ToolCallArgsDelta>());
        Assert.Equal(start.Id, arguments.Id);
        Assert.Equal("{\"path\":", arguments.Chunk);
        AssertInvalidStream(events);
    }

    [Theory]
    [InlineData("call_1", null, "call_changed", "read_file")]
    [InlineData(null, "read_file", "call_1", "write_file")]
    public async Task ToolIdentityIsFrozenEvenBeforeBothFieldsAreKnown(string? firstId, string? firstName, string id, string name)
    {
        var events = await Stream(TextFrame("kept") + ToolFrame(firstId, firstName, "") + ToolFrame(id, name, "{}") + "data: [DONE]\n\n");
        Assert.Equal("kept", Assert.Single(events.OfType<TextDelta>()).Text);
        Assert.Empty(events.OfType<ToolCallStart>());
        AssertInvalidStream(events);
    }

    [Fact]
    public async Task RepeatedToolIdentityAcceptsFiveArgumentDeltas()
    {
        var chunks = new[] { "{\"", "path", "\":\"", "a.cs", "\"}" };
        var events = await Stream(string.Concat(chunks.Select(chunk => ToolFrame("call_1", "read_file", chunk))) + "data: [DONE]\n\n");
        var start = Assert.Single(events.OfType<ToolCallStart>());
        Assert.Equal("read_file", start.Name);
        var arguments = events.OfType<ToolCallArgsDelta>().ToArray();
        Assert.Equal(chunks, arguments.Select(delta => delta.Chunk));
        Assert.All(arguments, delta => Assert.Equal(start.Id, delta.Id));
        var end = Assert.Single(events.OfType<ToolCallEnd>());
        Assert.Equal(start.Id, end.Id);
        Assert.Equal("{\"path\":\"a.cs\"}", end.ParsedArgs.GetRawText());
        Assert.Single(events.OfType<Done>());
        Assert.Empty(events.OfType<StreamError>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("data: {\"choices\":[{\"delta\":{},\"finish_reason\":null}]}\n\n")]
    [InlineData("event: future_event\ndata: {\"future\":true}\n\n")]
    [InlineData("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":1,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n")]
    public async Task EofWithoutTerminalEvidencePreservesTextButNeverCompletes(string tail)
    {
        var events = await Stream(TextFrame("kept") + tail);
        Assert.Equal("kept", Assert.Single(events.OfType<TextDelta>()).Text);
        AssertInvalidStream(events);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"path\":")]
    [InlineData("")]
    public async Task ToolArgumentsAtEofWithoutTerminalEvidenceNeverEndTheCall(string arguments)
    {
        var events = await Stream(TextFrame("kept") + ToolFrame("call_1", "read_file", arguments));
        Assert.Equal("kept", Assert.Single(events.OfType<TextDelta>()).Text);
        Assert.Single(events.OfType<ToolCallStart>());
        Assert.Equal(arguments, Assert.Single(events.OfType<ToolCallArgsDelta>()).Chunk);
        AssertInvalidStream(events);
    }

    [Fact]
    public async Task EmptyEofIsNotSuccessfulCompletion() => AssertInvalidStream(await Stream(""));

    [Theory]
    [InlineData("data: [DONE]\n\n", FinishReason.Other)]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n", FinishReason.Stop)]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"length\"}]}\n\n", FinishReason.MaxTokens)]
    public async Task EitherTerminalMarkerAllowsCompletionAndRetainsUsage(string terminal, FinishReason reason)
    {
        var usage = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}\n\n";
        var events = await Stream(TextFrame("kept") + usage + terminal);
        Assert.Equal("kept", Assert.Single(events.OfType<TextDelta>()).Text);
        Assert.Single(events.OfType<Usage>());
        Assert.Equal(reason, Assert.Single(events.OfType<Done>()).FinishReason);
        Assert.Empty(events.OfType<StreamError>());
    }

    [Fact]
    public async Task ToolFinishReasonWithoutDoneMarkerStillReconstructsFiveDeltasAndTrailingUsage()
    {
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "openai.sse"));
        var events = await Stream(fixture.Replace("data: [DONE]", ""));
        Assert.Equal(5, events.OfType<ToolCallArgsDelta>().Count());
        Assert.Equal("{\"path\":\"a.cs\"}", Assert.Single(events.OfType<ToolCallEnd>()).ParsedArgs.GetRawText());
        Assert.Single(events.OfType<Usage>());
        Assert.Equal(FinishReason.ToolUse, Assert.Single(events.OfType<Done>()).FinishReason);
        Assert.Empty(events.OfType<StreamError>());
    }

    [Theory]
    [InlineData("{\"path\":", "call_1", "read_file")]
    [InlineData("{}", null, "read_file")]
    [InlineData("{}", "call_1", null)]
    public async Task TerminalEvidenceDoesNotMakeAnUnfinishedToolCallValid(string arguments, string? id, string? name)
    {
        foreach (var terminal in new[] { "data: [DONE]\n\n", "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" })
        {
            var events = await Stream(TextFrame("kept") + ToolFrame(id, name, arguments) + terminal);
            Assert.Equal("kept", Assert.Single(events.OfType<TextDelta>()).Text);
            AssertInvalidStream(events);
        }
    }

    [Fact]
    public async Task OpenRouterReplayMergesPerFrameDetailsWithoutDroppingBlocksOrOpaqueState()
    {
        // Native detail fields and delta/replay semantics: https://openrouter.ai/docs/use-cases/reasoning-tokens
        const string first = "[{\"type\":\"reasoning.text\",\"index\":2,\"id\":\"text-1\",\"text\":\"checking \",\"signature\":null,\"format\":\"anthropic-claude-v1\"},{\"type\":\"reasoning.encrypted\",\"index\":3,\"id\":\"encrypted-1\",\"data\":\"opaque-\",\"format\":\"anthropic-claude-v1\"}]";
        const string second = "[{\"type\":\"reasoning.text\",\"index\":2,\"id\":\"text-1\",\"text\":\"files\"},{\"type\":\"reasoning.summary\",\"index\":4,\"id\":\"summary-1\",\"summary\":\"first \",\"format\":\"openai-responses-v1\"},{\"type\":\"reasoning.text\",\"index\":5,\"id\":\"text-1\",\"text\":\"separate\",\"signature\":\"separate-signature\"}]";
        const string third = "[{\"type\":\"reasoning.encrypted\",\"index\":3,\"id\":\"encrypted-1\",\"data\":\"data\"},{\"type\":\"reasoning.text\",\"index\":2,\"id\":\"text-1\",\"text\":\"\",\"signature\":\"signed-block\"},{\"type\":\"reasoning.summary\",\"index\":4,\"id\":\"summary-1\",\"summary\":\"second\"}]";
        var events = await Stream("data: {\"choices\":[{\"delta\":{\"reasoning_details\":" + first + "}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"reasoning_details\":" + second + "}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"reasoning_details\":" + third + "}}]}\n\ndata: [DONE]\n\n");
        Assert.Empty(events.OfType<StreamError>());
        var deltas = events.OfType<ReasoningDelta>().ToArray();
        Assert.Equal(new[] { first, second, third }, deltas.Select(delta => delta.ProviderState));
        var parts = deltas.Select(delta => new ReasoningPart(delta.Text, delta.ProviderState)).Cast<ContentPart>().ToArray();
        using var payload = JsonDocument.Parse(OpenRouterReplayPayload(parts));
        var details = payload.RootElement.GetProperty("messages")[0].GetProperty("reasoning_details");
        Assert.Equal(4, details.GetArrayLength());
        Assert.Equal(new[] { 2, 3, 4, 5 }, details.EnumerateArray().Select(detail => detail.GetProperty("index").GetInt32()));
        Assert.Equal("checking files", details[0].GetProperty("text").GetString());
        Assert.Equal("signed-block", details[0].GetProperty("signature").GetString());
        Assert.Equal("anthropic-claude-v1", details[0].GetProperty("format").GetString());
        Assert.Equal("opaque-data", details[1].GetProperty("data").GetString());
        Assert.Equal("anthropic-claude-v1", details[1].GetProperty("format").GetString());
        Assert.Equal("first second", details[2].GetProperty("summary").GetString());
        Assert.Equal("openai-responses-v1", details[2].GetProperty("format").GetString());
        Assert.Equal("separate", details[3].GetProperty("text").GetString());
        Assert.Equal("separate-signature", details[3].GetProperty("signature").GetString());
        Assert.Equal(new[] { first, second, third }, parts.Cast<ReasoningPart>().Select(part => part.ProviderState));
    }

    [Fact]
    public void OpenRouterReplayUsesTypeAndIdAsWellAsIndexForIdentity()
    {
        var parts = new ContentPart[]
        {
            new ReasoningPart("", "[{\"type\":\"reasoning.text\",\"index\":0,\"id\":\"a\",\"text\":\"A\"},{\"type\":\"reasoning.text\",\"index\":0,\"id\":\"b\",\"text\":\"B\"},{\"type\":\"reasoning.encrypted\",\"index\":0,\"id\":\"a\",\"data\":\"X\"},{\"type\":\"reasoning.text\",\"index\":1,\"id\":null,\"text\":\"C\"}]"),
            new ReasoningPart("", "[{\"type\":\"reasoning.text\",\"index\":0,\"id\":\"a\",\"text\":\"1\"},{\"type\":\"reasoning.text\",\"index\":0,\"id\":\"b\",\"text\":\"2\"},{\"type\":\"reasoning.encrypted\",\"index\":0,\"id\":\"a\",\"data\":\"Y\"},{\"type\":\"reasoning.text\",\"index\":1,\"text\":\"3\"}]")
        };
        using var payload = JsonDocument.Parse(OpenRouterReplayPayload(parts));
        var details = payload.RootElement.GetProperty("messages")[0].GetProperty("reasoning_details");
        Assert.Equal(4, details.GetArrayLength());
        Assert.Equal("A1", details[0].GetProperty("text").GetString());
        Assert.Equal("B2", details[1].GetProperty("text").GetString());
        Assert.Equal("XY", details[2].GetProperty("data").GetString());
        Assert.Equal("C3", details[3].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, details[3].GetProperty("id").ValueKind);
    }

    [Fact]
    public void OpenRouterReplayMergesIdentifiedUnindexedDetailsButKeepsAnonymousBlocks()
    {
        var parts = new ContentPart[]
        {
            new ReasoningPart("", "[{\"type\":\"reasoning.text\",\"id\":\"a\",\"text\":\"first \",\"signature\":\"signed\"},{\"type\":\"reasoning.encrypted\",\"data\":\"anonymous-one\"}]"),
            new ReasoningPart("", "[{\"type\":\"reasoning.text\",\"id\":\"a\",\"text\":\"second\"},{\"type\":\"reasoning.encrypted\",\"data\":\"anonymous-two\"}]")
        };
        using var payload = JsonDocument.Parse(OpenRouterReplayPayload(parts));
        var details = payload.RootElement.GetProperty("messages")[0].GetProperty("reasoning_details");
        Assert.Equal(3, details.GetArrayLength());
        Assert.Equal("first second", details[0].GetProperty("text").GetString());
        Assert.Equal("signed", details[0].GetProperty("signature").GetString());
        Assert.Equal("anonymous-one", details[1].GetProperty("data").GetString());
        Assert.Equal("anonymous-two", details[2].GetProperty("data").GetString());
    }

    [Fact]
    public void OpenRouterReplayRetainsIdentityAndOpaqueMetadataAcrossSparseDeltas()
    {
        var parts = new ContentPart[]
        {
            new ReasoningPart("", "[{\"type\":\"reasoning.text\",\"index\":8,\"id\":\"text-8\",\"text\":\"a\",\"signature\":\"opaque-signature\",\"format\":\"anthropic-claude-v1\"}]"),
            new ReasoningPart("", "[{\"type\":\"reasoning.text\",\"index\":8,\"text\":\"a\",\"signature\":null,\"format\":null}]"),
            new ReasoningPart("", "[{\"type\":\"reasoning.text\",\"id\":\"text-8\",\"text\":\"b\"}]")
        };
        using var payload = JsonDocument.Parse(OpenRouterReplayPayload(parts));
        var detail = Assert.Single(payload.RootElement.GetProperty("messages")[0].GetProperty("reasoning_details").EnumerateArray());
        Assert.Equal("aab", detail.GetProperty("text").GetString());
        Assert.Equal(8, detail.GetProperty("index").GetInt32());
        Assert.Equal("text-8", detail.GetProperty("id").GetString());
        Assert.Equal("opaque-signature", detail.GetProperty("signature").GetString());
        Assert.Equal("anthropic-claude-v1", detail.GetProperty("format").GetString());
    }

    private static string OpenRouterReplayPayload(ContentPart[] parts)
    {
        var configuration = new ProviderConfiguration(Guid.NewGuid().ToString("D"), "openrouter", ProviderKind.OpenRouter, "https://provider.test/v1/");
        var request = new ChatRequest(configuration, new ModelInfo("openrouter", "fixture", ModelCapabilities.Text), new[] { new Message(MessageRole.Assistant, parts) });
        var handler = new Handler(_ => throw new InvalidOperationException("Replay must not send HTTP."));
        var payload = Provider(handler).BuildPayload(request);
        Assert.Equal(0, handler.Requests);
        return payload;
    }

    private static string TextFrame(string text) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = text } } } }) + "\n\n";
    private static string ToolFrame(string? id, string? name, string arguments) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 0, id, function = new { name, arguments } } } } } } }) + "\n\n";
    private static void AssertInvalidStream(List<StreamEvent> events)
    {
        var error = Assert.Single(events.OfType<StreamError>());
        Assert.Equal("invalid_stream", error.Code);
        Assert.Equal(error.Code, error.Message);
        Assert.Empty(events.OfType<Done>());
        Assert.Empty(events.OfType<ToolCallEnd>());
    }

    [Fact]
    public async Task PaidPostIsNeverAutomaticallyRetriedAndErrorBodyIsNotExposed()
    {
        var handler = new Handler(_ => new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent(Secret) });
        var configuration = Configuration();
        var events = new List<StreamEvent>();
        await Provider(handler).StreamAsync(new ChatRequest(configuration, new ModelInfo("openai", "test", ModelCapabilities.Text), new[] { new Message(MessageRole.User, new ContentPart[] { new TextPart("hi") }) }), Secret,
            value => { events.Add(value); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(1, handler.Requests);
        var error = Assert.Single(events.OfType<StreamError>());
        Assert.Equal("rate_limited", error.Code);
        Assert.DoesNotContain(Secret, error.Message);
    }

    [Fact]
    public void ProvidersStayNetstandardAndNeverReferenceVsOrWebview()
    {
        var assembly = typeof(IChatProvider).Assembly;
        Assert.Equal(".NETStandard,Version=v2.0", assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()!.FrameworkName);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name => name.Name!.StartsWith("Microsoft.VisualStudio", StringComparison.Ordinal) || name.Name.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://remote.test/")]
    [InlineData("https://user:secret@provider.test/")]
    [InlineData("https://provider.test/?key=secret")]
    [InlineData("file:///C:/data")]
    public void ConfigurationRejectsInsecureEndpoints(string url) => Assert.Throws<ArgumentException>(() => new ProviderConfiguration(Guid.NewGuid().ToString("D"), "fixture", ProviderKind.OpenAI, url));

    private static async Task<List<StreamEvent>> Stream(string data)
    {
        var events = new List<StreamEvent>();
        var configuration = Configuration();
        await Provider(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") })).StreamAsync(
            new ChatRequest(configuration, new ModelInfo("openai", "fixture", ModelCapabilities.Text), new[] { new Message(MessageRole.User, new ContentPart[] { new TextPart("hi") }) }), Secret,
            value => { events.Add(value); return Task.CompletedTask; }, CancellationToken.None);
        return events;
    }
}
