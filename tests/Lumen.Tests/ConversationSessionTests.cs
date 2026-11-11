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

public sealed class ConversationSessionTests
{
    private const string Secret = "synthetic-history-key-7bd48c";
    private const string Model = "text-only-fixture";
    private const string Image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
    private const string ReasoningSignature = "history-reasoning-signature";
    private const string ToolSignature = "history-tool-signature";

    [Fact]
    public async Task DisposedSessionRoundTripReplaysOnlyAfterExplicitChatAndNewKeepsArchive()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var first = await fixture.Chat("previous completed question");
        var summary = ChatSummary(first, fixture.ConfigurationId, Model, 1);
        var conversationId = summary.GetProperty("id").GetString()!;
        Assert.True(Directory.Exists(fixture.HistoryPath));
        Assert.Equal(2, ReadDomain(fixture, conversationId).Messages.Count);
        fixture.Restart();

        var reads = fixture.Credentials.Reads;
        var requests = fixture.Handler.Requests;
        var disk = Snapshot(fixture.Root);
        var opened = await fixture.Open(conversationId);
        Assert.Equal("byok.history.open.result", Type(opened));
        Assert.Equal(conversationId, Payload(opened).GetProperty("conversation").GetProperty("id").GetString());
        var turn = Assert.Single(Payload(opened).GetProperty("turns").EnumerateArray());
        Assert.Equal("previous completed question", turn.GetProperty("userText").GetString());
        Assert.Equal("completed answer", turn.GetProperty("text").GetString());
        Assert.Equal("completed", turn.GetProperty("status").GetString());
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Equal(disk, Snapshot(fixture.Root));

        await fixture.Configure();
        Assert.Equal("byok.list.result", Type(await fixture.Send("byok.list", new { })));
        Assert.Equal("byok.discover.result", Type(await fixture.Send("byok.discover", new { configurationId = fixture.ConfigurationId })));
        await fixture.Select();
        Assert.Single(fixture.Handler.ChatBodies);
        var second = await fixture.Chat("next explicit question");
        ChatSummary(second, fixture.ConfigurationId, Model, 2);
        using (var replay = JsonDocument.Parse(fixture.Handler.ChatBodies.Last()))
        {
            var messages = replay.RootElement.GetProperty("messages");
            Assert.Equal(3, messages.GetArrayLength());
            AssertWireText(messages[0], "user", "previous completed question");
            AssertWireText(messages[1], "assistant", "completed answer");
            AssertWireText(messages[2], "user", "next explicit question");
        }

        disk = Snapshot(fixture.Root);
        var created = await fixture.Send("byok.history.new", new { });
        Assert.Equal("byok.history.new.result", Type(created));
        Assert.True(Payload(created).GetProperty("created").GetBoolean());
        Assert.Equal(disk, Snapshot(fixture.Root));
        var third = await fixture.Chat("new conversation question");
        var newId = ChatSummary(third, fixture.ConfigurationId, Model, 1).GetProperty("id").GetString();
        Assert.NotEqual(conversationId, newId);
        using (var fresh = JsonDocument.Parse(fixture.Handler.ChatBodies.Last()))
            AssertWireText(Assert.Single(fresh.RootElement.GetProperty("messages").EnumerateArray()), "user", "new conversation question");
        var archives = Payload(await fixture.List()).GetProperty("conversations");
        Assert.Equal(2, archives.GetArrayLength());
        Assert.Contains(archives.EnumerateArray(), item => item.GetProperty("id").GetString() == conversationId && item.GetProperty("turnCount").GetInt32() == 2);
        AssertSecretAbsent(ReadJsonFiles(fixture.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameSelectionRetainsConversationButDifferentModelOrConfigurationClearsIt(bool changeConfiguration)
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var originalId = ChatSummary(await fixture.Chat("original question"), fixture.ConfigurationId, Model, 1).GetProperty("id").GetString()!;
        await fixture.Select();
        Assert.Equal(originalId, ChatSummary(await fixture.Chat("same selection question"), fixture.ConfigurationId, Model, 2).GetProperty("id").GetString());
        using (var replay = JsonDocument.Parse(fixture.Handler.ChatBodies.Last()))
            Assert.Equal(3, replay.RootElement.GetProperty("messages").GetArrayLength());

        if (changeConfiguration)
        {
            var otherId = Guid.NewGuid().ToString("D");
            await fixture.Configure(otherId);
            await fixture.Select(otherId);
            await fixture.Select();
        }
        else
        {
            await fixture.Select(modelId: "second-text-fixture");
            await fixture.Select();
        }
        var disk = Snapshot(fixture.Root);
        var archives = Payload(await fixture.List()).GetProperty("conversations");
        Assert.Equal(originalId, Assert.Single(archives.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(disk, Snapshot(fixture.Root));
        var freshId = ChatSummary(await fixture.Chat("after different selection"), fixture.ConfigurationId, Model, 1).GetProperty("id").GetString();
        Assert.NotEqual(originalId, freshId);
        using var fresh = JsonDocument.Parse(fixture.Handler.ChatBodies.Last());
        AssertWireText(Assert.Single(fresh.RootElement.GetProperty("messages").EnumerateArray()), "user", "after different selection");
        Assert.Equal(2, Payload(await fixture.List()).GetProperty("conversations").GetArrayLength());
    }

    [Fact]
    public async Task ListOpenRenameAndDeleteWorkReadOnlyWithProviderMissingAndWithoutCredentialsOrHttp()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var original = ChatSummary(await fixture.Chat("archived question"), fixture.ConfigurationId, Model, 1);
        var conversationId = original.GetProperty("id").GetString()!;
        await fixture.Send("byok.remove", new { configurationId = fixture.ConfigurationId });
        fixture.Restart();
        fixture.Credentials.RejectReads = true;
        var reads = fixture.Credentials.Reads;
        var writes = fixture.Credentials.Writes;
        var deletes = fixture.Credentials.Deletes;
        var requests = fixture.Handler.Requests;
        var disk = Snapshot(fixture.Root);

        var listed = await fixture.List();
        Assert.Equal("byok.history.list.result", Type(listed));
        Assert.Equal(conversationId, Assert.Single(Payload(listed).GetProperty("conversations").EnumerateArray()).GetProperty("id").GetString());
        var opened = await fixture.Open(conversationId);
        Assert.Equal("byok.history.open.result", Type(opened));
        Assert.Equal("archived question", Payload(opened).GetProperty("turns")[0].GetProperty("userText").GetString());
        Assert.Equal(disk, Snapshot(fixture.Root));

        var title = new string('R', 160);
        var renamed = await fixture.Send("byok.history.rename", new { conversationId, title });
        Assert.Equal("byok.history.rename.result", Type(renamed));
        var summary = Payload(renamed).GetProperty("conversation");
        AssertSummary(summary, fixture.ConfigurationId, Model, 1);
        Assert.Equal(title, summary.GetProperty("title").GetString());
        Assert.Equal(original.GetProperty("id").GetString(), summary.GetProperty("id").GetString());
        Assert.Equal(title, ReadDomain(fixture, conversationId).Title);
        Assert.Single(Payload(await fixture.List(title)).GetProperty("conversations").EnumerateArray());
        Assert.Empty(Payload(await fixture.List("no-such-history-title")).GetProperty("conversations").EnumerateArray());

        var deleted = await fixture.Send("byok.history.delete", new { conversationId, confirm = true });
        Assert.Equal("byok.history.delete.result", Type(deleted));
        Assert.True(Payload(deleted).GetProperty("deleted").GetBoolean());
        Assert.Empty(Payload(await fixture.List()).GetProperty("conversations").EnumerateArray());
        AssertError(await fixture.Open(conversationId));
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(writes, fixture.Credentials.Writes);
        Assert.Equal(deletes, fixture.Credentials.Deletes);
        Assert.Equal(requests, fixture.Handler.Requests);
        AssertUiPrivate(fixture.Replies);
    }

    [Fact]
    public async Task DeletingActiveConversationClearsReplayButPreservesOtherArchives()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var retainedId = ChatSummary(await fixture.Chat("retained archive"), fixture.ConfigurationId, Model, 1).GetProperty("id").GetString();
        await fixture.Send("byok.history.new", new { });
        var deletedId = ChatSummary(await fixture.Chat("delete this active turn"), fixture.ConfigurationId, Model, 1).GetProperty("id").GetString();
        var requests = fixture.Handler.Requests;
        var reads = fixture.Credentials.Reads;
        Assert.Equal("byok.history.delete.result", Type(await fixture.Send("byok.history.delete", new { conversationId = deletedId, confirm = true })));
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(retainedId, Assert.Single(Payload(await fixture.List()).GetProperty("conversations").EnumerateArray()).GetProperty("id").GetString());
        var nextId = ChatSummary(await fixture.Chat("after deletion"), fixture.ConfigurationId, Model, 1).GetProperty("id").GetString();
        Assert.NotEqual(deletedId, nextId);
        using var body = JsonDocument.Parse(fixture.Handler.ChatBodies.Last());
        AssertWireText(Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray()), "user", "after deletion");
    }

    [Fact]
    public async Task HistoryPagesBoundedTurnsWithStableIndicesAndAnEmptyEndPage()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        string? conversationId = null;
        for (var index = 0; index < 12; index++)
            conversationId = ChatSummary(await fixture.Chat("question " + index), fixture.ConfigurationId, Model, index + 1).GetProperty("id").GetString();
        var disk = Snapshot(fixture.Root);
        var requests = fixture.Handler.Requests;
        var reads = fixture.Credentials.Reads;
        foreach (var (offset, limit, expected) in new[] { (0, 10, 10), (10, 10, 2), (3, 1, 1), (12, 10, 0) })
        {
            var opened = await fixture.Open(conversationId!, offset, limit);
            Assert.Equal("byok.history.open.result", Type(opened));
            var payload = Payload(opened);
            Assert.Equal(offset, payload.GetProperty("offset").GetInt32());
            Assert.Equal(12, payload.GetProperty("total").GetInt32());
            var turns = payload.GetProperty("turns");
            Assert.Equal(expected, turns.GetArrayLength());
            for (var index = 0; index < expected; index++)
            {
                var turn = turns[index];
                Assert.Equal(offset + index, turn.GetProperty("index").GetInt32());
                Assert.Equal("question " + (offset + index), turn.GetProperty("userText").GetString());
                Assert.False(turn.GetProperty("hasImage").GetBoolean());
                Assert.Equal("completed answer", turn.GetProperty("text").GetString());
                Assert.Equal("", turn.GetProperty("reasoning").GetString());
                Assert.Empty(turn.GetProperty("tools").EnumerateArray());
                Assert.Equal("completed", turn.GetProperty("status").GetString());
            }
        }
        Assert.Equal(disk, Snapshot(fixture.Root));
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Equal(reads, fixture.Credentials.Reads);
    }

    [Fact]
    public async Task SignedReasoningToolAndImageSurviveDiskAndRestartWithoutLeakingIntoUiViews()
    {
        using var fixture = new Fixture("Gemini");
        fixture.Handler.Stream = GeminiFrame(new object[]
        {
            new { text = "private reasoning display", thought = true, thoughtSignature = ReasoningSignature },
            new { text = "signed answer" },
            new { functionCall = new { id = "native-history-call", name = "read_file", args = new { path = "history.cs", line = 7 } }, thoughtSignature = ToolSignature }
        }, true);
        await fixture.ConfigureAndSelect();
        Assert.Equal("byok.override.result", Type(await fixture.Send("byok.override", new { configurationId = fixture.ConfigurationId, modelId = Model, vision = true, tools = true, reasoning = false })));
        var first = await fixture.Send("byok.chat", new { configurationId = fixture.ConfigurationId, text = "question with image", effort = "Off", image = new { mediaType = "image/png", base64Data = Image } });
        var conversationId = ChatSummary(first, fixture.ConfigurationId, Model, 1).GetProperty("id").GetString()!;
        var persisted = ReadDomain(fixture, conversationId);
        var image = Assert.Single(persisted.Messages[0].Parts.OfType<ImagePart>());
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(Image, image.Base64Data);
        var assistant = persisted.Messages[1];
        var reasoning = Assert.Single(assistant.Parts.OfType<ReasoningPart>());
        Assert.Equal("private reasoning display", reasoning.Text);
        using (var state = JsonDocument.Parse(reasoning.ProviderState!))
            Assert.Equal(ReasoningSignature, state.RootElement.GetProperty("thoughtSignature").GetString());
        var tool = Assert.Single(assistant.Parts.OfType<ToolCallPart>());
        Assert.Equal("native-history-call", tool.Id);
        Assert.Equal("read_file", tool.Name);
        Assert.Equal("history.cs", tool.Arguments.GetProperty("path").GetString());
        Assert.Equal(7, tool.Arguments.GetProperty("line").GetInt32());
        using (var state = JsonDocument.Parse(tool.ProviderState!))
            Assert.Equal(ToolSignature, state.RootElement.GetProperty("thoughtSignature").GetString());
        fixture.Restart();

        var requests = fixture.Handler.Requests;
        var reads = fixture.Credentials.Reads;
        var disk = Snapshot(fixture.Root);
        await fixture.List();
        var opened = await fixture.Open(conversationId);
        Assert.Equal("byok.history.open.result", Type(opened));
        var turn = Assert.Single(Payload(opened).GetProperty("turns").EnumerateArray());
        Assert.True(turn.GetProperty("hasImage").GetBoolean());
        Assert.Equal("signed answer", turn.GetProperty("text").GetString());
        Assert.Equal(reasoning.Text, turn.GetProperty("reasoning").GetString());
        var toolView = Assert.Single(turn.GetProperty("tools").EnumerateArray());
        Assert.Equal(tool.Id, toolView.GetProperty("id").GetString());
        Assert.Equal(tool.Name, toolView.GetProperty("name").GetString());
        Assert.Equal(tool.Arguments.GetRawText(), toolView.GetProperty("arguments").GetRawText());
        Assert.Equal(disk, Snapshot(fixture.Root));
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Equal(reads, fixture.Credentials.Reads);
        AssertUiPrivate(fixture.Replies);

        await fixture.Send("byok.discover", new { configurationId = fixture.ConfigurationId });
        await fixture.Select();
        Assert.Equal("byok.override.result", Type(await fixture.Send("byok.override", new { configurationId = fixture.ConfigurationId, modelId = Model, vision = true, tools = true, reasoning = false })));
        fixture.Handler.Stream = GeminiFrame(new object[] { new { text = "next answer" } }, true);
        ChatSummary(await fixture.Chat("next signed question"), fixture.ConfigurationId, Model, 2);
        using var replay = JsonDocument.Parse(fixture.Handler.ChatBodies.Last());
        var contents = replay.RootElement.GetProperty("contents");
        // Legacy tool cards have no result: retain the archive but skip the entire
        // pair on replay rather than sending an orphan native functionCall.
        Assert.Single(contents.EnumerateArray());
        Assert.DoesNotContain(ReasoningSignature, fixture.Handler.ChatBodies.Last());
        Assert.DoesNotContain(ToolSignature, fixture.Handler.ChatBodies.Last());
        Assert.DoesNotContain(Image, fixture.Handler.ChatBodies.Last());
        AssertUiPrivate(fixture.Replies);
        AssertSecretAbsent(ReadJsonFiles(fixture.Root));
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("Gemini")]
    public async Task NativeCancelPersistsPartialTurnAndRestartReplayExcludesCancelledPair(string kind)
    {
        using var fixture = new Fixture(kind);
        await fixture.ConfigureAndSelect();
        var completedId = ChatSummary(await fixture.Chat("completed before cancellation"), fixture.ConfigurationId, Model, 1).GetProperty("id").GetString()!;
        var partialSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnReply = reply =>
        {
            if (Type(reply) == "byok.event" && Payload(reply).TryGetProperty("text", out var text) && text.GetString() == "cancelled partial answer")
                partialSeen.TrySetResult();
        };
        fixture.Handler.BlockingStream = new BlockingStream(PartialFrame(kind, "cancelled partial answer"));
        var running = fixture.Chat("cancelled user question");
        await partialSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Handler.BlockingStream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = await fixture.Send("byok.cancel", new { });
        Assert.Equal("byok.cancel.result", Type(cancel));
        Assert.True(Payload(cancel).GetProperty("cancelled").GetBoolean());
        AssertError(await running.WaitAsync(TimeSpan.FromSeconds(1)), "cancelled");
        Assert.True(fixture.Handler.BlockingStream.Disposed);
        fixture.Handler.BlockingStream = null;
        fixture.OnReply = null;
        fixture.Restart();

        var requests = fixture.Handler.Requests;
        var reads = fixture.Credentials.Reads;
        var opened = await fixture.Open(completedId);
        Assert.Equal("byok.history.open.result", Type(opened));
        var turns = Payload(opened).GetProperty("turns");
        Assert.Equal(2, turns.GetArrayLength());
        Assert.Equal("completed", turns[0].GetProperty("status").GetString());
        Assert.Equal("cancelled", turns[1].GetProperty("status").GetString());
        Assert.Equal("cancelled user question", turns[1].GetProperty("userText").GetString());
        Assert.Equal("cancelled partial answer", turns[1].GetProperty("text").GetString());
        var stored = ReadDomain(fixture, completedId);
        Assert.Equal(4, stored.Messages.Count);
        Assert.Equal("cancelled partial answer", Assert.Single(stored.Messages[3].Parts.OfType<TextPart>()).Text);
        Assert.Contains(ReadJsonFiles(fixture.HistoryPath), json => json.Contains("\"cancelled\"", StringComparison.Ordinal));
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Equal(reads, fixture.Credentials.Reads);

        await fixture.Send("byok.discover", new { configurationId = fixture.ConfigurationId });
        await fixture.Select();
        ChatSummary(await fixture.Chat("explicit after cancellation"), fixture.ConfigurationId, Model, 3);
        var body = fixture.Handler.ChatBodies.Last();
        Assert.DoesNotContain("cancelled partial answer", body);
        Assert.DoesNotContain("cancelled user question", body);
        Assert.Contains("completed before cancellation", body);
        Assert.Contains("completed answer", body);
        using var replay = JsonDocument.Parse(body);
        Assert.Equal(3, replay.RootElement.GetProperty(kind == "Gemini" ? "contents" : "messages").GetArrayLength());
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Anthropic")]
    [InlineData("Gemini")]
    public async Task PrematureEofSavesFailedPartialPairButNeverReplaysIt(string kind)
    {
        using var fixture = new Fixture(kind);
        await fixture.ConfigureAndSelect();
        fixture.Handler.Stream = PartialFrame(kind, "failed partial answer");
        var failed = await fixture.Chat("failed user question");
        Assert.Equal("byok.chat.result", Type(failed));
        Assert.False(Payload(failed).GetProperty("completed").GetBoolean());
        var conversationId = Payload(failed).GetProperty("conversation").GetProperty("id").GetString()!;
        fixture.Restart();
        var opened = await fixture.Open(conversationId);
        Assert.Equal("byok.history.open.result", Type(opened));
        var turn = Assert.Single(Payload(opened).GetProperty("turns").EnumerateArray());
        Assert.Equal("failed", turn.GetProperty("status").GetString());
        Assert.Equal("failed user question", turn.GetProperty("userText").GetString());
        Assert.Equal("failed partial answer", turn.GetProperty("text").GetString());
        Assert.Equal(2, ReadDomain(fixture, conversationId).Messages.Count);
        Assert.Contains(ReadJsonFiles(fixture.HistoryPath), json => json.Contains("\"failed\"", StringComparison.Ordinal));
        await fixture.Send("byok.discover", new { configurationId = fixture.ConfigurationId });
        await fixture.Select();
        fixture.Handler.Stream = CompletedFrame(kind);
        ChatSummary(await fixture.Chat("retry explicit question"), fixture.ConfigurationId, Model, 2);
        var body = fixture.Handler.ChatBodies.Last();
        Assert.DoesNotContain("failed partial answer", body);
        Assert.DoesNotContain("failed user question", body);
        using var parsed = JsonDocument.Parse(body);
        Assert.Single(parsed.RootElement.GetProperty(kind == "Gemini" ? "contents" : "messages").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderSecretEchoCannotEnterPersistedJsonOrFutureReplay(bool existingArchive)
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        if (existingArchive) await fixture.Chat("safe previous question");
        var disk = Snapshot(fixture.Root);
        fixture.Handler.Stream = PartialFrame("OpenAI", "provider echoes " + Secret) + "data: [DONE]\n\n";
        AssertError(await fixture.Chat("secret echo question"), "invalid_response");
        Assert.Equal(disk, Snapshot(fixture.Root));
        AssertSecretAbsent(ReadJsonFiles(fixture.Root));
        fixture.Handler.Stream = CompletedFrame("OpenAI");
        ChatSummary(await fixture.Chat("safe next question"), fixture.ConfigurationId, Model, existingArchive ? 2 : 1);
        Assert.DoesNotContain("secret echo question", fixture.Handler.ChatBodies.Last());
        Assert.DoesNotContain(Secret, fixture.Handler.ChatBodies.Last());
        AssertSecretAbsent(ReadJsonFiles(fixture.Root));
    }

    [Fact]
    public async Task EscapedCredentialEchoIsRejectedBeforePersistence()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        const string escapedSecret = "history-secret-\"\\-test";
        await fixture.Credentials.WriteAsync(fixture.ConfigurationId, escapedSecret, CancellationToken.None);
        var before = Snapshot(fixture.Root);
        fixture.Handler.Stream = PartialFrame("OpenAI", escapedSecret) + "data: [DONE]\n\n";
        AssertError(await fixture.Chat("escaped secret question"), "invalid_response");
        Assert.Equal(before, Snapshot(fixture.Root));
    }

    [Fact]
    public async Task LeadingWhitespaceDoesNotProduceAnInvalidArchiveTitle()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var summary = ChatSummary(await fixture.Chat(new string(' ', 100) + "title"), fixture.ConfigurationId, Model, 1);
        Assert.Equal("title", summary.GetProperty("title").GetString());
    }

    [Fact]
    public async Task OversizedPageReturnsControlledErrorWithoutChangingTheArchive()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString("D");
        var archive = new StoredConversation(fixture.ConfigurationId, new Conversation(id, ChatMode.Ask,
            new[] { new Message(MessageRole.User, new ContentPart[] { new TextPart("question") }),
                new Message(MessageRole.Assistant, new ContentPart[] { new TextPart(new string('x', 4 * 1024 * 1024 + 1)) }) }, "large", Model), new[] { "completed" });
        await new ConversationStore(fixture.HistoryPath).SaveAsync(archive, CancellationToken.None);
        var before = Snapshot(fixture.Root);
        AssertError(await fixture.Open(id), "history_too_large");
        Assert.Equal(before, Snapshot(fixture.Root));
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.Equal(0, fixture.Credentials.Reads);
    }

    [Fact]
    public async Task FullArchiveRejectsChatBeforeHttpOrCredentialRead()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var id = Guid.NewGuid().ToString("D");
        var messages = Enumerable.Range(0, 10000).SelectMany(_ => new[] {
            new Message(MessageRole.User, new ContentPart[] { new TextPart("q") }),
            new Message(MessageRole.Assistant, new ContentPart[] { new TextPart("a") }) }).ToArray();
        await new ConversationStore(fixture.HistoryPath).SaveAsync(new StoredConversation(fixture.ConfigurationId,
            new Conversation(id, ChatMode.Ask, messages, "full", Model), Enumerable.Repeat("completed", 10000).ToArray()), CancellationToken.None);
        Assert.Equal("byok.history.open.result", Type(await fixture.Open(id)));
        var before = Snapshot(fixture.Root);
        var http = fixture.Handler.Requests;
        var reads = fixture.Credentials.Reads;
        AssertError(await fixture.Chat("must not spend"), "history_too_large");
        Assert.Equal(http, fixture.Handler.Requests);
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(before, Snapshot(fixture.Root));
    }

    [Theory]
    [MemberData(nameof(InvalidHistoryPayloads))]
    public async Task InvalidHistorySchemasIdsTraversalAndUnconfirmedDeletionHaveNoSideEffects(string type, string rawPayload)
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        var conversationId = ChatSummary(await fixture.Chat("preserved archive"), fixture.ConfigurationId, Model, 1).GetProperty("id").GetString()!;
        var disk = Snapshot(fixture.Root);
        var requests = fixture.Handler.Requests;
        var reads = fixture.Credentials.Reads;
        var writes = fixture.Credentials.Writes;
        var deletes = fixture.Credentials.Deletes;
        fixture.Credentials.RejectReads = true;
        using var payload = JsonDocument.Parse(rawPayload.Replace("CONVERSATION", conversationId, StringComparison.Ordinal));
        AssertError(await fixture.Send(type, payload.RootElement));
        Assert.Equal(disk, Snapshot(fixture.Root));
        Assert.Equal(requests, fixture.Handler.Requests);
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(writes, fixture.Credentials.Writes);
        Assert.Equal(deletes, fixture.Credentials.Deletes);
        Assert.Equal(conversationId, Assert.Single(Payload(await fixture.List()).GetProperty("conversations").EnumerateArray()).GetProperty("id").GetString());
    }

    public static IEnumerable<object[]> InvalidHistoryPayloads()
    {
        foreach (var offset in new[] { "-1", "2", "1.5", "2147483648", "\"0\"", "null", "false" })
            yield return new object[] { "byok.history.open", "{\"conversationId\":\"CONVERSATION\",\"offset\":" + offset + ",\"limit\":1}" };
        foreach (var limit in new[] { "0", "-1", "11", "1.5", "2147483648", "\"1\"", "null", "true" })
            yield return new object[] { "byok.history.open", "{\"conversationId\":\"CONVERSATION\",\"offset\":0,\"limit\":" + limit + "}" };
        foreach (var invalidId in new[] { "not-a-guid", "../outside.json", "../../providers.json", "..\\outside.json", "00000000-0000-0000-0000-000000000000", "9aa56cdab36941dca64143cc7090eac9", "{9aa56cda-b369-41dc-a641-43cc7090eac9}", "" })
        {
            yield return new object[] { "byok.history.open", JsonSerializer.Serialize(new { conversationId = invalidId, offset = 0, limit = 1 }) };
            yield return new object[] { "byok.history.rename", JsonSerializer.Serialize(new { conversationId = invalidId, title = "bad rename" }) };
            yield return new object[] { "byok.history.delete", JsonSerializer.Serialize(new { conversationId = invalidId, confirm = true }) };
        }
        foreach (var query in new[] { "null", "7", "false", JsonSerializer.Serialize(new string('q', 257)) })
            yield return new object[] { "byok.history.list", "{\"query\":" + query + "}" };
        foreach (var title in new[] { "null", "7", "false", "\"\"", "\"   \"", JsonSerializer.Serialize(new string('t', 161)) })
            yield return new object[] { "byok.history.rename", "{\"conversationId\":\"CONVERSATION\",\"title\":" + title + "}" };
        foreach (var confirm in new[] { "false", "null", "1", "\"true\"" })
            yield return new object[] { "byok.history.delete", "{\"conversationId\":\"CONVERSATION\",\"confirm\":" + confirm + "}" };
        yield return new object[] { "byok.history.list", "{}" };
        yield return new object[] { "byok.history.list", "{\"query\":\"\",\"extra\":true}" };
        yield return new object[] { "byok.history.list", "{\"query\":\"\",\"query\":\"duplicate\"}" };
        yield return new object[] { "byok.history.open", "{\"conversationId\":\"CONVERSATION\",\"limit\":1}" };
        yield return new object[] { "byok.history.open", "{\"conversationId\":\"CONVERSATION\",\"offset\":0}" };
        yield return new object[] { "byok.history.open", "{\"conversationId\":\"CONVERSATION\",\"offset\":0,\"limit\":1,\"extra\":true}" };
        yield return new object[] { "byok.history.new", "{\"extra\":true}" };
        yield return new object[] { "byok.history.rename", "{\"conversationId\":\"CONVERSATION\"}" };
        yield return new object[] { "byok.history.rename", "{\"conversationId\":\"CONVERSATION\",\"title\":\"rename\",\"extra\":true}" };
        yield return new object[] { "byok.history.delete", "{\"conversationId\":\"CONVERSATION\"}" };
        yield return new object[] { "byok.history.delete", "{\"conversationId\":\"CONVERSATION\",\"confirm\":true,\"extra\":true}" };
    }

    [Fact]
    public async Task FailedStorageReturnsControlledErrorWithoutReportingChatCompletion()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAndSelect();
        Assert.False(Directory.Exists(fixture.HistoryPath));
        await File.WriteAllTextAsync(fixture.HistoryPath, "history path is a file");
        var disk = Snapshot(fixture.Root);
        var requests = fixture.Handler.Requests;
        AssertError(await fixture.Chat("cannot persist this turn"));
        Assert.Equal(requests + 1, fixture.Handler.Requests);
        Assert.Equal(disk, Snapshot(fixture.Root));
        AssertSecretAbsent(ReadJsonFiles(fixture.Root));
        var reads = fixture.Credentials.Reads;
        requests = fixture.Handler.Requests;
        AssertError(await fixture.List());
        Assert.Equal(reads, fixture.Credentials.Reads);
        Assert.Equal(requests, fixture.Handler.Requests);
    }

    private static string Type(JsonElement reply) => reply.GetProperty("type").GetString()!;
    private static JsonElement Payload(JsonElement reply) => reply.GetProperty("payload");
    private static void AssertError(JsonElement reply, string? code = null)
    {
        Assert.Equal("error", Type(reply));
        Assert.False(string.IsNullOrWhiteSpace(Payload(reply).GetProperty("code").GetString()));
        if (code is not null) Assert.Equal(code, Payload(reply).GetProperty("code").GetString());
    }
    private static JsonElement ChatSummary(JsonElement reply, string configurationId, string modelId, int turns)
    {
        Assert.Equal("byok.chat.result", Type(reply));
        Assert.True(Payload(reply).GetProperty("completed").GetBoolean());
        var summary = Payload(reply).GetProperty("conversation");
        AssertSummary(summary, configurationId, modelId, turns);
        return summary;
    }
    private static void AssertSummary(JsonElement summary, string configurationId, string modelId, int turns)
    {
        Assert.Equal(new[] { "configurationId", "id", "modelId", "title", "turnCount", "updatedAtUtc" }, summary.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.True(Guid.TryParseExact(summary.GetProperty("id").GetString(), "D", out var id));
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(configurationId, summary.GetProperty("configurationId").GetString());
        Assert.Equal(modelId, summary.GetProperty("modelId").GetString());
        Assert.Equal(turns, summary.GetProperty("turnCount").GetInt32());
        Assert.True(DateTimeOffset.TryParse(summary.GetProperty("updatedAtUtc").GetString(), out var updated));
        Assert.Equal(TimeSpan.Zero, updated.Offset);
    }
    private static void AssertWireText(JsonElement message, string role, string text)
    {
        Assert.Equal(role, message.GetProperty("role").GetString());
        var content = message.GetProperty("content");
        if (content.ValueKind == JsonValueKind.String) Assert.Equal(text, content.GetString());
        else
        {
            var part = Assert.Single(content.EnumerateArray());
            Assert.Equal("text", part.GetProperty("type").GetString());
            Assert.Equal(text, part.GetProperty("text").GetString());
        }
    }
    private static void AssertSecretAbsent(IEnumerable<string> values)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Assert.DoesNotContain(Secret, "negative control " + Secret));
        foreach (var value in values) Assert.DoesNotContain(Secret, value);
    }
    private static void AssertUiPrivate(IEnumerable<string> values)
    {
        foreach (var forbidden in new[] { Image, "base64Data", "providerState", ReasoningSignature, ToolSignature })
        {
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Assert.DoesNotContain(forbidden, "negative control " + forbidden, StringComparison.OrdinalIgnoreCase));
            foreach (var value in values) Assert.DoesNotContain(forbidden, value, StringComparison.OrdinalIgnoreCase);
        }
    }
    private static string[] Snapshot(string root) => Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Select(path => "directory:" + path)
        .Concat(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToBase64String(File.ReadAllBytes(path))))
        .OrderBy(value => value, StringComparer.Ordinal).ToArray();
    private static string[] ReadJsonFiles(string root) => Directory.Exists(root) ? Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray() : Array.Empty<string>();
    private static Conversation ReadDomain(Fixture fixture, string conversationId)
    {
        var matches = new List<Conversation>();
        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("messages", out _) && value.TryGetProperty("id", out var id) && id.GetString() == conversationId)
                {
                    Assert.True(ConversationSerializer.TryParse(value.GetRawText(), out var domain, out var error), error);
                    matches.Add(domain!);
                }
                else foreach (var property in value.EnumerateObject()) Visit(property.Value);
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item);
        }
        foreach (var json in ReadJsonFiles(fixture.HistoryPath))
        {
            using var document = JsonDocument.Parse(json);
            Visit(document.RootElement);
        }
        return Assert.Single(matches);
    }
    private static string GeminiFrame(object[] parts, bool completed) => "data: " + JsonSerializer.Serialize(new
    {
        candidates = new[] { new { content = new { parts }, finishReason = completed ? "STOP" : (string?)null } }
    }) + "\n\n";
    private static string PartialFrame(string kind, string text) => kind switch
    {
        "Gemini" => GeminiFrame(new object[] { new { text } }, false),
        "Anthropic" => "event: content_block_start\ndata: " + JsonSerializer.Serialize(new { type = "content_block_start", index = 0, content_block = new { type = "text", text } }) + "\n\nevent: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n",
        _ => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } }) + "\n\n"
    };
    private static string CompletedFrame(string kind) => PartialFrame(kind, "completed answer") + (kind switch
    {
        "Gemini" => "data: {\"candidates\":[{\"finishReason\":\"STOP\"}]}\n\n",
        "Anthropic" => "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
        _ => "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
    });

    private sealed class Credentials : ICredentialStore
    {
        private readonly Dictionary<string, string> values = new();
        public int Reads;
        public int Writes;
        public int Deletes;
        public bool RejectReads;
        public Task<string?> ReadAsync(string id, CancellationToken token)
        {
            Reads++;
            Assert.False(RejectReads, "History operations must not read credentials.");
            return Task.FromResult(values.TryGetValue(id, out var value) ? value : null);
        }
        public Task WriteAsync(string id, string secret, CancellationToken token) { Writes++; values[id] = secret; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken token) { Deletes++; values.Remove(id); return Task.CompletedTask; }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests;
        public string Catalog = "";
        public string Stream = "";
        public BlockingStream? BlockingStream;
        public readonly List<string> ChatBodies = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("provider.test", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog) };
            Assert.Equal(HttpMethod.Post, request.Method);
            ChatBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = BlockingStream is null ? new StringContent(Stream) : new StreamContent(BlockingStream)
            };
        }
    }
    private sealed class BlockingStream : Stream
    {
        private readonly byte[] prefix;
        private readonly CancellationTokenSource disposed = new();
        private int position;
        public bool Disposed;
        public readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BlockingStream(string prefix) { this.prefix = Encoding.UTF8.GetBytes(prefix); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (position < prefix.Length)
            {
                var length = Math.Min(count, prefix.Length - position);
                Array.Copy(prefix, position, buffer, offset, length);
                position += length;
                return length;
            }
            Blocked.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposed.Token);
            await Task.Delay(Timeout.Infinite, linked.Token);
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposed = true; disposed.Cancel(); }
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Lumen.ConversationSessionTests." + Guid.NewGuid().ToString("N"));
        public string HistoryPath => Path.Combine(Root, "conversations");
        public string ConfigurationId { get; } = Guid.NewGuid().ToString("D");
        public readonly Credentials Credentials = new();
        public readonly Handler Handler = new();
        public Action<JsonElement>? OnReply;
        private readonly string kind;
        private readonly HttpClient client;
        private readonly ProviderFactory providers;
        private readonly List<string> replies = new();
        private ByokSession session;
        public string[] Replies { get { lock (replies) return replies.ToArray(); } }
        public Fixture(string kind = "OpenAI")
        {
            this.kind = kind;
            Directory.CreateDirectory(Root);
            Handler.Catalog = kind == "Gemini"
                ? "{\"models\":[{\"name\":\"models/" + Model + "\",\"supportedGenerationMethods\":[\"generateContent\"]}]}"
                : "{\"data\":[{\"id\":\"" + Model + "\"},{\"id\":\"second-text-fixture\"}],\"has_more\":false}";
            Handler.Stream = CompletedFrame(kind);
            client = new HttpClient(Handler);
            providers = new ProviderFactory(new HttpTransport(client, (_, _) => Task.CompletedTask), new CapabilityResolver());
            session = CreateSession();
        }
        private ByokSession CreateSession() => new(providers, Credentials, new SettingsStore(Path.Combine(Root, "providers.json")));
        public void Restart() { session.Dispose(); session = CreateSession(); }
        public async Task<JsonElement> Send(string type, object payload)
        {
            var id = Guid.NewGuid().ToString("D");
            var json = JsonSerializer.Serialize(new { version = 1, id, type, payload });
            Assert.True(await session.HandleAsync("https://lumen.local/index.html", json, SendReply, CancellationToken.None));
            JsonElement? result = null;
            foreach (var response in Replies)
            {
                using var parsed = JsonDocument.Parse(response);
                var reply = parsed.RootElement;
                if (reply.GetProperty("id").GetString() == id && Type(reply) != "byok.event") result = reply.Clone();
            }
            Assert.True(result.HasValue, "The native request must return a correlated result or controlled error.");
            return result.Value;
        }
        private Task SendReply(string json)
        {
            lock (replies) replies.Add(json);
            using var parsed = JsonDocument.Parse(json);
            OnReply?.Invoke(parsed.RootElement);
            return Task.CompletedTask;
        }
        public async Task Configure(string? configurationId = null)
        {
            var result = await Send("byok.configure", new { id = configurationId ?? ConfigurationId, providerId = "fixture", kind, baseUrl = "https://provider.test/v1/", key = Secret });
            Assert.Equal("byok.configure.result", Type(result));
        }
        public async Task Select(string? configurationId = null, string modelId = Model)
        {
            var result = await Send("byok.select", new { configurationId = configurationId ?? ConfigurationId, modelId });
            Assert.Equal("byok.select.result", Type(result));
        }
        public async Task ConfigureAndSelect() { await Configure(); await Select(); }
        public Task<JsonElement> Chat(string text) => Send("byok.chat", new { configurationId = ConfigurationId, text, effort = "Off" });
        public Task<JsonElement> List(string query = "") => Send("byok.history.list", new { query });
        public Task<JsonElement> Open(string conversationId, int offset = 0, int limit = 10) => Send("byok.history.open", new { conversationId, offset, limit });
        public void Dispose() { session.Dispose(); client.Dispose(); Directory.Delete(Root, true); }
    }
}
