using System;
using System.Text.Json;
using Lumen.Core.Bridge;
using Xunit;

namespace Lumen.Tests;

public sealed class BridgeProtocolTests
{
    private const string Source = "https://lumen.local/index.html";
    private const string Id = "11111111-2222-3333-4444-555555555555";
    private static readonly BridgePayload Theme = new()
    {
        Culture = "pt-BR", Background = "#202020", Foreground = "#ffffff", Accent = "#80caff"
    };

    [Fact]
    public void ReadyReturnsHostThemeAndCorrelation()
    {
        var reply = BridgeProtocol.Handle(Source, Request("ready", "{}"), Theme);
        Assert.NotNull(reply);
        Assert.Equal(Id, reply.Id);
        Assert.Equal("ready.result", reply.Type);
        Assert.Same(Theme, reply.Payload);
    }

    [Theory]
    [InlineData("Olá, Lumen!")]
    [InlineData("<script>alert('x')</script>")]
    [InlineData("\"quoted\"\nsecond line")]
    public void PingRoundTripsTextWithoutExecutingIt(string text)
    {
        var reply = BridgeProtocol.Handle(Source, Request("ping", JsonSerializer.Serialize(new { text })), Theme);
        Assert.NotNull(reply);
        Assert.Equal("ping.result", reply.Type);
        Assert.Equal(Id, reply.Id);
        using var json = JsonDocument.Parse(reply.ToJson());
        Assert.Equal(text, json.RootElement.GetProperty("payload").GetProperty("text").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"id\":\"bad\",\"type\":\"ready\",\"payload\":{}}")]
    public void MalformedMessagesReturnControlledError(string json)
    {
        var reply = BridgeProtocol.Handle(Source, json, Theme);
        Assert.NotNull(reply);
        Assert.Equal("error", reply.Type);
        Assert.Equal("invalid_message", reply.Payload.Code);
        Assert.Null(reply.Payload.Text);
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    public void UnpairedSurrogatesReturnControlledError(string invalid)
    {
        var valid = Request("ping", "{\"text\":\"value\"}");
        var mutations = new[]
        {
            valid.Replace("value", invalid),
            valid.Replace(Id, invalid),
            valid.Replace("ping", invalid),
            valid.Replace("version", invalid),
            valid.Replace("text", invalid)
        };
        foreach (var mutation in mutations)
        {
            var reply = BridgeProtocol.Handle(Source, mutation, Theme)!;
            Assert.Equal("error", reply.Type);
            Assert.Equal("invalid_message", reply.Payload.Code);
        }
    }

    [Fact]
    public void ValidSurrogatePairRoundTrips()
    {
        var reply = BridgeProtocol.Handle(Source, Request("ping", "{\"text\":\"\\uD834\\uDD1E\"}"), Theme)!;
        Assert.Equal("ping.result", reply.Type);
        Assert.Equal("\uD834\uDD1E", reply.Payload.Text);
    }

    [Fact]
    public void UncorrelatedErrorSerializesExplicitNullId()
    {
        var reply = BridgeProtocol.Handle(Source, "{", Theme)!;
        using var json = JsonDocument.Parse(reply.ToJson());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("id").ValueKind);
    }

    [Theory]
    [InlineData("ready", "null")]
    [InlineData("ready", "{\"unexpected\":true}")]
    [InlineData("ping", "{\"text\":42}")]
    [InlineData("ping", "{\"text\":null}")]
    [InlineData("ping", "{\"text\":\"a\",\"text\":\"b\"}")]
    [InlineData("run_command", "{\"text\":\"whoami\"}")]
    public void InvalidPayloadsAndUnknownTypesAreRejected(string type, string payload)
    {
        var reply = BridgeProtocol.Handle(Source, Request(type, payload), Theme);
        Assert.NotNull(reply);
        Assert.Equal("error", reply.Type);
        Assert.Equal(Id, reply.Id);
    }

    [Theory]
    [InlineData("http://lumen.local/index.html")]
    [InlineData("https://lumen.local.evil/index.html")]
    [InlineData("https://lumen.local:444/index.html")]
    [InlineData("https://user@lumen.local/index.html")]
    [InlineData("https://lumen.local/other.html")]
    [InlineData("file:///C:/index.html")]
    public void UntrustedOriginsDoNotReceiveReplies(string source) =>
        Assert.Null(BridgeProtocol.Handle(source, Request("ready", "{}"), Theme));

    [Fact]
    public void OversizedMessagesAndTextAreRejected()
    {
        Assert.Equal("error", BridgeProtocol.Handle(Source, new string('x', BridgeProtocol.MaxMessageLength + 1), Theme)!.Type);
        var payload = JsonSerializer.Serialize(new { text = new string('x', BridgeProtocol.MaxTextLength + 1) });
        Assert.Equal("error", BridgeProtocol.Handle(Source, Request("ping", payload), Theme)!.Type);
    }

    [Fact]
    public void DuplicateFieldsAndWrongVersionsAreRejected()
    {
        var valid = Request("ready", "{}");
        var mutations = new[]
        {
            valid.Replace("\"version\":1", "\"version\":2"),
            valid.Replace("\"version\":1", "\"version\":1,\"version\":1"),
            valid.Replace("\"version\":1", "\"version\":1.5"),
            valid.Replace("\"version\":1", "\"version\":\"1\""),
            valid.Replace(Id, Guid.Empty.ToString("D"))
        };
        foreach (var mutation in mutations)
            Assert.Equal("error", BridgeProtocol.Handle(Source, mutation, Theme)!.Type);
    }

    private static string Request(string type, string payload) =>
        $"{{\"version\":1,\"id\":\"{Id}\",\"type\":\"{type}\",\"payload\":{payload}}}";
}
