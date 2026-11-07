using System;
using System.Collections.Generic;
using System.Text.Json;
using Lumen.Core.Domain;
using Xunit;

namespace Lumen.Tests;

public sealed class ConversationSerializerTests
{
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static Conversation Sample()
    {
        using var arguments = JsonDocument.Parse("""{"path":"src/App.cs","pattern":"**/*.cs"}""");
        var messages = new List<Message>
        {
            new(MessageRole.System, new ContentPart[] { new TextPart("You are Lumen.") }),
            new(MessageRole.User, new ContentPart[]
            {
                new TextPart("Look at this"),
                new ImagePart("image/png", Pixel, ImageDetail.High),
                new ReasoningPart("The user wants a search.", "opaque-signature-verbatim")
            }, "anthropic/claude-sonnet"),
            new(MessageRole.Assistant, new ContentPart[]
            {
                new ReasoningPart("thinking"),
                new ToolCallPart("call_1", "search_files", arguments.RootElement.Clone())
            }),
            new(MessageRole.Tool, new ContentPart[] { new ToolResultPart("call_1", true, "no matches") })
        };

        return new Conversation(
            "6f1c0d2a-9d3f-4b6a-8a1e-2c5f7b9d0e11",
            ChatMode.Agent,
            messages,
            "Spike",
            "anthropic/claude-sonnet",
            ReasoningIntent.High,
            new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 7, 12, 5, 0, TimeSpan.Zero));
    }

    [Fact]
    public void RoundTripPreservesEveryContentPartKind()
    {
        var original = Sample();

        var json = ConversationSerializer.ToJson(original);
        Assert.True(ConversationSerializer.TryParse(json, out var restored, out var error), error);

        Assert.NotNull(restored);
        Assert.Equal(original.Id, restored!.Id);
        Assert.Equal(ChatMode.Agent, restored.Mode);
        Assert.Equal(ReasoningIntent.High, restored.ReasoningIntent);
        Assert.Equal(original.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(original.UpdatedAtUtc, restored.UpdatedAtUtc);
        Assert.Equal(original.Messages.Count, restored.Messages.Count);

        var image = Assert.IsType<ImagePart>(restored.Messages[1].Parts[1]);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(Pixel, image.Base64Data);
        Assert.Equal(ImageDetail.High, image.Detail);

        var reasoning = Assert.IsType<ReasoningPart>(restored.Messages[1].Parts[2]);
        Assert.Equal("opaque-signature-verbatim", reasoning.ProviderState);

        var call = Assert.IsType<ToolCallPart>(restored.Messages[2].Parts[1]);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("search_files", call.Name);
        Assert.Equal("**/*.cs", call.Arguments.GetProperty("pattern").GetString());
        Assert.Equal(JsonValueKind.Object, call.Arguments.ValueKind);

        var result = Assert.IsType<ToolResultPart>(restored.Messages[3].Parts[0]);
        Assert.Equal("call_1", result.InvocationId);
        Assert.True(result.IsError);
        Assert.Equal("no matches", result.Content);

        Assert.Equal(MessageRole.Tool, restored.Messages[3].Role);
        Assert.Equal("anthropic/claude-sonnet", restored.Messages[1].ModelId);
    }

    [Fact]
    public void RoundTripIsStableAcrossTwoSerializations()
    {
        var first = ConversationSerializer.ToJson(Sample());
        Assert.True(ConversationSerializer.TryParse(first, out var restored, out var error), error);
        var second = ConversationSerializer.ToJson(restored!);
        Assert.Equal(first, second);
    }

    [Fact]
    public void UnknownContentPartIsPreservedAndNeverBecomesText()
    {
        const string json = """
        {
          "id": "c1",
          "mode": "Ask",
          "messages": [
            { "role": "user", "parts": [ { "kind": "hologram", "volume": 11, "nested": { "a": [1, 2] } } ] }
          ]
        }
        """;

        Assert.True(ConversationSerializer.TryParse(json, out var conversation, out var error), error);
        var part = conversation!.Messages[0].Parts[0];
        var unknown = Assert.IsType<UnknownPart>(part);
        Assert.IsNotType<TextPart>(part);
        Assert.Equal("hologram", unknown.Kind);
        Assert.Equal(11, unknown.Raw.GetProperty("volume").GetInt32());
        Assert.Equal(2, unknown.Raw.GetProperty("nested").GetProperty("a")[1].GetInt32());

        var rewritten = JsonDocument.Parse(ConversationSerializer.ToJson(conversation)).RootElement
            .GetProperty("messages")[0].GetProperty("parts")[0];
        Assert.Equal("hologram", rewritten.GetProperty("kind").GetString());
        Assert.Equal(11, rewritten.GetProperty("volume").GetInt32());
        Assert.Equal(2, rewritten.GetProperty("nested").GetProperty("a")[1].GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("""{"id":"c1","mode":"Agent","messages":[{"role":"user","parts":[{"kind":"text","text":"cut""")]
    [InlineData("""{"id":"c1","mode":"Wizard","messages":[]}""")]
    [InlineData("""{"id":"c1","mode":"Agent","messages":[{"role":"wizard","parts":[]}]}""")]
    [InlineData("""{"id":"","mode":"Agent","messages":[]}""")]
    [InlineData("""{"id":"c1","mode":"Agent","messages":[{"role":"user","parts":[]}]}""")]
    [InlineData("""{"id":"c1","mode":"Agent","messages":[{"role":"user","parts":["plain string"]}]}""")]
    [InlineData("""{"id":"c1","mode":"Agent","messages":[{"role":"user","parts":[{"kind":"text"}]}]}""")]
    [InlineData("""{"id":"c1","mode":"Agent","messages":[{"role":"user","parts":[{"kind":"image","mediaType":"image/png","base64Data":"not base64!"}]}]}""")]
    [InlineData("""{"id":"c1","mode":"Agent","messages":[{"role":"user","parts":[{"kind":"image","mediaType":"application/pdf","base64Data":"AAAA"}]}]}""")]
    [InlineData("""{"id":"c1","mode":"Agent","messages":[{"role":"user","parts":[{"kind":"toolCall","id":"t1","name":"n","arguments":[1,2]}]}]}""")]
    [InlineData("""{"id":"c1","mode":"Agent","messages":[{"role":"user","parts":[{"kind":"toolResult","invocationId":"t1","content":7}]}]}""")]
    [InlineData("""{"id":"c1","mode":"Agent","messages":[{"role":"user","parts":[{"kind":"reasoning","text":"\uD800"}]}]}""")]
    [InlineData("""{"id":"c1","mode":"Agent","messages":[{"role":"user","parts":[{"kind":"text","text":123}]}]}""")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void MalformedOrInvalidConversationFailsWithoutThrowing(string json)
    {
        Assert.False(ConversationSerializer.TryParse(json, out var conversation, out var error));
        Assert.Null(conversation);
        Assert.Equal(ConversationSerializer.InvalidJson, error);
    }

    [Fact]
    public void DeeplyNestedJsonIsRejectedByDepthLimit()
    {
        var json = "{\"id\":\"c1\",\"mode\":\"Ask\",\"messages\":[{\"role\":\"user\",\"parts\":[" +
            new string('[', 200) + new string(']', 200) + "]}]}";

        Assert.False(ConversationSerializer.TryParse(json, out var conversation, out var error));
        Assert.Null(conversation);
        Assert.Equal(ConversationSerializer.InvalidJson, error);
    }

    [Fact]
    public void DeserializedJsonElementsOutliveTheirBackingDocument()
    {
        const string json = """
        {
          "id": "c1",
          "mode": "Agent",
          "messages": [
            { "role": "assistant", "parts": [
              { "kind": "toolCall", "id": "t1", "name": "read_file", "arguments": { "path": "a.cs" } },
              { "kind": "hologram", "volume": 11 }
            ] }
          ]
        }
        """;

        Assert.True(ConversationSerializer.TryParse(json, out var conversation, out var error), error);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var call = Assert.IsType<ToolCallPart>(conversation!.Messages[0].Parts[0]);
        Assert.Equal("a.cs", call.Arguments.GetProperty("path").GetString());
        var unknown = Assert.IsType<UnknownPart>(conversation.Messages[0].Parts[1]);
        Assert.Equal(11, unknown.Raw.GetProperty("volume").GetInt32());
    }

    [Fact]
    public void ConstructorsRejectInvalidDomainState()
    {
        Assert.Throws<ArgumentException>(() => new ImagePart("image/png", "not base64!"));
        Assert.Throws<ArgumentException>(() => new ImagePart("image/png", string.Empty));
        Assert.Throws<ArgumentException>(() => new ImagePart("text/plain", "AAAA"));
        Assert.Throws<ArgumentException>(() => new ToolCallPart("t1", "n", JsonDocument.Parse("[1]").RootElement));
        Assert.Throws<ArgumentException>(() => new ToolSpec("read file", "d", JsonDocument.Parse("{}").RootElement));
        Assert.Throws<ArgumentException>(() => new Message(MessageRole.User, Array.Empty<ContentPart>()));
        Assert.Throws<ArgumentNullException>(() => new Conversation(string.Empty, ChatMode.Ask, new List<Message>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Conversation("c1", (ChatMode)999, new List<Message>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Message((MessageRole)999, new ContentPart[] { new TextPart("x") }));
    }

    [Fact]
    public void NullConversationCannotBeSerialized() =>
        Assert.Throws<ArgumentNullException>(() => ConversationSerializer.ToJson(null!));
}
