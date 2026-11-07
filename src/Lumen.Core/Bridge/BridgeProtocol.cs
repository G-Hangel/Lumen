using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Lumen.Core.Bridge;

public static class BridgeProtocol
{
    public const int MaxMessageLength = 65536;
    public const int MaxTextLength = 4096;

    public static bool IsTrustedSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "lumen.local" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.AbsolutePath == "/" || uri.AbsolutePath == "/index.html");

    public static BridgeMessage? Handle(string source, string json, BridgePayload theme)
    {
        if (!IsTrustedSource(source))
            return null;

        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxMessageLength)
            return Error(null, "invalid_message");

        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (!HasFields(root, "version", "id", "type", "payload"))
                return Error(null, "invalid_message");

            var identifier = root.GetProperty("id");
            if (identifier.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(identifier.GetString(), "D", out var correlation) ||
                correlation == Guid.Empty)
                return Error(null, "invalid_message");

            id = identifier.GetString();
            var version = root.GetProperty("version");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
                return Error(id, "invalid_message");

            var type = root.GetProperty("type");
            if (type.ValueKind != JsonValueKind.String)
                return Error(id, "invalid_message");

            var payload = root.GetProperty("payload");
            switch (type.GetString())
            {
                case "ready" when HasFields(payload):
                    return new BridgeMessage { Id = id, Type = "ready.result", Payload = theme };
                case "ping" when HasFields(payload, "text"):
                    var text = payload.GetProperty("text");
                    if (text.ValueKind != JsonValueKind.String || text.GetString()!.Length > MaxTextLength)
                        return Error(id, "invalid_message");
                    return new BridgeMessage
                    {
                        Id = id,
                        Type = "ping.result",
                        Payload = new BridgePayload { Text = text.GetString() }
                    };
                default:
                    return Error(id, "invalid_message");
            }
        }
        // JsonDocument defers UTF-16 validation until strings or property names are decoded.
        catch (Exception exception) when (exception is JsonException || exception is InvalidOperationException)
        {
            return Error(id, "invalid_message");
        }
    }

    private static bool HasFields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!remaining.Remove(property.Name))
                return false;

        return remaining.Count == 0;
    }

    private static BridgeMessage Error(string? id, string code) => new()
    {
        Id = id,
        Type = "error",
        Payload = new BridgePayload { Code = code }
    };
}
