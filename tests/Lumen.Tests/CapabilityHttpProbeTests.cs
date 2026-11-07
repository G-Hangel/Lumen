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

public sealed class CapabilityHttpProbeTests
{
    private const string Secret = "synthetic-offline-probe-key";
    private const int MaxBytes = 16 * 1024 * 1024;
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    public static IEnumerable<object[]> Endpoints
    {
        get
        {
            var endpoints = new (ProviderKind Kind, string Provider, string BaseUrl, string Url, int Tokens)[]
            {
                (ProviderKind.OpenAI, "openai", "https://provider.test/v1", "https://provider.test/v1/chat/completions", 16),
                (ProviderKind.OpenAI, "groq", "https://provider.test/custom/api/v1/", "https://provider.test/custom/api/v1/chat/completions", 1),
                (ProviderKind.OpenRouter, "openrouter", "https://router.test/api/v1", "https://router.test/api/v1/chat/completions", 1),
                (ProviderKind.LMStudio, "lmstudio", "http://localhost:1234", "http://localhost:1234/v1/chat/completions", 1),
                (ProviderKind.LMStudio, "lmstudio", "http://localhost:1234/v1", "http://localhost:1234/v1/chat/completions", 1),
                (ProviderKind.LMStudio, "lmstudio", "http://localhost:1234/api/v0/", "http://localhost:1234/v1/chat/completions", 1),
                (ProviderKind.Azure, "azure", "https://azure.test", "https://azure.test/openai/v1/chat/completions", 16),
                (ProviderKind.Azure, "azure", "https://azure.test/openai", "https://azure.test/openai/v1/chat/completions", 16),
                (ProviderKind.Azure, "azure", "https://azure.test/openai/v1/", "https://azure.test/openai/v1/chat/completions", 16)
            };
            foreach (var endpoint in endpoints)
                foreach (var capability in new[] { ModelCapabilities.Vision, ModelCapabilities.Tools })
                    yield return new object[] { endpoint.Kind, endpoint.Provider, endpoint.BaseUrl, endpoint.Url, endpoint.Tokens, capability };
        }
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task SendsOneTinyNonStreamingProbeToKnownEndpointWithCorrectAuthentication(ProviderKind kind,
        string provider, string baseUrl, string url, int tokens, ModelCapabilities capability)
    {
        var configuration = Configuration(kind, provider, baseUrl, "selected/deployment");
        var model = new ModelInfo(provider, "selected/deployment", ModelCapabilities.Text | ModelCapabilities.Reasoning,
            reasoning: new ReasoningSupport(ReasoningKind.Effort, new[] { ReasoningIntent.High }, mandatory: true));
        using var handler = new Handler(request =>
        {
            Assert.Equal(url, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Empty(request.RequestUri.Query);
            if (kind == ProviderKind.Azure)
            {
                Assert.Equal(Secret, Assert.Single(request.Headers.GetValues("api-key")));
                Assert.Null(request.Headers.Authorization);
            }
            else
            {
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal(Secret, request.Headers.Authorization.Parameter);
                Assert.False(request.Headers.Contains("api-key"));
            }
            return Json(200, "{\"choices\":[{\"message\":{\"content\":\"\"},\"finish_reason\":\"length\"}]}");
        });
        using var client = new HttpClient(handler);
        Assert.Null(await Service(client).ProbeAsync(configuration, model, Secret, capability, CancellationToken.None));
        Assert.Equal(1, handler.Requests);
        using var document = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var root = document.RootElement;
        Assert.Equal("selected/deployment", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        var tokenField = tokens == 16 ? "max_completion_tokens" : "max_tokens";
        Assert.Equal(tokens, root.GetProperty(tokenField).GetInt32());
        Assert.False(root.TryGetProperty(tokens == 16 ? "max_tokens" : "max_completion_tokens", out _));
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal(new[] { "role", "content" }, message.EnumerateObject().Select(field => field.Name));
        if (capability == ModelCapabilities.Vision)
        {
            Assert.Equal(4, root.EnumerateObject().Count());
            var content = message.GetProperty("content");
            Assert.Equal(2, content.GetArrayLength());
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal(".", content[0].GetProperty("text").GetString());
            Assert.Equal("image_url", content[1].GetProperty("type").GetString());
            var image = content[1].GetProperty("image_url");
            Assert.Equal("low", image.GetProperty("detail").GetString());
            Assert.Equal("data:image/png;base64," + Pixel, image.GetProperty("url").GetString());
            AssertValidPixel(Convert.FromBase64String(Pixel));
        }
        else
        {
            Assert.Equal(6, root.EnumerateObject().Count());
            Assert.Equal(".", message.GetProperty("content").GetString());
            Assert.Equal("none", root.GetProperty("tool_choice").GetString());
            var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
            Assert.Equal("function", tool.GetProperty("type").GetString());
            var function = tool.GetProperty("function");
            Assert.Equal("lumen_probe", function.GetProperty("name").GetString());
            Assert.Equal("object", function.GetProperty("parameters").GetProperty("type").GetString());
            Assert.Empty(function.GetProperty("parameters").GetProperty("properties").EnumerateObject());
        }
        foreach (var forbidden in new[] { "reasoning", "reasoning_effort", "thinking", "chat_template_kwargs", "stream_options" })
            Assert.False(root.TryGetProperty(forbidden, out _));
        Assert.DoesNotContain(Secret, document.RootElement.GetRawText());
        Assert.Equal(ModelCapabilities.Text | ModelCapabilities.Reasoning, model.Capabilities);
    }

    [Theory]
    [InlineData(ProviderKind.OpenAI, true)]
    [InlineData(ProviderKind.OpenRouter, true)]
    [InlineData(ProviderKind.LMStudio, true)]
    [InlineData(ProviderKind.Azure, true)]
    [InlineData(ProviderKind.Anthropic, false)]
    [InlineData(ProviderKind.Gemini, false)]
    [InlineData(ProviderKind.Ollama, false)]
    [InlineData((ProviderKind)123, false)]
    public void SupportsOnlyVerifiedChatCompletionsProviders(ProviderKind kind, bool supported) =>
        Assert.Equal(supported, CapabilityHttpProbe.Supports(kind));

    [Theory]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.Gemini)]
    [InlineData(ProviderKind.Ollama)]
    public async Task UnsupportedProviderDoesNotSendAnyRequest(ProviderKind kind)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var configuration = Configuration(kind);
        Assert.Null(await Service(client).ProbeAsync(configuration, Model(configuration), null, ModelCapabilities.Vision, CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData(ModelCapabilities.Reasoning)]
    [InlineData(ModelCapabilities.Text)]
    [InlineData(ModelCapabilities.None)]
    [InlineData(ModelCapabilities.ParallelTools)]
    [InlineData(ModelCapabilities.Streaming)]
    [InlineData(ModelCapabilities.StructuredOutput)]
    [InlineData(ModelCapabilities.Pdf)]
    [InlineData(ModelCapabilities.Vision | ModelCapabilities.Tools)]
    [InlineData(ModelCapabilities.Vision | ModelCapabilities.Reasoning)]
    [InlineData((ModelCapabilities)1024)]
    public async Task ReasoningAndAnyOtherOrCombinedCapabilityCannotSpendTokens(ModelCapabilities capability)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var configuration = Configuration();
        Assert.Null(await Service(client).ProbeAsync(configuration, Model(configuration), null, capability, CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("different-provider", "selected")]
    [InlineData("openai", "unselected")]
    public async Task MismatchedProviderOrSelectionDoesNotSend(string provider, string modelId)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var model = new ModelInfo(provider, modelId, ModelCapabilities.Text);
        Assert.Null(await Service(client).ProbeAsync(Configuration(), model, null, ModelCapabilities.Tools, CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    public static IEnumerable<object[]> Acceptances
    {
        get
        {
            foreach (var body in new[]
            {
                "{}", "{\"choices\":[]}", "{\"error\":null}",
                "{\"choices\":[{\"message\":{\"content\":null},\"finish_reason\":\"length\"}]}",
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\".\"},\"finish_reason\":\"stop\"}]}",
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"I support vision and tools.\"},\"finish_reason\":\"stop\"}]}",
                "{\"ignored_parameters\":[\"tools\",\"tool_choice\"],\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}",
                "{\"ignored_parameters\":[\"image_url\"],\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}"
            })
                foreach (var capability in new[] { ModelCapabilities.Tools, ModelCapabilities.Vision })
                    yield return new object[] { capability, body };
        }
    }

    [Theory]
    [MemberData(nameof(Acceptances))]
    public async Task AcceptanceWithoutSemanticEvidenceIsInconclusiveAndNeverCached(ModelCapabilities capability, string body)
    {
        using var handler = new Handler(_ => Json(200, body));
        using var client = new HttpClient(handler);
        var configuration = Configuration();
        var service = Service(client);
        var store = new CapabilityProbeStore();
        var session = Session(service, configuration, store);
        Assert.Null(await session.ProbeAsync(configuration.Id, "selected", capability, true));
        Assert.Null(await session.ProbeAsync(configuration.Id, "selected", capability, true));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(4, session.RemainingBudget);
        Assert.Null(store.Get(configuration.Id, "selected", capability));
        using var cache = JsonDocument.Parse(store.ExportJson());
        Assert.Empty(cache.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Null(await Session(service, configuration, store).ProbeAsync(configuration.Id, "selected", capability, true));
        Assert.Equal(2, handler.Requests);
        Assert.Null(store.Get(configuration.Id, "selected", capability));
    }

    public static IEnumerable<object[]> Rejections
    {
        get
        {
            yield return new object[] { ModelCapabilities.Tools, Error("unsupported_parameter", "tools", "This model does not support tools.", null) };
            yield return new object[] { ModelCapabilities.Tools, Error(null, "tools", "This model does not support tools.") };
            yield return new object[] { ModelCapabilities.Tools, Error(null, null, "Tool calling is not supported by this model.") };
            yield return new object[] { ModelCapabilities.Tools, Error(null, null, "The selected model does not support function calling.") };
            yield return new object[] { ModelCapabilities.Tools, Error("unsupported_parameter", "tools", "Unsupported parameter: 'tools' is not supported with this model.") };
            yield return new object[] { ModelCapabilities.Vision, Error("unsupported_parameter", "messages[0].content[1].image_url", "This model does not support image_url.", null) };
            yield return new object[] { ModelCapabilities.Vision, Error(null, "messages[0].content[1].type", "This model does not support image inputs.") };
            yield return new object[] { ModelCapabilities.Vision, Error(null, null, "Images are not supported by this model.") };
            yield return new object[] { ModelCapabilities.Vision, Error(null, "image_url", "IMAGE_URL IS NOT SUPPORTED BY THIS MODEL.") };
        }
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task ExplicitStructuredRejectionIsFalseAndOnlyForRequestedCapability(ModelCapabilities capability, string body)
    {
        foreach (var status in new[] { 400, 422 })
        {
            using var handler = new Handler(_ => Json(status, body));
            using var client = new HttpClient(handler);
            var configuration = Configuration();
            var service = Service(client);
            Assert.False(await service.ProbeAsync(configuration, Model(configuration), null, capability, CancellationToken.None));
            var other = capability == ModelCapabilities.Tools ? ModelCapabilities.Vision : ModelCapabilities.Tools;
            Assert.Null(await service.ProbeAsync(configuration, Model(configuration), null, other, CancellationToken.None));
            Assert.Equal(2, handler.Requests);
        }
    }

    public static IEnumerable<object[]> UnknownResponses
    {
        get
        {
            foreach (var status in new[] { 401, 403, 402, 429, 500, 502, 503, 404, 302, 201, 204 })
                yield return new object[] { status, Error("unsupported_parameter", "tools", "This model does not support tools.") };
            foreach (var body in new[]
            {
                "tools are not supported", "{\"message\":\"tools are not supported\"}",
                "{\"error\":\"tools are not supported\"}", "{\"error\":{\"message\":\"tools are not supported\"}}",
                Error("context_length_exceeded", "messages", "Context length exceeded with tools. Tools are not supported at this context length."),
                Error("invalid_api_key", "tools", "Tools are not supported with this key."),
                Error("insufficient_quota", "tools", "Tools are not supported without quota."),
                Error("rate_limit_exceeded", "tools", "Tools are not supported at this rate."),
                Error(null, "tools", "Tools are not supported due to authentication."),
                Error(null, "tools", "Tools are not supported because quota is exhausted."),
                Error(null, "tools", "Tools are not supported at this context length."),
                Error("unsupported_parameter", "tools", "Unsupported parameter tools.", "authentication_error"),
                "{\"error\":{\"type\":\"invalid_request_error\",\"code\":401,\"message\":\"Tools are not supported.\"}}",
                "{\"error\":{\"type\":\"invalid_request_error\",\"param\":{},\"message\":\"Tools are not supported.\"}}",
                "{\"error\":{\"type\":17,\"code\":\"unsupported_parameter\",\"param\":\"tools\"}}",
                Error(null, "max_tokens", "Tools are not supported with this output limit."),
                Error(null, "tools", "Invalid tool schema."), Error(null, "tools", "Invalid value."),
                Error(null, "tool_choice", "Invalid tool name."), Error(null, null, "Unsupported model."),
                Error(null, "image_url", "Unsupported image format."),
                Error("unsupported_value", "messages[0].content[1].image_url.url", "Unsupported image URL scheme."),
                Error(null, "messages", "Image is too large."),
                Error(null, null, "The word tools appears in unrelated body text."),
                Error(null, null, "This model does not support toolshed."),
                Error(null, null, new string('x', 4097) + " Tools are not supported."),
                "{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"\\uD800\"}}",
                "{", "[]", "null"
            })
                foreach (var status in new[] { 400, 422 }) yield return new object[] { status, body };
            foreach (var body in new[] { "[]", "null", "1", "\"ok\"", "", "{", "{\"error\":{}}", "{\"error\":\"bad\"}", new string('[', 65) + new string(']', 65) })
                yield return new object[] { 200, body };
        }
    }

    public static IEnumerable<object[]> ConditionalAndParameterErrors
    {
        get
        {
            var errors = new (string? Param, string Message)[]
            {
                ("tool_choice", ""),
                ("tool_choice", "Unsupported parameter: 'tool_choice'."),
                (null, "This model does not support tool_choice."),
                ("tool_choice", "This model does not support tools."),
                ("tools", ""),
                ("tools", "Unsupported parameter: 'tools'."),
                ("tools", "Tools are not supported."),
                ("image_url", ""),
                ("image_url", "Unsupported parameter: 'image_url'."),
                ("image_url", "IMAGE_URL IS NOT SUPPORTED."),
                ("tools", "Tools are only supported when streaming is enabled."),
                ("tools", "This model does not support tools when streaming is disabled."),
                ("tools", "This model does not support tools unless streaming is enabled."),
                ("tools", "Tools are not allowed."),
                ("tools", "Tool calling is not supported by this model for your account."),
                ("tools", "This model does not support tools. Your account has restricted access."),
                ("tools", "This model does not support tools at this context length."),
                ("tools", "This model does not support tools because the token budget is exhausted."),
                ("tools", "This model does not support tools without credits."),
                ("image_url", "Image_url is only supported when streaming is enabled."),
                ("image_url", "Image inputs are not allowed."),
                ("messages", "Invalid content type. image_url is only supported by certain models."),
                ("image_url", "Images are not supported by this model in this region."),
                ("image_url", "This model does not support images with your subscription."),
                ("image_url", "This model does not support images because quota is exhausted.")
            };
            foreach (var error in errors)
                foreach (var code in new string?[] { null, "unsupported_parameter", "unsupported_value" })
                    foreach (var status in new[] { 400, 422 })
                        yield return new object[] { status, Error(code, error.Param, error.Message) };
        }
    }

    [Theory]
    [MemberData(nameof(UnknownResponses))]
    [MemberData(nameof(ConditionalAndParameterErrors))]
    public async Task UnknownErrorsAreNotCachedAsPermanentNegativesAndPaidPostNeverRetries(int status, string body)
    {
        foreach (var capability in new[] { ModelCapabilities.Tools, ModelCapabilities.Vision })
        {
            var calls = 0;
            using var handler = new Handler(_ => ++calls == 1 ? Json(status, body) : Json(200, "{}"));
            using var client = new HttpClient(handler);
            var service = Service(client);
            var configuration = Configuration();
            var store = new CapabilityProbeStore();
            var session = Session(service, configuration, store);
            Assert.Null(await session.ProbeAsync(configuration.Id, "selected", capability, true));
            Assert.Null(await session.ProbeAsync(configuration.Id, "selected", capability, true));
            Assert.Equal(1, handler.Requests);
            Assert.Equal(4, session.RemainingBudget);
            Assert.Null(store.Get(configuration.Id, "selected", capability));
            using var cache = JsonDocument.Parse(store.ExportJson());
            Assert.Empty(cache.RootElement.GetProperty("entries").EnumerateArray());
            Assert.DoesNotContain(Secret, store.ExportJson());
            Assert.Null(await Session(service, configuration, store).ProbeAsync(configuration.Id, "selected", capability, true));
            Assert.Equal(2, handler.Requests);
            Assert.Null(store.Get(configuration.Id, "selected", capability));
            using var nextCache = JsonDocument.Parse(store.ExportJson());
            Assert.Empty(nextCache.RootElement.GetProperty("entries").EnumerateArray());
        }
    }

    [Fact]
    public async Task OptInSessionUsesOnlyCurrentSelectionAndStopsAtVisibleFiveAttemptBudget()
    {
        using var handler = new Handler(_ => Json(429, ""));
        using var client = new HttpClient(handler);
        var configuration = Configuration(selected: null);
        var session = Session(Service(client), configuration);
        Assert.Null(await session.ProbeAsync(configuration.Id, "selected", ModelCapabilities.Vision, false));
        Assert.Null(await session.ProbeAsync(configuration.Id, "selected", ModelCapabilities.Reasoning, true));
        Assert.Equal(5, CapabilityProbe.MaxProbesPerSession);
        Assert.Equal(5, session.RemainingBudget);
        Assert.Equal(0, handler.Requests);
        for (var index = 0; index < 6; index++)
        {
            Assert.Null(await session.ProbeAsync(configuration.Id, "selection-" + index, ModelCapabilities.Tools, true));
            Assert.Equal(Math.Max(0, 4 - index), session.RemainingBudget);
        }
        Assert.Equal(5, handler.Requests);
        Assert.Equal(Enumerable.Range(0, 5).Select(index => "selection-" + index), handler.Bodies.Select(body =>
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("model").GetString();
        }));
    }

    [Theory]
    [InlineData(ModelCapabilities.Tools, "tools")]
    [InlineData(ModelCapabilities.Vision, "image_url")]
    public async Task UnconditionalModelRejectionIsPermanentAcrossSessionsWithoutDuplicatePost(ModelCapabilities capability, string parameter)
    {
        using var handler = new Handler(_ => Json(400, Error("unsupported_parameter", parameter, "This model does not support " + parameter + ".", null)));
        using var client = new HttpClient(handler);
        var configuration = Configuration();
        var service = Service(client);
        var session = Session(service, configuration);
        Assert.False(await session.ProbeAsync(configuration.Id, "selected", capability, true));
        Assert.False(await session.ProbeAsync(configuration.Id, "selected", capability, true));
        using var cache = JsonDocument.Parse(session.Store.ExportJson());
        var entry = Assert.Single(cache.RootElement.GetProperty("entries").EnumerateArray());
        Assert.False(entry.GetProperty("supported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("expiresAt").ValueKind);
        var imported = new CapabilityProbeStore();
        imported.ImportJson(session.Store.ExportJson());
        var next = Session(service, configuration, imported);
        Assert.False(await next.ProbeAsync(configuration.Id, "selected", capability, true));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(5, next.RemainingBudget);
    }

    [Fact]
    public async Task CredentialIsPerCallAndNeverRetainedForNextRequest()
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            if (++calls == 1) Assert.Equal(Secret, request.Headers.Authorization!.Parameter);
            else Assert.Null(request.Headers.Authorization);
            return Json(200, "{}");
        });
        using var client = new HttpClient(handler);
        var service = Service(client);
        var configuration = Configuration();
        Assert.Null(await service.ProbeAsync(configuration, Model(configuration), Secret, ModelCapabilities.Tools, CancellationToken.None));
        Assert.Null(await service.ProbeAsync(configuration, Model(configuration), null, ModelCapabilities.Vision, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkFailureOrTransportTimeoutIsUnknownAndNotCached(bool timeout)
    {
        using var handler = new Handler(_ => throw (timeout ? new TaskCanceledException(Secret) : new HttpRequestException(Secret)));
        using var client = new HttpClient(handler);
        var configuration = Configuration();
        var session = Session(Service(client), configuration);
        Assert.Null(await session.ProbeAsync(configuration.Id, "selected", ModelCapabilities.Vision, true));
        Assert.Null(session.Store.Get(configuration.Id, "selected", ModelCapabilities.Vision));
        Assert.Null(await session.ProbeAsync(configuration.Id, "selected", ModelCapabilities.Vision, true));
        Assert.Equal(1, handler.Requests);
        Assert.DoesNotContain(Secret, session.Store.ExportJson());
    }

    [Fact]
    public async Task InvalidCredentialHeaderDoesNotThrowOrSendOrLeak()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var configuration = Configuration();
        Assert.Null(await Service(client).ProbeAsync(configuration, Model(configuration), Secret + "\r\n", ModelCapabilities.Tools, CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task PrecancelledProbePropagatesCancellationWithoutSending()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new Handler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var configuration = Configuration();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(client).ProbeAsync(
            configuration, Model(configuration), null, ModelCapabilities.Tools, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task CancellationDuringSendPropagatesAndConsumesAttemptWithoutCaching()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler(_ => { cancellation.Cancel(); throw new HttpRequestException(Secret); });
        using var client = new HttpClient(handler);
        var configuration = Configuration();
        var session = Session(Service(client), configuration);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ProbeAsync(
            configuration.Id, "selected", ModelCapabilities.Tools, true, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(4, session.RemainingBudget);
        Assert.Null(session.Store.Get(configuration.Id, "selected", ModelCapabilities.Tools));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(422)]
    public async Task CancellationInterruptsBodyReadAndDisposesResponse(int status)
    {
        using var stream = new BlockingStream();
        using var handler = new Handler(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(stream) });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var configuration = Configuration();
        var session = Session(Service(client), configuration);
        var operation = session.ProbeAsync(configuration.Id, "selected", ModelCapabilities.Tools, true, cancellation.Token);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await session.ProbeAsync(configuration.Id, "selected", ModelCapabilities.Tools, true));
        Assert.Equal(1, handler.Requests);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(4, session.RemainingBudget);
        Assert.Null(session.Store.Get(configuration.Id, "selected", ModelCapabilities.Tools));
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(400, false)]
    [InlineData(422, false)]
    [InlineData(200, true)]
    public async Task OversizedBodyIsUnknownEvenWithSuccessfulStatusOrCapabilityError(int status, bool declared)
    {
        var prefix = status == 200 ? "{}" : Error("unsupported_parameter", "tools", "", null);
        using var stream = new PaddedStream(prefix, MaxBytes + 1);
        using var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(stream) };
            if (declared) response.Content.Headers.ContentLength = MaxBytes + 1;
            else Assert.Null(response.Content.Headers.ContentLength);
            return response;
        });
        using var client = new HttpClient(handler);
        var configuration = Configuration();
        Assert.Null(await Service(client).ProbeAsync(configuration, Model(configuration), Secret, ModelCapabilities.Tools, CancellationToken.None));
        Assert.Equal(declared ? 0 : MaxBytes + 1, stream.BytesRead);
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task ExactBoundedObjectIsInconclusiveAndResponseDisposed()
    {
        using var stream = new PaddedStream("{}", MaxBytes);
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var client = new HttpClient(handler);
        var configuration = Configuration();
        Assert.Null(await Service(client).ProbeAsync(configuration, Model(configuration), null, ModelCapabilities.Tools, CancellationToken.None));
        Assert.Equal(MaxBytes, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task BodyReadFailureIsUnknownWithoutRawResponseException()
    {
        using var stream = new FailingStream();
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var client = new HttpClient(handler);
        var configuration = Configuration();
        Assert.Null(await Service(client).ProbeAsync(configuration, Model(configuration), Secret, ModelCapabilities.Vision, CancellationToken.None));
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Requests);
    }

    private static CapabilityHttpProbe Service(HttpClient client) => new(new HttpTransport(client,
        (_, _) => throw new InvalidOperationException("A paid POST must never retry.")));
    private static CapabilityProbe Session(CapabilityHttpProbe service, ProviderConfiguration configuration, CapabilityProbeStore? store = null) =>
        new((id, modelId, capability, token) =>
        {
            Assert.Equal(configuration.Id, id);
            return service.ProbeAsync(configuration, Model(configuration, modelId), Secret, capability, token);
        }, store);
    private static ProviderConfiguration Configuration(ProviderKind kind = ProviderKind.OpenAI, string provider = "openai",
        string baseUrl = "https://provider.test/v1", string? selected = "selected") =>
        new("decb4c6c-d718-4e0e-b59b-f74b048ae60a", provider, kind, baseUrl, selected);
    private static ModelInfo Model(ProviderConfiguration configuration, string modelId = "selected") =>
        new(configuration.ProviderId, modelId, ModelCapabilities.Text);
    private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
        Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(10)) }
    };
    private static string Error(string? code, string? param, string message, string? type = "invalid_request_error") =>
        JsonSerializer.Serialize(new { error = new { code, param, type, message } });

    private static void AssertValidPixel(byte[] bytes)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes.Take(8));
        Assert.Equal(1u, BigEndian(bytes, 16));
        Assert.Equal(1u, BigEndian(bytes, 20));
        for (var offset = 8; offset < bytes.Length;)
        {
            var size = (int)BigEndian(bytes, offset);
            uint crc = uint.MaxValue;
            for (var index = offset + 4; index < offset + 8 + size; index++)
            {
                crc ^= bytes[index];
                for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
            Assert.Equal(BigEndian(bytes, offset + 8 + size), ~crc);
            offset += size + 12;
        }
    }
    private static uint BigEndian(byte[] bytes, int offset) =>
        (uint)bytes[offset] << 24 | (uint)bytes[offset + 1] << 16 | (uint)bytes[offset + 2] << 8 | bytes[offset + 3];

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) { this.respond = respond; }
        public int Requests { get; private set; }
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private abstract class ReadOnlyStream : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class PaddedStream : ReadOnlyStream
    {
        private readonly byte[] prefix;
        private readonly int size;
        public PaddedStream(string prefix, int size) { this.prefix = Encoding.UTF8.GetBytes(prefix); this.size = size; }
        public int BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            count = Math.Min(count, size - BytesRead);
            for (var index = 0; index < count; index++)
                buffer[offset + index] = BytesRead + index < prefix.Length ? prefix[BytesRead + index] : (byte)' ';
            BytesRead += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }
    }

    private sealed class FailingStream : ReadOnlyStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(Secret);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(new IOException(Secret));
    }

    private sealed class BlockingStream : ReadOnlyStream
    {
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult(true);
            return pending.Task;
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); pending.TrySetException(new IOException(Secret)); }
    }
}
