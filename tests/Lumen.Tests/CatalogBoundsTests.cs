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
using Lumen.Providers;
using Xunit;

namespace Lumen.Tests;

public sealed class CatalogBoundsTests
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxEntries = 10_000;
    private const string Secret = "offline-body-must-not-leak";
    private const string EmptyRegistry = "{\"version\":1,\"models\":[]}";

    public static IEnumerable<object[]> AllProviders => Enum.GetValues<ProviderKind>().Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(AllProviders))]
    public async Task CatalogRejectsTooManyEntriesBeforeResolvingModelsOrRequestingDetails(ProviderKind kind)
    {
        using var handler = new Handler(_ => Json(CatalogBody(kind, MaxEntries + 1, poisonFirst: true)));
        using var client = new HttpClient(handler);
        var result = await Catalog(kind, client).DiscoverAsync(Configuration(kind), null, CancellationToken.None);
        AssertSafeFailure(result, "response_too_large");
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public async Task CatalogCountsDuplicatesAndFilteredEntriesAgainstLimit(ProviderKind kind)
    {
        using var handler = new Handler(_ => Json(CatalogBody(kind, MaxEntries + 1, filtered: true)));
        using var client = new HttpClient(handler);
        var result = await Catalog(kind, client).DiscoverAsync(Configuration(kind), null, CancellationToken.None);
        AssertSafeFailure(result, "response_too_large");
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public async Task CatalogAcceptsExactlyTenThousandEntries(ProviderKind kind)
    {
        using var handler = new Handler(request => Json(request.Method == HttpMethod.Get
            ? CatalogBody(kind, MaxEntries) : "{\"capabilities\":[\"completion\"]}"));
        using var client = new HttpClient(handler);
        var result = await Catalog(kind, client).DiscoverAsync(Configuration(kind), null, CancellationToken.None);
        Assert.Null(result.ErrorCode);
        Assert.Equal(kind is ProviderKind.Azure or ProviderKind.Ollama ? 1 : MaxEntries, result.Models.Count);
        Assert.Equal(kind == ProviderKind.Ollama ? 2 : 1, handler.Requests);
    }

    [Theory]
    [InlineData(ProviderKind.OpenRouter, MaxEntries)]
    [InlineData(ProviderKind.Anthropic, MaxEntries)]
    [InlineData(ProviderKind.Gemini, MaxEntries)]
    [InlineData(ProviderKind.OpenRouter, MaxEntries + 1)]
    [InlineData(ProviderKind.Anthropic, MaxEntries + 1)]
    [InlineData(ProviderKind.Gemini, MaxEntries + 1)]
    public async Task PaginationLimitIsTotalEntriesNotPerPageOrReturnedModels(ProviderKind kind, int entries)
    {
        var served = 0;
        using var handler = new Handler(_ =>
        {
            var count = Math.Min(1000, entries - served);
            served += count;
            return Json(CatalogBody(kind, count, served < entries ? served.ToString() : null,
                poisonFirst: served > MaxEntries));
        });
        using var client = new HttpClient(handler);
        var result = await Catalog(kind, client).DiscoverAsync(Configuration(kind), null, CancellationToken.None);
        Assert.Equal((entries + 999) / 1000, handler.Requests);
        if (entries > MaxEntries) AssertSafeFailure(result, "response_too_large");
        else
        {
            Assert.Null(result.ErrorCode);
            Assert.Equal(MaxEntries, result.Models.Count);
        }
    }

    [Theory]
    [InlineData(ProviderKind.OpenRouter)]
    [InlineData(ProviderKind.Gemini)]
    public async Task FilteredPagesStillConsumeTotalEntryBudget(ProviderKind kind)
    {
        var page = 0;
        using var handler = new Handler(_ => Json(CatalogBody(kind, ++page == 1 ? 6000 : 4001,
            page == 1 ? "second" : null, filtered: true)));
        using var client = new HttpClient(handler);
        var result = await Catalog(kind, client).DiscoverAsync(Configuration(kind), null, CancellationToken.None);
        AssertSafeFailure(result, "response_too_large");
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData(MaxEntries)]
    [InlineData(MaxEntries + 1)]
    public async Task LMStudioFallbackEnforcesSameEntryLimit(int entries)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/api/v0/models"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json(CatalogBody(ProviderKind.LMStudio, entries, poisonFirst: entries > MaxEntries)));
        using var client = new HttpClient(handler);
        var result = await Catalog(ProviderKind.LMStudio, client).DiscoverAsync(Configuration(ProviderKind.LMStudio), null, CancellationToken.None);
        Assert.Equal(2, handler.Requests);
        if (entries > MaxEntries) AssertSafeFailure(result, "response_too_large");
        else
        {
            Assert.Null(result.ErrorCode);
            Assert.Equal(MaxEntries, result.Models.Count);
        }
    }

    [Theory]
    [InlineData(MaxEntries)]
    [InlineData(MaxEntries + 1)]
    public async Task AzureChecksConfiguredCountBeforeDeploymentResolution(int entries)
    {
        using var handler = new Handler(_ => Json("{\"data\":[]}"));
        using var client = new HttpClient(handler);
        var configuration = Configuration(ProviderKind.Azure, Enumerable.Range(0, entries).Select(index => "deployment-" + index).ToArray());
        var result = await Catalog(ProviderKind.Azure, client).DiscoverAsync(configuration, null, CancellationToken.None);
        if (entries > MaxEntries) AssertSafeFailure(result, "response_too_large");
        else
        {
            Assert.Null(result.ErrorCode);
            Assert.Equal(MaxEntries, result.Models.Count);
        }
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public async Task ActualByteLimitRejectsOversizedResponseWithoutContentLength(ProviderKind kind)
    {
        using var stream = new PaddedStream(CatalogBody(kind, 1), MaxBytes + 100_000);
        using var handler = new Handler(_ => StreamResponse(stream));
        using var client = new HttpClient(handler);
        var result = await Catalog(kind, client).DiscoverAsync(Configuration(kind), null, CancellationToken.None);
        AssertSafeFailure(result, "response_too_large");
        Assert.Equal(MaxBytes + 1, stream.BytesRead);
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MissingChunkedOrLyingContentLengthCannotBypassActualByteLimit(int framing)
    {
        using var stream = new PaddedStream(CatalogBody(ProviderKind.OpenRouter, 1), MaxBytes + 1);
        using var handler = new Handler(_ => StreamResponse(stream, framing));
        using var client = new HttpClient(handler);
        var result = await Catalog(ProviderKind.OpenRouter, client).DiscoverAsync(Configuration(ProviderKind.OpenRouter), null, CancellationToken.None);
        AssertSafeFailure(result, "response_too_large");
        Assert.Equal(MaxBytes + 1, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExactlySixteenMiBRemainsValidIncludingChunkedAndLyingLength(int framing)
    {
        using var stream = new PaddedStream(CatalogBody(ProviderKind.OpenRouter, 1), MaxBytes);
        using var handler = new Handler(_ => StreamResponse(stream, framing));
        using var client = new HttpClient(handler);
        var result = await Catalog(ProviderKind.OpenRouter, client).DiscoverAsync(Configuration(ProviderKind.OpenRouter), null, CancellationToken.None);
        Assert.Null(result.ErrorCode);
        Assert.Single(result.Models);
        Assert.Equal(MaxBytes, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task DeclaredOversizeRejectsBeforeReadingBody()
    {
        using var stream = new PaddedStream("{}", MaxBytes + 1);
        using var handler = new Handler(_ =>
        {
            var response = StreamResponse(stream);
            response.Content.Headers.ContentLength = MaxBytes + 1;
            return response;
        });
        using var client = new HttpClient(handler);
        var result = await Catalog(ProviderKind.OpenAI, client).DiscoverAsync(Configuration(ProviderKind.OpenAI), null, CancellationToken.None);
        AssertSafeFailure(result, "response_too_large");
        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ByteLimitCountsUtf8BytesNotCharacters()
    {
        var body = "{\"data\":[],\"description\":\"" + new string('é', MaxBytes / 2) + "\"}";
        Assert.True(body.Length < MaxBytes);
        using var stream = new PaddedStream(body, Encoding.UTF8.GetByteCount(body));
        using var handler = new Handler(_ => StreamResponse(stream));
        using var client = new HttpClient(handler);
        var result = await Catalog(ProviderKind.OpenRouter, client).DiscoverAsync(Configuration(ProviderKind.OpenRouter), null, CancellationToken.None);
        AssertSafeFailure(result, "response_too_large");
        Assert.Equal(MaxBytes + 1, stream.BytesRead);
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public async Task TruncatedJsonAndBodyReadFailuresReturnFixedErrorsWithoutBodyLeakage(ProviderKind kind)
    {
        using var malformedHandler = new Handler(_ => Json("{\"marker\":\"" + Secret));
        using var malformedClient = new HttpClient(malformedHandler);
        AssertSafeFailure(await Catalog(kind, malformedClient).DiscoverAsync(Configuration(kind), null, CancellationToken.None), "invalid_response");
        using var stream = new FailingStream();
        using var handler = new Handler(_ => StreamResponse(stream));
        using var client = new HttpClient(handler);
        AssertSafeFailure(await Catalog(kind, client).DiscoverAsync(Configuration(kind), null, CancellationToken.None), "invalid_response");
        Assert.True(stream.Disposed);
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public async Task CancellationInterruptsPendingReadEvenWhenStreamIgnoresToken(ProviderKind kind)
    {
        using var stream = new BlockingStream();
        using var handler = new Handler(_ => StreamResponse(stream));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var operation = Catalog(kind, client).DiscoverAsync(Configuration(kind), null, cancellation.Token);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Requests);
    }

    private static void AssertSafeFailure(CatalogResult result, string code)
    {
        Assert.Equal(code, result.ErrorCode);
        Assert.Empty(result.Models);
        Assert.Null(result.CredentialValid);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    private static IModelCatalog Catalog(ProviderKind kind, HttpClient client)
    {
        var transport = new HttpTransport(client);
        var resolver = new CapabilityResolver(EmptyRegistry);
        return kind switch
        {
            ProviderKind.OpenAI => new OpenAIProvider(transport, resolver),
            ProviderKind.OpenRouter => new OpenRouterProvider(transport, resolver),
            ProviderKind.Anthropic => new AnthropicProvider(transport, resolver),
            ProviderKind.Gemini => new GeminiProvider(transport, resolver),
            ProviderKind.Ollama => new OllamaProvider(transport, resolver),
            ProviderKind.LMStudio => new LMStudioProvider(transport, resolver),
            ProviderKind.Azure => new AzureProvider(transport, resolver),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static ProviderConfiguration Configuration(ProviderKind kind, IReadOnlyList<string>? deployments = null) => new ProviderConfiguration(
        "cde7f3bd-59e7-4e11-975a-31305b1c31f5", kind.ToString().ToLowerInvariant(), kind, "https://provider.test/v1/",
        deploymentNames: kind == ProviderKind.Azure ? deployments ?? new[] { "deployment" } : null);

    private static string CatalogBody(ProviderKind kind, int count, string? next = null, bool poisonFirst = false, bool filtered = false)
    {
        var id = poisonFirst ? new string('x', 1025) : "duplicate";
        var item = kind switch
        {
            ProviderKind.Gemini => new { name = "models/" + id, supportedGenerationMethods = new[] { filtered ? "embedContent" : "generateContent" } } as object,
            ProviderKind.Ollama => new { name = id },
            ProviderKind.LMStudio => new { id, type = filtered ? "embeddings" : "llm" },
            ProviderKind.OpenRouter when filtered => new { id, architecture = new { output_modalities = new[] { "embedding" } } },
            ProviderKind.OpenAI when filtered => new { ignored = true },
            _ => new { id }
        };
        var normal = kind == ProviderKind.Gemini
            ? JsonSerializer.Serialize(new { name = "models/duplicate", supportedGenerationMethods = new[] { filtered ? "embedContent" : "generateContent" } })
            : JsonSerializer.Serialize(item);
        var serialized = JsonSerializer.Serialize(item);
        var array = "[" + string.Join(",", Enumerable.Range(0, count).Select(index => index == 0 ? serialized : normal)) + "]";
        var field = kind is ProviderKind.Gemini or ProviderKind.Ollama ? "models" : "data";
        var suffix = kind switch
        {
            ProviderKind.OpenRouter => ",\"links\":{\"next\":" + JsonSerializer.Serialize(next is null ? null : "?page=" + next) + "}",
            ProviderKind.Anthropic => ",\"has_more\":" + (next is null ? "false" : "true") + ",\"last_id\":" + JsonSerializer.Serialize(next),
            ProviderKind.Gemini => ",\"nextPageToken\":" + JsonSerializer.Serialize(next),
            _ => ""
        };
        return "{\"" + field + "\":" + array + ",\"marker\":\"" + Secret + "\"" + suffix + "}";
    }

    private static HttpResponseMessage Json(string body) => new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage StreamResponse(Stream stream, int framing = 0)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        if (framing == 1) response.Headers.TransferEncodingChunked = true;
        if (framing == 2) response.Content.Headers.ContentLength = 1;
        else Assert.Null(response.Content.Headers.ContentLength);
        return response;
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) { this.respond = respond; }
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(request.Method == HttpMethod.Get || request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/show");
            Requests++;
            return Task.FromResult(respond(request));
        }
    }

    private abstract class NonSeekableStream : Stream
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

    private sealed class PaddedStream : NonSeekableStream
    {
        private readonly byte[] prefix;
        private readonly int size;
        public PaddedStream(string prefix, int size) { this.prefix = Encoding.UTF8.GetBytes(prefix); this.size = size; }
        public int BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            count = Math.Min(Math.Min(count, 7777), size - BytesRead);
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

    private sealed class FailingStream : NonSeekableStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(Secret);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(new IOException(Secret));
    }

    private sealed class BlockingStream : NonSeekableStream
    {
        public TaskCompletionSource<bool> ReadStarted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> pendingRead = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult(true);
            return pendingRead.Task;
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            pendingRead.TrySetException(new IOException(Secret));
        }
    }
}
