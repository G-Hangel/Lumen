using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Xunit;

namespace Lumen.Tests;

public sealed class ContextSessionTests
{
    private const string Secret = "synthetic-context-key-41a98b";
    private const string Model = "text-only-fixture";

    [Theory]
    [InlineData("file", null)]
    [InlineData("file", "example.cs")]
    [InlineData("solution", null)]
    [InlineData("selection", null)]
    [InlineData("output", null)]
    public async Task CapturePreviewsNativeSnapshotWithoutConfigurationCredentialsHttpOrPersistence(string kind, string? path)
    {
        using var fixture = new Fixture();
        fixture.Credentials.RejectReads = true;
        fixture.Source.Label = "native label Ω";
        fixture.Source.Text = "native buffer\n<script>not executed</script>";
        var before = Disk(fixture.Root);
        var context = await fixture.Capture(kind, path);
        Assert.Equal(new[] { "id", "kind", "label", "text" }, context.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.True(Guid.TryParseExact(context.GetProperty("id").GetString(), "D", out var id));
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(kind, context.GetProperty("kind").GetString());
        Assert.Equal(fixture.Source.Label, context.GetProperty("label").GetString());
        Assert.Equal(fixture.Source.Text, context.GetProperty("text").GetString());
        Assert.Equal((kind, path), Assert.Single(fixture.Source.Captures));
        Assert.Empty(fixture.Source.Validated);
        Assert.Equal(0, fixture.Credentials.Reads);
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.Equal(0, fixture.Handler.Probes);
        Assert.Equal(before, Disk(fixture.Root));
        Assert.DoesNotContain(fixture.Root, context.GetRawText());
    }

    [Fact]
    public async Task FilePathAt1024CharactersIsForwardedWithoutBrowserSupplyingContent()
    {
        using var fixture = new Fixture();
        var path = new string('p', 1024);
        await fixture.Capture("file", path);
        Assert.Equal(("file", path), Assert.Single(fixture.Source.Captures));
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.Equal(0, fixture.Credentials.Reads);
    }

    [Fact]
    public async Task MissingContextSourceReturnsControlledErrorWithoutCredentialOrHttpAccess()
    {
        using var fixture = new Fixture(withSource: false);
        AssertError(await fixture.Send("byok.context.capture", new { kind = "selection" }), "context_unavailable");
        Assert.Empty(fixture.Source.Captures);
        Assert.Equal(0, fixture.Credentials.Reads);
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task NativeSnapshotsBecomeOneSecondTextPartInRequestedOrderAndSuccessConsumesIds()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        fixture.Source.Label = "first.cs";
        fixture.Source.Text = "first native buffer";
        var first = await fixture.CaptureId();
        fixture.Source.Label = "selection Ω";
        fixture.Source.Text = "second native buffer\nline 2";
        var second = await fixture.CaptureId("selection");
        fixture.Source.Text = "changed after preview; must not be recaptured";
        const string prompt = "  preserve my prompt\nexactly  ";
        const string contextText = "[Context: selection Ω]\nsecond native buffer\nline 2\n\n[Context: first.cs]\nfirst native buffer";
        var response = await fixture.Chat(prompt, new[] { second.ToUpperInvariant(), first });
        AssertCompleted(response);
        Assert.Equal(2, fixture.Source.Captures.Count);
        Assert.Equal(new[] { "second native buffer\nline 2", "first native buffer" }, fixture.Source.Validated.Select(snapshot => snapshot.Text));
        using (var body = JsonDocument.Parse(Assert.Single(fixture.Handler.ChatBodies)))
            AssertWireTexts(Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "user"), prompt, contextText);
        var id = Payload(response).GetProperty("conversation").GetProperty("id").GetString()!;
        var stored = await new ConversationStore(fixture.HistoryPath).ReadAsync(id, CancellationToken.None);
        Assert.NotNull(stored);
        var parts = stored!.Conversation.Messages[0].Parts;
        Assert.Equal(2, parts.Count);
        Assert.Equal(prompt, Assert.IsType<TextPart>(parts[0]).Text);
        Assert.Equal(contextText, Assert.IsType<TextPart>(parts[1]).Text);
        var opened = await fixture.Open(id);
        var turn = Assert.Single(Payload(opened).GetProperty("turns").EnumerateArray());
        Assert.Equal(prompt, turn.GetProperty("userText").GetString());
        Assert.Equal(contextText, turn.GetProperty("contextText").GetString());
        var reads = fixture.Credentials.Reads;
        var requests = fixture.Handler.Requests;
        AssertError(await fixture.Chat("cannot reuse", new[] { first }), "context_stale");
        AssertError(await fixture.Chat("cannot reuse second", new[] { second }), "context_stale");
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Equal(0, fixture.Handler.Probes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoContextChatHasOnlyPromptAndNoContextHistoryProjection(bool explicitEmptyIds)
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var response = await fixture.Chat("plain prompt", explicitEmptyIds ? Array.Empty<string>() : null);
        AssertCompleted(response);
        using var body = JsonDocument.Parse(Assert.Single(fixture.Handler.ChatBodies));
        AssertWireTexts(Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "user"), "plain prompt");
        var id = Payload(response).GetProperty("conversation").GetProperty("id").GetString()!;
        var turn = Assert.Single(Payload(await fixture.Open(id)).GetProperty("turns").EnumerateArray());
        Assert.Equal("plain prompt", turn.GetProperty("userText").GetString());
        Assert.False(turn.TryGetProperty("contextText", out _));
        Assert.Empty(fixture.Source.Validated);
    }

    [Fact]
    public async Task ReleaseIsCredentialFreeIdempotentAndDoesNotInvalidateOtherSnapshots()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var released = await fixture.CaptureId();
        var retained = await fixture.CaptureId("output");
        var reads = fixture.Credentials.Reads;
        var requests = fixture.Handler.Requests;
        var before = Disk(fixture.Root);
        foreach (var ids in new[] { new[] { released.ToUpperInvariant() }, new[] { released }, new[] { Guid.NewGuid().ToString("D") }, Array.Empty<string>() })
        {
            var result = await fixture.Send("byok.context.release", new { ids });
            Assert.Equal("byok.context.release.result", Type(result));
            Assert.Equal(new[] { "released" }, Payload(result).EnumerateObject().Select(property => property.Name));
            Assert.True(Payload(result).GetProperty("released").GetBoolean());
        }
        AssertError(await fixture.Chat("released", new[] { released }), "context_stale");
        AssertError(await fixture.Chat("unknown", new[] { Guid.NewGuid().ToString("D") }), "context_stale");
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Equal(before, Disk(fixture.Root));
        AssertCompleted(await fixture.Chat("retained", new[] { retained }));
        Assert.Single(fixture.Source.Validated);
    }

    [Fact]
    public async Task WorkspaceChangeFailsValidationBeforeCredentialsHttpAndLeavesSnapshotAvailable()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var id = await fixture.CaptureId();
        fixture.Source.CurrentRoot = Path.Combine(fixture.Root, "different-solution");
        fixture.Credentials.RejectReads = true;
        var reads = fixture.Credentials.Reads;
        var requests = fixture.Handler.Requests;
        var before = Disk(fixture.Root);
        AssertError(await fixture.Chat("must not spend", new[] { id }), "context_stale");
        Assert.Single(fixture.Source.Validated);
        Assert.Equal(1, fixture.Source.StaleRejections);
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Equal(before, Disk(fixture.Root));
        fixture.Source.CurrentRoot = fixture.Root;
        fixture.Credentials.RejectReads = false;
        AssertCompleted(await fixture.Chat("retry valid workspace", new[] { id }));
        Assert.Equal(2, fixture.Source.Validated.Count);
        Assert.Single(fixture.Source.Captures);
    }

    [Fact]
    public async Task EightSnapshotLimitRejectsNinthAndReleaseRestoresCapacityWithoutApiOrProbe()
    {
        using var fixture = new Fixture();
        var ids = new List<string>();
        for (var index = 0; index < 8; index++) ids.Add(await fixture.CaptureId());
        AssertError(await fixture.Send("byok.context.capture", new { kind = "file" }), "context_too_large");
        Assert.Equal(8, fixture.Source.Captures.Count);
        Assert.Equal("byok.context.release.result", Type(await fixture.Send("byok.context.release", new { ids = new[] { ids[0] } })));
        var replacement = await fixture.CaptureId();
        Assert.Equal(9, fixture.Source.Captures.Count);
        AssertError(await fixture.Send("byok.context.capture", new { kind = "output" }), "context_too_large");
        var release = await fixture.Send("byok.context.release", new { ids = ids.Skip(1).Concat(new[] { replacement }).ToArray() });
        Assert.Equal("byok.context.release.result", Type(release));
        Assert.True(Payload(release).GetProperty("released").GetBoolean());
        await fixture.CaptureId();
        Assert.Equal(0, fixture.Credentials.Reads);
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.Equal(0, fixture.Handler.Probes);
        Assert.Empty(Disk(fixture.Root));
    }

    [Fact]
    public async Task IndividualAndAggregateTextLimitsRejectOverflowAndReleaseRestoresTextBudget()
    {
        using var fixture = new Fixture();
        fixture.Source.Text = new string('x', 65537);
        AssertError(await fixture.Send("byok.context.capture", new { kind = "file" }), "context_too_large");
        fixture.Source.Text = new string('x', 65536);
        var first = await fixture.CaptureId();
        var second = await fixture.CaptureId();
        fixture.Source.Text = "x";
        AssertError(await fixture.Send("byok.context.capture", new { kind = "file" }), "context_too_large");
        Assert.Equal("byok.context.release.result", Type(await fixture.Send("byok.context.release", new { ids = new[] { first } })));
        fixture.Source.Text = new string('y', 65536);
        var replacement = await fixture.CaptureId();
        Assert.NotEqual(first, replacement);
        Assert.NotEqual(second, replacement);
        fixture.Source.Text = "y";
        AssertError(await fixture.Send("byok.context.capture", new { kind = "selection" }), "context_too_large");
        Assert.Equal(0, fixture.Credentials.Reads);
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.Equal(0, fixture.Handler.Probes);
        Assert.Empty(Disk(fixture.Root));
    }

    [Fact]
    public async Task EightUniqueContextIdsAreAcceptedAndJoinedWithoutChangingPrompt()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var ids = new List<string>();
        var texts = new List<string>();
        for (var index = 0; index < 8; index++)
        {
            fixture.Source.Label = "label " + index;
            fixture.Source.Text = "text " + index;
            texts.Add("[Context: " + fixture.Source.Label + "]\n" + fixture.Source.Text);
            ids.Add(await fixture.CaptureId());
        }
        AssertCompleted(await fixture.Chat("eight contexts", ids.ToArray()));
        using var body = JsonDocument.Parse(Assert.Single(fixture.Handler.ChatBodies));
        AssertWireTexts(Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "user"), "eight contexts", string.Join("\n\n", texts));
        Assert.Equal(8, fixture.Source.Validated.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CredentialInPromptOrNativeContextIsRejectedBeforeHttpAndSnapshotIsRetained(bool secretInContext)
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        fixture.Source.Text = secretInContext ? "native contains " + Secret : "safe native buffer";
        var id = await fixture.CaptureId();
        var requests = fixture.Handler.Requests;
        var before = Disk(fixture.Root);
        AssertError(await fixture.Chat(secretInContext ? "safe prompt" : "prompt contains " + Secret, new[] { id }), "invalid_response");
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Empty(fixture.Handler.ChatBodies);
        Assert.Single(fixture.Source.Validated);
        Assert.Equal(before, Disk(fixture.Root));
        AssertSecretAbsent(fixture.Replies.Where(reply => !reply.Contains("byok.context.capture.result", StringComparison.Ordinal)));
        await fixture.Credentials.WriteAsync(fixture.ConfigurationId, "replacement-synthetic-key", CancellationToken.None);
        AssertCompleted(await fixture.Chat("retry retained snapshot", new[] { id }));
        Assert.Single(fixture.Source.Captures);
    }

    [Fact]
    public async Task ProviderCredentialEchoStillCannotPersistAndErrorRetainsContextForRetry()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var id = await fixture.CaptureId();
        var before = Disk(fixture.Root);
        fixture.Handler.Stream = Frame("provider echoes " + Secret);
        AssertError(await fixture.Chat("echo attempt", new[] { id }), "invalid_response");
        Assert.Equal(before, Disk(fixture.Root));
        AssertSecretAbsent(Directory.GetFiles(fixture.Root, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));
        fixture.Handler.Stream = Frame("safe answer");
        AssertCompleted(await fixture.Chat("retry after echo", new[] { id }));
        Assert.Equal(2, fixture.Handler.ChatBodies.Count);
        using var body = JsonDocument.Parse(fixture.Handler.ChatBodies.Last());
        AssertWireTexts(Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "user"), "retry after echo", "[Context: native.cs]\nnative text");
        Assert.DoesNotContain("echo attempt", fixture.Handler.ChatBodies.Last());
        Assert.Single(fixture.Source.Captures);
    }

    [Fact]
    public async Task CancellationRetainsIdsAndRetryDoesNotRecaptureOrReplayCancelledTurn()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var id = await fixture.CaptureId();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.BeforeChat = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        var running = fixture.Chat("cancelled prompt", new[] { id });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelled = await fixture.Send("byok.cancel", new { });
        Assert.Equal("byok.cancel.result", Type(cancelled));
        Assert.True(Payload(cancelled).GetProperty("cancelled").GetBoolean());
        AssertError(await running.WaitAsync(TimeSpan.FromSeconds(5)), "cancelled");
        fixture.Handler.BeforeChat = null;
        AssertCompleted(await fixture.Chat("retry after cancel", new[] { id }));
        Assert.Equal(2, fixture.Handler.ChatBodies.Count);
        Assert.Equal(2, fixture.Source.Validated.Count);
        Assert.Single(fixture.Source.Captures);
        using var body = JsonDocument.Parse(fixture.Handler.ChatBodies.Last());
        AssertWireTexts(Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "user"), "retry after cancel", "[Context: native.cs]\nnative text");
    }

    [Fact]
    public async Task CaptureSharesBusyGateAndCancellationDoesNotReserveSnapshotCapacity()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.BeforeCapture = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        var capture = fixture.Send("byok.context.capture", new { kind = "selection" });
        var first = await Task.WhenAny(entered.Task, capture).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first == entered.Task, capture.IsCompletedSuccessfully ? (await capture).GetRawText() : "Capture failed before entering the source.");
        await entered.Task;
        AssertError(await fixture.Send("byok.context.capture", new { kind = "file" }), "bridge_busy");
        AssertError(await fixture.Send("byok.context.release", new { ids = Array.Empty<string>() }), "bridge_busy");
        Assert.Equal("byok.cancel.result", Type(await fixture.Send("byok.cancel", new { })));
        AssertError(await capture.WaitAsync(TimeSpan.FromSeconds(5)), "cancelled");
        fixture.Source.BeforeCapture = null;
        for (var index = 0; index < 8; index++) await fixture.CaptureId();
        AssertError(await fixture.Send("byok.context.capture", new { kind = "file" }), "context_too_large");
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.Equal(0, fixture.Credentials.Reads);
        Assert.Empty(Disk(fixture.Root));
    }

    [Fact]
    public async Task ContextPersistenceRoundTripReplaysAfterRestartWithoutRecaptureOrValidation()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        fixture.Source.Text = "archived native Ω\nline 2";
        var captured = await fixture.CaptureId();
        var result = await fixture.Chat("original prompt", new[] { captured });
        AssertCompleted(result);
        var conversationId = Payload(result).GetProperty("conversation").GetProperty("id").GetString()!;
        var orphan = await fixture.CaptureId("output");
        fixture.Restart();
        fixture.Source.RejectCapture = true;
        fixture.Source.CurrentRoot = Path.Combine(fixture.Root, "changed-workspace");
        var reads = fixture.Credentials.Reads;
        var requests = fixture.Handler.Requests;
        var before = Disk(fixture.Root);
        var opened = await fixture.Open(conversationId);
        Assert.Equal("byok.history.open.result", Type(opened));
        var turn = Assert.Single(Payload(opened).GetProperty("turns").EnumerateArray());
        const string contextText = "[Context: native.cs]\narchived native Ω\nline 2";
        Assert.Equal("original prompt", turn.GetProperty("userText").GetString());
        Assert.Equal(contextText, turn.GetProperty("contextText").GetString());
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Equal(before, Disk(fixture.Root));
        await fixture.Send("byok.discover", new { configurationId = fixture.ConfigurationId });
        await fixture.Select();
        reads = fixture.Credentials.Reads;
        requests = fixture.Handler.Requests;
        AssertError(await fixture.Chat("restart orphan", new[] { orphan }), "context_stale");
        AssertError(await fixture.Chat("restart consumed", new[] { captured }), "context_stale");
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(requests, fixture.Handler.Requests);
        AssertCompleted(await fixture.Chat("explicit continuation"));
        using var replay = JsonDocument.Parse(fixture.Handler.ChatBodies.Last());
        var messages = replay.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        AssertWireTexts(messages[1], "original prompt", contextText);
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        AssertWireTexts(messages[3], "explicit continuation");
        Assert.Equal(2, fixture.Source.Captures.Count);
        Assert.Single(fixture.Source.Validated);
        AssertSecretAbsent(fixture.Replies.Concat(Directory.GetFiles(fixture.Root, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task MalformedSchemasAndDuplicateGuidAliasesHaveNoSourceCredentialHttpOrDiskSideEffects(string type, string rawPayload)
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var retained = await fixture.CaptureId();
        var captures = fixture.Source.Captures.Count;
        var reads = fixture.Credentials.Reads;
        var requests = fixture.Handler.Requests;
        var before = Disk(fixture.Root);
        using var payload = JsonDocument.Parse(rawPayload.Replace("CONFIG", fixture.ConfigurationId, StringComparison.Ordinal).Replace("CONTEXT", retained, StringComparison.Ordinal).Replace("UPPER", retained.ToUpperInvariant(), StringComparison.Ordinal));
        AssertError(await fixture.Send(type, payload.RootElement), "invalid_message");
        Assert.Equal(captures, fixture.Source.Captures.Count);
        Assert.Empty(fixture.Source.Validated);
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Equal(0, fixture.Handler.Probes);
        Assert.Equal(before, Disk(fixture.Root));
        AssertCompleted(await fixture.Chat("still retained", new[] { retained }));
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        foreach (var payload in new[] { "{}", "[]", "null", "{\"kind\":null}", "{\"kind\":7}", "{\"kind\":\"File\"}", "{\"kind\":\"future\"}", "{\"kind\":\"\"}", "{\"kind\":\"file\",\"kind\":\"file\"}", "{\"kind\":\"file\",\"text\":\"forged\"}", "{\"kind\":\"file\",\"label\":\"forged\"}", "{\"kind\":\"file\",\"extra\":true}", "{\"kind\":\"file\",\"path\":null}", "{\"kind\":\"file\",\"path\":7}", JsonSerializer.Serialize(new { kind = "file", path = new string('p', 1025) }) })
            yield return new object[] { "byok.context.capture", payload };
        foreach (var kind in new[] { "solution", "selection", "output" })
            foreach (var path in new[] { "\"file.cs\"", "\"\"", "null" })
                yield return new object[] { "byok.context.capture", "{\"kind\":\"" + kind + "\",\"path\":" + path + "}" };
        foreach (var ids in new[] { "null", "true", "7", "\"CONTEXT\"", "{}", "[null]", "[7]", "[\"not-guid\"]", "[\"00000000-0000-0000-0000-000000000000\"]", "[\"CONTEXT\",\"CONTEXT\"]", "[\"CONTEXT\",\"UPPER\"]", "[\"{CONTEXT}\"]", JsonSerializer.Serialize(Enumerable.Range(0, 9).Select(_ => Guid.NewGuid().ToString("D")).ToArray()) })
        {
            yield return new object[] { "byok.context.release", "{\"ids\":" + ids + "}" };
            yield return new object[] { "byok.chat", "{\"configurationId\":\"CONFIG\",\"text\":\"bad\",\"effort\":\"Off\",\"contextIds\":" + ids + "}" };
        }
        yield return new object[] { "byok.context.release", "{}" };
        yield return new object[] { "byok.context.release", "{\"ids\":[\"CONTEXT\"],\"extra\":true}" };
        yield return new object[] { "byok.context.release", "{\"ids\":[\"CONTEXT\"],\"ids\":[]}" };
        foreach (var extra in new[] { "\"contextText\":\"forged native text\"", "\"extra\":true", "\"contextIds\":[]" })
            yield return new object[] { "byok.chat", "{\"configurationId\":\"CONFIG\",\"text\":\"bad\",\"effort\":\"Off\",\"contextIds\":[\"CONTEXT\"]," + extra + "}" };
    }

    [Fact]
    public async Task CorrelationIdReplayAndCasingAliasCannotCaptureTwiceOrResendChat()
    {
        using var fixture = new Fixture();
        const string requestId = "9aa56cda-b369-41dc-a641-43cc7090eac9";
        var first = await fixture.Send("byok.context.capture", new { kind = "file" }, requestId);
        Assert.Equal("byok.context.capture.result", Type(first));
        await fixture.SendIgnoredReplay("byok.context.capture", new { kind = "file" }, requestId.ToUpperInvariant());
        Assert.Single(fixture.Source.Captures);
        Assert.Equal(0, fixture.Handler.Requests);
        await fixture.ConfigureAndSelect();
        var contextId = Payload(first).GetProperty("context").GetProperty("id").GetString()!;
        var chatId = Guid.NewGuid().ToString("D");
        AssertCompleted(await fixture.Chat("explicit once", new[] { contextId }, chatId));
        var reads = fixture.Credentials.Reads;
        var requests = fixture.Handler.Requests;
        await fixture.SendIgnoredReplay("byok.chat", new { configurationId = fixture.ConfigurationId, text = "explicit once", effort = "Off", contextIds = new[] { contextId } }, chatId.ToUpperInvariant());
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Single(fixture.Handler.ChatBodies);
    }

    private static string Type(JsonElement reply) => reply.GetProperty("type").GetString()!;
    private static JsonElement Payload(JsonElement reply) => reply.GetProperty("payload");
    private static void AssertError(JsonElement reply, string? code = null)
    {
        Assert.Equal("error", Type(reply));
        var actual = Payload(reply).GetProperty("code").GetString();
        Assert.False(string.IsNullOrWhiteSpace(actual));
        if (code is not null) Assert.Equal(code, actual);
    }
    private static void AssertCompleted(JsonElement reply)
    {
        Assert.Equal("byok.chat.result", Type(reply));
        Assert.True(Payload(reply).GetProperty("completed").GetBoolean());
    }
    private static void AssertWireTexts(JsonElement message, params string[] texts)
    {
        Assert.Equal("user", message.GetProperty("role").GetString());
        var content = message.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(texts.Length, content.GetArrayLength());
        for (var index = 0; index < texts.Length; index++)
        {
            Assert.Equal("text", content[index].GetProperty("type").GetString());
            Assert.Equal(texts[index], content[index].GetProperty("text").GetString());
        }
    }
    private static string[] Disk(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => path + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    private static void AssertSecretAbsent(IEnumerable<string> values)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Assert.DoesNotContain(Secret, "negative control " + Secret));
        foreach (var value in values) Assert.DoesNotContain(Secret, value);
    }
    private static string Frame(string text) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text }, finish_reason = "stop" } } }) + "\n\ndata: [DONE]\n\n";

    private sealed class ContextSource : IContextSource
    {
        public string CurrentRoot;
        public string Label = "native.cs";
        public string Text = "native text";
        public bool RejectCapture;
        public Func<CancellationToken, Task>? BeforeCapture;
        public int StaleRejections;
        public readonly List<(string Kind, string? Path)> Captures = new();
        public readonly List<ContextSnapshot> Validated = new();
        public ContextSource(string root) { CurrentRoot = root; }
        public async Task<ContextSnapshot> CaptureAsync(string kind, string? path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(RejectCapture, "Replay must not recapture archived context.");
            Captures.Add((kind, path));
            if (BeforeCapture is not null) await BeforeCapture(cancellationToken);
            return new ContextSnapshot(CurrentRoot, kind, Label, Text);
        }
        public Task ValidateAsync(ContextSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validated.Add(snapshot);
            if (!string.Equals(snapshot.WorkspaceRoot, CurrentRoot, StringComparison.OrdinalIgnoreCase))
            {
                StaleRejections++;
                throw new ProviderException("context_stale");
            }
            return Task.CompletedTask;
        }
    }
    private sealed class Credentials : ICredentialStore
    {
        private readonly Dictionary<string, string> values = new();
        public int Reads;
        public bool RejectReads;
        public Task<string?> ReadAsync(string id, CancellationToken cancellationToken)
        {
            Reads++;
            Assert.False(RejectReads, "Context preview or stale validation must precede credential reads.");
            return Task.FromResult(values.TryGetValue(id, out var value) ? value : null);
        }
        public Task WriteAsync(string id, string secret, CancellationToken cancellationToken) { values[id] = secret; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken cancellationToken) { values.Remove(id); return Task.CompletedTask; }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests;
        public int Probes;
        public string Stream = Frame("safe answer");
        public Func<CancellationToken, Task>? BeforeChat;
        public readonly List<string> ChatBodies = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("provider.test", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"" + Model + "\"}]}") };
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var parsed = JsonDocument.Parse(body);
            if (parsed.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.False)
            {
                Probes++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}") };
            }
            ChatBodies.Add(body);
            if (BeforeChat is not null) await BeforeChat(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Stream) };
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Lumen.ContextSessionTests." + Guid.NewGuid().ToString("N"));
        public string HistoryPath => Path.Combine(Root, "conversations");
        public string ConfigurationId { get; } = Guid.NewGuid().ToString("D");
        public readonly Credentials Credentials = new();
        public readonly Handler Handler = new();
        public readonly ContextSource Source;
        private readonly HttpClient client;
        private readonly ProviderFactory providers;
        private readonly CapabilityHttpProbe probe;
        private readonly bool withSource;
        private readonly List<string> replies = new();
        private ByokSession session;
        public string[] Replies { get { lock (replies) return replies.ToArray(); } }
        public Fixture(bool withSource = true)
        {
            this.withSource = withSource;
            Directory.CreateDirectory(Root);
            Source = new ContextSource(Root);
            client = new HttpClient(Handler);
            var transport = new HttpTransport(client, (_, _) => Task.CompletedTask);
            providers = new ProviderFactory(transport, new CapabilityResolver());
            probe = new CapabilityHttpProbe(transport);
            session = CreateSession();
        }
        private ByokSession CreateSession() => new(providers, Credentials, new SettingsStore(Path.Combine(Root, "providers.json")), probe, withSource ? Source : null);
        public void Restart() { session.Dispose(); session = CreateSession(); }
        public async Task<JsonElement> Send(string type, object payload, string? requestId = null)
        {
            var id = requestId ?? Guid.NewGuid().ToString("D");
            var before = Replies.Length;
            var json = JsonSerializer.Serialize(new { version = 1, id, type, payload });
            Assert.True(await session.HandleAsync("https://lumen.local/index.html", json, SendReply, CancellationToken.None));
            JsonElement? result = null;
            foreach (var response in Replies.Skip(before))
            {
                using var parsed = JsonDocument.Parse(response);
                var reply = parsed.RootElement;
                if (string.Equals(reply.GetProperty("id").GetString(), id, StringComparison.OrdinalIgnoreCase) && Type(reply) != "byok.event") result = reply.Clone();
            }
            Assert.True(result.HasValue, "Native RPC must return a correlated result or controlled error.");
            return result.Value;
        }
        public async Task SendIgnoredReplay(string type, object payload, string requestId)
        {
            var before = Replies;
            var json = JsonSerializer.Serialize(new { version = 1, id = requestId, type, payload });
            Assert.True(await session.HandleAsync("https://lumen.local/index.html", json, SendReply, CancellationToken.None));
            Assert.Equal(before, Replies);
        }
        private Task SendReply(string json) { lock (replies) replies.Add(json); return Task.CompletedTask; }
        public async Task ConfigureAndSelect()
        {
            Assert.Equal("byok.configure.result", Type(await Send("byok.configure", new { id = ConfigurationId, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = Secret })));
            await Select();
        }
        public async Task Select() => Assert.Equal("byok.select.result", Type(await Send("byok.select", new { configurationId = ConfigurationId, modelId = Model })));
        public async Task<JsonElement> Capture(string kind = "file", string? path = null)
        {
            var result = await Send("byok.context.capture", path is null ? (object)new { kind } : new { kind, path });
            Assert.Equal("byok.context.capture.result", Type(result));
            return Payload(result).GetProperty("context");
        }
        public async Task<string> CaptureId(string kind = "file") => (await Capture(kind)).GetProperty("id").GetString()!;
        public Task<JsonElement> Chat(string text, string[]? contextIds = null, string? requestId = null) => Send("byok.chat", contextIds is null
            ? (object)new { configurationId = ConfigurationId, text, effort = "Off" }
            : new { configurationId = ConfigurationId, text, effort = "Off", contextIds }, requestId);
        public Task<JsonElement> Open(string conversationId) => Send("byok.history.open", new { conversationId, offset = 0, limit = 10 });
        public void Dispose() { session.Dispose(); client.Dispose(); Directory.Delete(Root, true); }
    }
}
