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

public sealed class CatalogProviderTests
{
    private const string Secret = "test-secret-must-not-leak";
    private const string EmptyRegistry = "{\"version\":1,\"models\":[]}";
    private const string OptimisticRegistry = """
        {"version":1,"models":[
          {"providerId":"openrouter","modelIdPattern":".*","vision":true,"tools":true,"reasoning":{"kind":"Effort","efforts":["Off","High"],"mandatory":false},"source":"https://example.test/metadata","confidence":"High","verifiedAt":"2020-01-01"},
          {"providerId":"ollama","modelIdPattern":".*","vision":true,"tools":true,"source":"https://example.test/metadata","confidence":"High","verifiedAt":"2020-01-01"},
          {"providerId":"lmstudio","modelIdPattern":".*","vision":true,"source":"https://example.test/metadata","confidence":"High","verifiedAt":"2020-01-01"}
        ]}
        """;

    [Fact]
    public async Task OpenRouterNativeFalseOverridesRegistryAndNameHintsWithoutInferenceOrCredentials()
    {
        using var handler = new FakeHandler(_ => Json("""
            {"data":[{"id":"text-vision","architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["max_tokens"]},
              {"id":"embedding","architecture":{"input_modalities":["text"],"output_modalities":["embedding"]},"supported_parameters":[]}],
             "total_count":2,"links":{"next":null}}
            """));
        using var client = new HttpClient(handler);
        var provider = new OpenRouterProvider(Transport(client), new CapabilityResolver(OptimisticRegistry));
        var result = await provider.DiscoverAsync(Configuration(ProviderKind.OpenRouter), Secret, CancellationToken.None);
        var model = Assert.Single(result.Models);
        Assert.Null(result.CredentialValid);
        Assert.Null(result.ErrorCode);
        Assert.False(model.Has(ModelCapabilities.Vision));
        Assert.False(model.Has(ModelCapabilities.Tools));
        Assert.False(model.Has(ModelCapabilities.Reasoning));
        Assert.False(model.Has(ModelCapabilities.StructuredOutput));
        Assert.Null(model.Reasoning);
        Assert.Equal(CapabilitySource.Native, model.SourceOf(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.Native, model.SourceOf(ModelCapabilities.Tools));
        Assert.Equal(CapabilitySource.Native, model.SourceOf(ModelCapabilities.Reasoning));
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://openrouter.ai/api/v1/models", sent.Url);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Null(sent.Body);
        Assert.Null(sent.Authorization);
        using var payload = JsonDocument.Parse(provider.BuildPayload(Request(Configuration(ProviderKind.OpenRouter), model, ReasoningIntent.High)));
        Assert.False(payload.RootElement.TryGetProperty("reasoning", out _));
        Assert.Equal("vision_not_supported", Assert.Throws<ProviderException>(() => provider.BuildPayload(
            new ChatRequest(Configuration(ProviderKind.OpenRouter), model, new[] { Message(MessageRole.User, new ImagePart("image/png", "AQ==")) }))).Code);
    }

    [Fact]
    public async Task OpenRouterPreservesExactEffortsMandatoryLimitsAndNativeCapabilities()
    {
        using var handler = new FakeHandler(_ => Json("""
            {"data":[{"id":"native","name":"Native Model","context_length":65536,
              "architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},
              "supported_parameters":["tools","parallel_tool_calls","reasoning","structured_outputs"],
              "reasoning":{"mandatory":true,"supported_efforts":["high","low"]},
              "top_provider":{"max_completion_tokens":8192}}]}
            """));
        using var client = new HttpClient(handler);
        var configuration = Configuration(ProviderKind.OpenRouter);
        var provider = new OpenRouterProvider(Transport(client), Resolver());
        var model = Assert.Single((await provider.DiscoverAsync(configuration, null, CancellationToken.None)).Models);
        Assert.Equal("Native Model", model.DisplayName);
        Assert.Equal(65536, model.ContextWindowTokens);
        Assert.Equal(8192, model.MaxOutputTokens);
        Assert.True(model.Has(ModelCapabilities.Vision | ModelCapabilities.Tools | ModelCapabilities.ParallelTools | ModelCapabilities.Reasoning | ModelCapabilities.StructuredOutput));
        Assert.True(model.Reasoning!.Mandatory);
        Assert.Equal(new[] { ReasoningIntent.High, ReasoningIntent.Low }, model.Reasoning.Efforts);
        Assert.Equal(CapabilitySource.Native, model.SourceOf(ModelCapabilities.Reasoning));
        foreach (var intent in new[] { ReasoningIntent.Off, ReasoningIntent.Medium })
        {
            using var payload = JsonDocument.Parse(provider.BuildPayload(Request(configuration, model, intent)));
            Assert.Equal("low", payload.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        }
    }

    [Fact]
    public async Task OpenRouterOptionalOffAndUnknownEffortDoNotInventWireEnums()
    {
        using var handler = new FakeHandler(_ => Json("""
            {"data":[
              {"id":"optional","supported_parameters":["reasoning"],"reasoning":{"mandatory":false,"supported_efforts":["none","minimal","high"]}},
              {"id":"unknown","supported_parameters":["reasoning"],"reasoning":{"mandatory":true,"supported_efforts":["xhigh"]}},
              {"id":"no-enums","supported_parameters":["reasoning"],"reasoning":{"mandatory":false}}
            ]}
            """));
        using var client = new HttpClient(handler);
        var provider = new OpenRouterProvider(Transport(client), Resolver());
        var configuration = Configuration(ProviderKind.OpenRouter);
        var models = (await provider.DiscoverAsync(configuration, null, CancellationToken.None)).Models;
        Assert.Equal(new[] { ReasoningIntent.Off, ReasoningIntent.Minimal, ReasoningIntent.High }, models[0].Reasoning!.Efforts);
        using var off = JsonDocument.Parse(provider.BuildPayload(Request(configuration, models[0])));
        Assert.Equal("none", off.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        using var unknown = JsonDocument.Parse(provider.BuildPayload(Request(configuration, models[1], ReasoningIntent.Max)));
        Assert.True(unknown.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.False(unknown.RootElement.GetProperty("reasoning").TryGetProperty("effort", out _));
        using var disabled = JsonDocument.Parse(provider.BuildPayload(Request(configuration, models[2])));
        Assert.False(disabled.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task OpenRouterFollowsOnlyDocumentedNextLink()
    {
        using var handler = new FakeHandler(request => request.Url.EndsWith("?offset=1&limit=1", StringComparison.Ordinal)
            ? Json("{\"data\":[{\"id\":\"second\"}],\"links\":{\"next\":null}}")
            : Json("{\"data\":[{\"id\":\"first\"}],\"links\":{\"next\":\"https://openrouter.ai/api/v1/models?offset=1&limit=1\"}}"));
        using var client = new HttpClient(handler);
        var result = await new OpenRouterProvider(Transport(client), Resolver()).DiscoverAsync(Configuration(ProviderKind.OpenRouter), null, CancellationToken.None);
        Assert.Equal(new[] { "first", "second" }, result.Models.Select(model => model.ModelId));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://openrouter.ai/api/v1/models", handler.Requests[0].Url);
    }

    [Theory]
    [InlineData("https://attacker.test/models", "unsafe_url")]
    [InlineData("https://openrouter.ai/api/v1/models", "invalid_response")]
    public async Task OpenRouterRejectsUnsafeOrCyclicNextLinks(string next, string error)
    {
        using var handler = new FakeHandler(_ => Json(JsonSerializer.Serialize(new { data = Array.Empty<object>(), links = new { next } })));
        using var client = new HttpClient(handler);
        var result = await new OpenRouterProvider(Transport(client), Resolver()).DiscoverAsync(Configuration(ProviderKind.OpenRouter), Secret, CancellationToken.None);
        Assert.Equal(error, result.ErrorCode);
        Assert.Empty(result.Models);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LMStudioFiltersEmbeddingsAndNativeLlmFalseBeatsRegistry()
    {
        using var handler = new FakeHandler(request => request.Method == HttpMethod.Get ? Json("""
            {"data":[{"id":"text-vision","type":"llm","max_context_length":131072},
              {"id":"images","type":"vlm","max_context_length":32768},
              {"id":"embed","type":"embeddings","max_context_length":2048}]}
            """) : Sse("data: [DONE]\n\n"));
        using var client = new HttpClient(handler);
        var provider = new LMStudioProvider(Transport(client), new CapabilityResolver(OptimisticRegistry));
        var configuration = Configuration(ProviderKind.LMStudio);
        var models = (await provider.DiscoverAsync(configuration, Secret, CancellationToken.None)).Models;
        Assert.Equal(2, models.Count);
        Assert.False(models[0].Has(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.Native, models[0].SourceOf(ModelCapabilities.Vision));
        Assert.Equal(131072, models[0].ContextWindowTokens);
        Assert.True(models[1].Has(ModelCapabilities.Vision));
        Assert.Equal(32768, models[1].ContextWindowTokens);
        Assert.Equal("http://localhost:1234/api/v0/models", handler.Requests[0].Url);
        await provider.StreamAsync(Request(configuration, models[0]), Secret, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("http://localhost:1234/v1/chat/completions", handler.Requests[1].Url);
        Assert.Equal("Bearer " + Secret, handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task LMStudioFallsBackToV1OnlyOn404()
    {
        using var handler = new FakeHandler(request => request.Url.EndsWith("/api/v0/models", StringComparison.Ordinal)
            ? Json("{}", HttpStatusCode.NotFound) : Json("{\"data\":[{\"id\":\"fallback\"},{\"id\":\"embed\",\"type\":\"embeddings\"}]}"));
        using var client = new HttpClient(handler);
        var result = await new LMStudioProvider(Transport(client), Resolver()).DiscoverAsync(Configuration(ProviderKind.LMStudio), null, CancellationToken.None);
        Assert.Equal("fallback", Assert.Single(result.Models).ModelId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("http://localhost:1234/v1/models", handler.Requests[1].Url);
    }

    [Theory]
    [InlineData(401, "invalid_key")]
    [InlineData(403, "permission_denied")]
    [InlineData(500, "provider_error")]
    [InlineData(200, "invalid_response")]
    public async Task LMStudioDoesNotHideAuthOrMalformedResponseWithFallback(int status, string code)
    {
        using var handler = new FakeHandler(_ => Json("{", (HttpStatusCode)status));
        using var client = new HttpClient(handler);
        var result = await new LMStudioProvider(Transport(client), Resolver()).DiscoverAsync(Configuration(ProviderKind.LMStudio), Secret, CancellationToken.None);
        Assert.Equal(code, result.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OllamaListShowChatAndFiveWholeToolCallsRoundTripOffline()
    {
        var fixture = File.ReadAllText(FixturePath());
        using var handler = new FakeHandler(request =>
        {
            if (request.Url.EndsWith("/api/tags", StringComparison.Ordinal)) return Json("""
                {"models":[{"name":"local-vlm","model":"local-vlm"},{"name":"text-vision"},{"name":"embed"}]}
                """);
            if (request.Url.EndsWith("/api/show", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(request.Body!);
                Assert.False(body.RootElement.GetProperty("verbose").GetBoolean());
                return body.RootElement.GetProperty("model").GetString() switch
                {
                    "local-vlm" => Json("{\"capabilities\":[\"completion\",\"vision\",\"tools\",\"thinking\"],\"thinking\":{\"values\":[false,true]},\"model_info\":{\"llama.context_length\":65536}}"),
                    "text-vision" => Json("{\"capabilities\":[\"completion\"],\"model_info\":{\"qwen.context_length\":4096}}"),
                    "embed" => Json("{\"capabilities\":[\"embedding\"]}"),
                    _ => throw new InvalidOperationException("Unexpected fake model.")
                };
            }
            Assert.EndsWith("/api/chat", request.Url, StringComparison.Ordinal);
            return Ndjson(fixture, fragmentSize: 5);
        });
        using var client = new HttpClient(handler);
        var configuration = Configuration(ProviderKind.Ollama);
        var provider = new OllamaProvider(Transport(client), new CapabilityResolver(OptimisticRegistry));
        var catalog = await provider.DiscoverAsync(configuration, null, CancellationToken.None);
        Assert.Null(catalog.ErrorCode);
        Assert.Equal(2, catalog.Models.Count);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.All(handler.Requests.Skip(1), request => Assert.Equal(HttpMethod.Post, request.Method));
        var model = catalog.Models[0];
        Assert.Equal(65536, model.ContextWindowTokens);
        Assert.True(model.Has(ModelCapabilities.Vision | ModelCapabilities.Tools | ModelCapabilities.Reasoning));
        Assert.False(catalog.Models[1].Has(ModelCapabilities.Vision));
        Assert.False(catalog.Models[1].Has(ModelCapabilities.Tools));
        Assert.False(catalog.Models[1].Has(ModelCapabilities.Reasoning));
        Assert.Equal(CapabilitySource.Native, catalog.Models[1].SourceOf(ModelCapabilities.Vision));
        var tools = new[] { new ToolSpec("read_file", "Read a file", Parse("""
            {"type":"object","properties":{"path":{"type":"string"},"range":{"type":"object","properties":{"start":{"type":"integer"},"end":{"type":"integer"}},"required":["start","end"]}},"required":["path"],"additionalProperties":false}
            """)) };
        var user = Message(MessageRole.User, new TextPart("Inspect"), new ImagePart("image/png", "AQID"));
        var request = new ChatRequest(configuration, model, new[] { user }, ReasoningIntent.High, tools, 1536);
        var events = await Collect(provider, request);
        Assert.DoesNotContain(events, item => item is StreamError);
        Assert.Equal("Reading now.", string.Concat(events.OfType<TextDelta>().Select(item => item.Text)));
        Assert.Equal("Check files.", string.Concat(events.OfType<ReasoningDelta>().Select(item => item.Text)));
        var starts = events.OfType<ToolCallStart>().ToArray();
        var deltas = events.OfType<ToolCallArgsDelta>().ToArray();
        var ends = events.OfType<ToolCallEnd>().ToArray();
        Assert.Equal(5, starts.Length);
        Assert.Equal(5, deltas.Length);
        Assert.Equal(5, ends.Length);
        Assert.Equal(5, starts.Select(item => item.Id).Distinct().Count());
        Assert.Equal(starts.Select(item => item.Id), deltas.Select(item => item.Id));
        Assert.Equal(starts.Select(item => item.Id), ends.Select(item => item.Id));
        Assert.All(ends, item => Assert.Equal(JsonValueKind.Object, item.ParsedArgs.ValueKind));
        Assert.Equal("src/Main.cs", ends[0].ParsedArgs.GetProperty("path").GetString());
        Assert.Equal(5, ends[0].ParsedArgs.GetProperty("range").GetProperty("end").GetInt32());
        Assert.Equal("ação", ends[0].ParsedArgs.GetProperty("tags")[0].GetString());
        Assert.Equal("quote\"", ends[0].ParsedArgs.GetProperty("tags")[1].GetString());
        Assert.Equal("a\\b", ends[0].ParsedArgs.GetProperty("literal").GetString());
        Assert.Equal(ends[0].ParsedArgs.GetRawText(), deltas[0].Chunk);
        var usage = Assert.Single(events.OfType<Usage>());
        Assert.Equal(19, usage.InputTokens);
        Assert.Equal(31, usage.OutputTokens);
        Assert.Null(usage.ReasoningTokens);
        Assert.Equal(FinishReason.ToolUse, Assert.Single(events.OfType<Done>()).FinishReason);
        using var sent = JsonDocument.Parse(handler.Requests[4].Body!);
        Assert.True(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("local-vlm", sent.RootElement.GetProperty("model").GetString());
        Assert.True(sent.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal(1536, sent.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal("AQID", sent.RootElement.GetProperty("messages")[0].GetProperty("images")[0].GetString());
        Assert.Equal(JsonValueKind.String, sent.RootElement.GetProperty("messages")[0].GetProperty("content").ValueKind);
        Assert.Equal(tools[0].ParametersSchema.GetRawText(), sent.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters").GetRawText());
        Assert.False(sent.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(sent.RootElement.TryGetProperty("max_tokens", out _));
        var parts = new List<ContentPart> { new TextPart("Reading now."), new ReasoningPart("Check files.") };
        parts.AddRange(ends.Select((end, index) => new ToolCallPart(end.Id, starts[index].Name, end.ParsedArgs)));
        var history = new List<Message> { user, new Message(MessageRole.Assistant, parts) };
        history.AddRange(ends.Select(end => Message(MessageRole.Tool, new ToolResultPart(end.Id, false, "ok"))));
        await Collect(provider, new ChatRequest(configuration, model, history, ReasoningIntent.Off, tools));
        using var replay = JsonDocument.Parse(handler.Requests[5].Body!);
        var messages = replay.RootElement.GetProperty("messages");
        Assert.Equal("Check files.", messages[1].GetProperty("thinking").GetString());
        Assert.Equal(5, messages[1].GetProperty("tool_calls").GetArrayLength());
        Assert.Equal(JsonValueKind.Object, messages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").ValueKind);
        Assert.False(messages[1].GetProperty("tool_calls")[0].TryGetProperty("id", out _));
        for (var index = 0; index < 5; index++)
        {
            Assert.Equal("tool", messages[index + 2].GetProperty("role").GetString());
            Assert.Equal(starts[index].Name, messages[index + 2].GetProperty("tool_name").GetString());
            Assert.Equal("ok", messages[index + 2].GetProperty("content").GetString());
        }
        Assert.False(replay.RootElement.GetProperty("think").GetBoolean());
    }

    [Theory]
    [InlineData("[false,true]", ReasoningIntent.Off, "false", false)]
    [InlineData("[false,true]", ReasoningIntent.Low, "true", false)]
    [InlineData("[true]", ReasoningIntent.Off, "true", true)]
    [InlineData("[\"low\",\"high\"]", ReasoningIntent.Medium, "\"low\"", true)]
    [InlineData("[\"low\",\"high\"]", ReasoningIntent.Off, "\"low\"", true)]
    [InlineData("[false,\"low\",\"high\"]", ReasoningIntent.Off, "false", false)]
    [InlineData("[\"none\",\"high\"]", ReasoningIntent.Off, "\"none\"", false)]
    public async Task OllamaThinkUsesOnlyValuesAdvertisedBySelectedModel(string values, ReasoningIntent intent, string expected, bool mandatory)
    {
        using var handler = new FakeHandler(request => request.Method == HttpMethod.Get
            ? Json("{\"models\":[{\"name\":\"thinking\"}]}")
            : Json("{\"capabilities\":[\"completion\",\"thinking\"],\"thinking\":{\"values\":" + values + "}}"));
        using var client = new HttpClient(handler);
        var configuration = Configuration(ProviderKind.Ollama);
        var provider = new OllamaProvider(Transport(client), Resolver());
        var model = Assert.Single((await provider.DiscoverAsync(configuration, null, CancellationToken.None)).Models);
        Assert.True(model.Has(ModelCapabilities.Reasoning));
        Assert.Equal(mandatory, model.Reasoning!.Mandatory);
        using var payload = JsonDocument.Parse(provider.BuildPayload(Request(configuration, model, intent)));
        Assert.Equal(expected, payload.RootElement.GetProperty("think").GetRawText());
    }

    [Theory]
    [InlineData("{\"capabilities\":[\"completion\"]}", false)]
    [InlineData("{\"capabilities\":[\"completion\",\"thinking\"],\"thinking\":{\"values\":[false]}}", false)]
    [InlineData("{\"capabilities\":[\"completion\",\"thinking\"]}", true)]
    public async Task OllamaOmitsThinkWhenUnsupportedOrWireValuesUnknown(string metadata, bool supportsReasoning)
    {
        using var handler = new FakeHandler(request => request.Method == HttpMethod.Get ? Json("{\"models\":[{\"name\":\"text-vision\"}]}") : Json(metadata));
        using var client = new HttpClient(handler);
        var configuration = Configuration(ProviderKind.Ollama);
        var provider = new OllamaProvider(Transport(client), Resolver());
        var model = Assert.Single((await provider.DiscoverAsync(configuration, null, CancellationToken.None)).Models);
        Assert.Equal(supportsReasoning, model.Has(ModelCapabilities.Reasoning));
        Assert.False(model.Has(ModelCapabilities.Vision));
        using var payload = JsonDocument.Parse(provider.BuildPayload(Request(configuration, model, ReasoningIntent.High)));
        Assert.False(payload.RootElement.TryGetProperty("think", out _));
    }

    [Theory]
    [InlineData("{\"message\":{\"content\":", "invalid_stream")]
    [InlineData("{\"message\":{\"tool_calls\":[{\"function\":{\"name\":\"read_file\",\"arguments\":\"{}\"}}]},\"done\":false}\n", "invalid_stream")]
    [InlineData("{\"done\":true,\"prompt_eval_count\":-1}\n", "invalid_stream")]
    [InlineData("", "invalid_stream")]
    [InlineData("{\"error\":\"test-secret-must-not-leak\"}\n", "provider_error")]
    public async Task OllamaUnknownAndTruncatedOrInvalidEventsKeepEarlierTextAndFailSafely(string tail, string code)
    {
        using var handler = new FakeHandler(_ => Ndjson("{\"message\":{\"content\":\"retained\"},\"done\":false}\n{\"new_event\":{\"secret\":\"ignored\"}}\n" + tail, 5));
        using var client = new HttpClient(handler);
        var provider = new OllamaProvider(Transport(client), Resolver());
        var configuration = Configuration(ProviderKind.Ollama);
        var events = await Collect(provider, Request(configuration, TextModel(configuration)), Secret);
        Assert.Equal("retained", string.Concat(events.OfType<TextDelta>().Select(item => item.Text)));
        var error = Assert.Single(events.OfType<StreamError>());
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
        Assert.Empty(events.OfType<Done>());
        Assert.Empty(events.OfType<ToolCallEnd>());
    }

    [Fact]
    public async Task OllamaKeepsWireIdsAndMakesLocalIdsUniqueAcrossTurns()
    {
        const string stream = "{\"message\":{\"tool_calls\":[{\"id\":\"wire-id\",\"function\":{\"name\":\"read_file\",\"arguments\":{}}},{\"function\":{\"name\":\"read_file\",\"arguments\":{}}}]},\"done\":true,\"done_reason\":\"tool_calls\"}\n";
        using var handler = new FakeHandler(_ => Ndjson(stream));
        using var client = new HttpClient(handler);
        var provider = new OllamaProvider(Transport(client), Resolver());
        var configuration = Configuration(ProviderKind.Ollama);
        var first = await Collect(provider, Request(configuration, TextModel(configuration)));
        var second = await Collect(provider, Request(configuration, TextModel(configuration)));
        Assert.Equal("wire-id", first.OfType<ToolCallStart>().First().Id);
        Assert.NotEqual(first.OfType<ToolCallStart>().Last().Id, second.OfType<ToolCallStart>().Last().Id);
        Assert.Equal(first.OfType<ToolCallStart>().Select(item => item.Id), first.OfType<ToolCallEnd>().Select(item => item.Id));
    }

    [Fact]
    public async Task OllamaCancelPropagatesInsteadOfEmittingErrorOrSuccess()
    {
        using var handler = new FakeHandler(_ => Ndjson("{\"message\":{\"content\":\"first\"},\"done\":false}\n{\"done\":true}\n", 1));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var configuration = Configuration(ProviderKind.Ollama);
        var events = new List<StreamEvent>();
        var provider = new OllamaProvider(Transport(client), Resolver());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.StreamAsync(Request(configuration, TextModel(configuration)), null, item =>
        { events.Add(item); cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Single(events);
        Assert.IsType<TextDelta>(events[0]);
    }

    [Fact]
    public async Task OllamaRejectsUnsupportedToolsImagesAndOrphanedResultsBeforeHttp()
    {
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No HTTP request is permitted."));
        using var client = new HttpClient(handler);
        var configuration = Configuration(ProviderKind.Ollama);
        var provider = new OllamaProvider(Transport(client), Resolver());
        var model = TextModel(configuration);
        var images = new ChatRequest(configuration, model, new[] { Message(MessageRole.User, new ImagePart("image/png", "AQ==")) });
        Assert.Equal("vision_not_supported", Assert.Throws<ProviderException>(() => provider.BuildPayload(images)).Code);
        var tools = new ChatRequest(configuration, model, new[] { Message(MessageRole.User, new TextPart("read")) }, tools: new[] { new ToolSpec("read_file", "Read", Parse("{}")) });
        Assert.Equal("tools_not_supported", Assert.Throws<ProviderException>(() => provider.BuildPayload(tools)).Code);
        var orphan = new ChatRequest(configuration, model, new[] { Message(MessageRole.Tool, new ToolResultPart("missing", false, "result")) });
        Assert.Equal("tool_history_invalid", Assert.Throws<ProviderException>(() => provider.BuildPayload(orphan)).Code);
        Assert.Empty(handler.Requests);
        var error = Assert.Single((await Collect(provider, images)).OfType<StreamError>());
        Assert.Equal("vision_not_supported", error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AzureOfferingsValidateKeyButNeverPretendToBeDeploymentInventory()
    {
        using var handler = new FakeHandler(_ => Json("{\"data\":[{\"id\":\"gpt-4.1\",\"capabilities\":{\"chat_completion\":true}}]}"));
        using var client = new HttpClient(handler);
        var provider = new AzureProvider(Transport(client), Resolver());
        var result = await provider.DiscoverAsync(Configuration(ProviderKind.Azure), Secret, CancellationToken.None);
        Assert.True(result.CredentialValid);
        Assert.Equal("deployment_required", result.ErrorCode);
        Assert.Empty(result.Models);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://resource.openai.azure.com/openai/v1/models", sent.Url);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(Secret, sent.ApiKey);
        Assert.Null(sent.Authorization);
        Assert.DoesNotContain("deployments", sent.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureReturnsOnlyConfiguredNamesAndUsesDeploymentInPayloadAndV1Path()
    {
        using var handler = new FakeHandler(request => request.Method == HttpMethod.Get
            ? Json("{\"data\":[{\"id\":\"gpt-4.1\",\"capabilities\":{\"chat_completion\":true,\"vision\":true}}]}")
            : Sse("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
        using var client = new HttpClient(handler);
        var configuration = Configuration(ProviderKind.Azure, new[] { "team-chat", "team-chat", "", "other-chat" });
        var provider = new AzureProvider(Transport(client), Resolver());
        var result = await provider.DiscoverAsync(configuration, Secret, CancellationToken.None);
        Assert.Equal(new[] { "team-chat", "other-chat" }, result.Models.Select(model => model.ModelId));
        Assert.Null(result.ErrorCode);
        Assert.False(result.Models[0].Has(ModelCapabilities.Vision));
        var events = await Collect(provider, Request(configuration, result.Models[0]), Secret);
        Assert.Equal("hello", Assert.Single(events.OfType<TextDelta>()).Text);
        Assert.Equal(FinishReason.Stop, Assert.Single(events.OfType<Done>()).FinishReason);
        var sent = handler.Requests[1];
        Assert.Equal("https://resource.openai.azure.com/openai/v1/chat/completions", sent.Url);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(Secret, sent.ApiKey);
        Assert.Null(sent.Authorization);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal("team-chat", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.Equal(1024, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public async Task AzureRejectsModelOfferingsOrUnconfiguredDeploymentBeforeChat()
    {
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No inference permitted."));
        using var client = new HttpClient(handler);
        var provider = new AzureProvider(Transport(client), Resolver());
        var configuration = Configuration(ProviderKind.Azure, new[] { "team-chat" });
        var events = await Collect(provider, Request(configuration, TextModel(configuration, "gpt-4.1")), Secret);
        Assert.Equal("deployment_not_configured", Assert.Single(events.OfType<StreamError>()).Code);
        var missing = Configuration(ProviderKind.Azure);
        Assert.Equal("deployment_required", Assert.Single((await Collect(provider, Request(missing, TextModel(missing)), Secret)).OfType<StreamError>()).Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(ProviderKind.OpenRouter, 401, "invalid_key", null)]
    [InlineData(ProviderKind.Ollama, 401, "invalid_key", false)]
    [InlineData(ProviderKind.Azure, 401, "invalid_key", false)]
    [InlineData(ProviderKind.Azure, 403, "permission_denied", false)]
    [InlineData(ProviderKind.OpenRouter, 429, "rate_limited", null)]
    [InlineData(ProviderKind.Azure, 429, "rate_limited", true)]
    [InlineData(ProviderKind.OpenRouter, 402, "insufficient_credit", null)]
    public async Task CatalogErrorsHaveSafeCodesAndCorrectCredentialValidity(ProviderKind kind, int status, string code, bool? valid)
    {
        using var handler = new FakeHandler(_ => Json("{\"error\":\"" + Secret + "\"}", (HttpStatusCode)status));
        using var client = new HttpClient(handler);
        var provider = Catalog(kind, Transport(client));
        var result = await provider.DiscoverAsync(Configuration(kind), Secret, CancellationToken.None);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(valid, result.CredentialValid);
        Assert.Empty(result.Models);
        Assert.DoesNotContain(Secret, result.ErrorCode!, StringComparison.Ordinal);
        Assert.Equal(status == 429 ? 3 : 1, handler.Requests.Count);
    }

    [Theory]
    [InlineData(ProviderKind.OpenRouter)]
    [InlineData(ProviderKind.Ollama)]
    [InlineData(ProviderKind.Azure)]
    public async Task Catalog404IsUrlErrorNotInvalidCredential(ProviderKind kind)
    {
        using var handler = new FakeHandler(_ => Json(Secret, HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        var result = await Catalog(kind, Transport(client)).DiscoverAsync(Configuration(kind), Secret, CancellationToken.None);
        Assert.Equal("invalid_url", result.ErrorCode);
        Assert.Null(result.CredentialValid);
    }

    [Theory]
    [InlineData(ProviderKind.OpenRouter)]
    [InlineData(ProviderKind.Ollama)]
    [InlineData(ProviderKind.Azure)]
    [InlineData(ProviderKind.LMStudio)]
    public async Task CatalogTruncatedMetadataAndNetworkErrorsAreSafe(ProviderKind kind)
    {
        using var handler = new FakeHandler(_ => Json("{\"secret\":\"" + Secret));
        using var client = new HttpClient(handler);
        var result = await Catalog(kind, Transport(client)).DiscoverAsync(Configuration(kind), Secret, CancellationToken.None);
        Assert.Equal("invalid_response", result.ErrorCode);
        Assert.Empty(result.Models);
        using var failingHandler = new FakeHandler(_ => throw new HttpRequestException(Secret));
        using var failingClient = new HttpClient(failingHandler);
        var failed = await Catalog(kind, Transport(failingClient)).DiscoverAsync(Configuration(kind), Secret, CancellationToken.None);
        Assert.Equal("network_error", failed.ErrorCode);
        Assert.DoesNotContain(Secret, failed.ErrorCode!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProviderKind.OpenRouter)]
    [InlineData(ProviderKind.Ollama)]
    [InlineData(ProviderKind.Azure)]
    [InlineData(ProviderKind.LMStudio)]
    public async Task ChatHttpErrorsDoNotEchoSecretsOrRetryPaidRequests(ProviderKind kind)
    {
        using var handler = new FakeHandler(_ => Json("{\"error\":\"" + Secret + "\"}", HttpStatusCode.TooManyRequests));
        using var client = new HttpClient(handler);
        var configuration = Configuration(kind, kind == ProviderKind.Azure ? new[] { "deployment" } : null);
        var provider = (IChatProvider)Catalog(kind, Transport(client));
        var events = await Collect(provider, Request(configuration, TextModel(configuration, "deployment")), Secret);
        var error = Assert.Single(events.OfType<StreamError>());
        Assert.Equal("rate_limited", error.Code);
        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    private static CapabilityResolver Resolver() => new CapabilityResolver(EmptyRegistry);
    private static HttpTransport Transport(HttpClient client) => new HttpTransport(client, (_, _) => Task.CompletedTask);
    private static IModelCatalog Catalog(ProviderKind kind, HttpTransport transport) => kind switch
    {
        ProviderKind.OpenRouter => new OpenRouterProvider(transport, Resolver()),
        ProviderKind.Ollama => new OllamaProvider(transport, Resolver()),
        ProviderKind.Azure => new AzureProvider(transport, Resolver()),
        ProviderKind.LMStudio => new LMStudioProvider(transport, Resolver()),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static ProviderConfiguration Configuration(ProviderKind kind, IReadOnlyList<string>? deployments = null) => new ProviderConfiguration(
        "de600af3-1463-4403-96a0-0d313ec0a267", kind.ToString().ToLowerInvariant(), kind, kind switch
        {
            ProviderKind.OpenRouter => "https://openrouter.ai/api/v1/",
            ProviderKind.Azure => "https://resource.openai.azure.com/",
            ProviderKind.Ollama => "http://localhost:11434/",
            ProviderKind.LMStudio => "http://localhost:1234/",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        }, deploymentNames: deployments);
    private static ModelInfo TextModel(ProviderConfiguration configuration, string id = "text") => new ModelInfo(configuration.ProviderId, id, ModelCapabilities.Text | ModelCapabilities.Streaming);
    private static Message Message(MessageRole role, params ContentPart[] parts) => new Message(role, parts);
    private static ChatRequest Request(ProviderConfiguration configuration, ModelInfo model, ReasoningIntent intent = ReasoningIntent.Off) => new ChatRequest(configuration, model, new[] { Message(MessageRole.User, new TextPart("hello")) }, intent);
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string body) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private static HttpResponseMessage Ndjson(string body, int fragmentSize = 5) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(body), fragmentSize)) };
    private static async Task<List<StreamEvent>> Collect(IChatProvider provider, ChatRequest request, string? credential = null)
    {
        var events = new List<StreamEvent>();
        await provider.StreamAsync(request, credential, item => { events.Add(item); return Task.CompletedTask; }, CancellationToken.None);
        return events;
    }
    private static string FixturePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Lumen.Tests", "Fixtures", "ollama.ndjson");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("The offline Ollama fixture is missing.");
    }

    private sealed record Captured(HttpMethod Method, string Url, string? Body, string? Authorization, string? ApiKey);
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<Captured, HttpResponseMessage> respond;
        public FakeHandler(Func<Captured, HttpResponseMessage> respond) { this.respond = respond; }
        public List<Captured> Requests { get; } = new List<Captured>();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var captured = new Captured(request.Method, request.RequestUri!.AbsoluteUri, body,
                request.Headers.TryGetValues("Authorization", out var authorization) ? authorization.Single() : null,
                request.Headers.TryGetValues("api-key", out var apiKey) ? apiKey.Single() : null);
            Requests.Add(captured);
            return respond(captured);
        }
    }
    private sealed class FragmentedStream : MemoryStream
    {
        private readonly int fragmentSize;
        public FragmentedStream(byte[] bytes, int fragmentSize) : base(bytes) { this.fragmentSize = fragmentSize; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => base.ReadAsync(buffer, offset, Math.Min(count, fragmentSize), cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer.Slice(0, Math.Min(buffer.Length, fragmentSize)), cancellationToken);
    }
}
