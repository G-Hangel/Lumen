using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumen.Core.Domain;

public static class ConversationSerializer
{
    public const string InvalidJson = "invalid_json";
    public const string InvalidConversation = "invalid_conversation";

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 64,
        Converters = { new ContentPartConverter(), new JsonStringEnumConverter() }
    };

    public static string ToJson(Conversation conversation)
    {
        if (conversation is null)
        {
            throw new ArgumentNullException(nameof(conversation));
        }

        return JsonSerializer.Serialize(conversation, Options);
    }

    public static bool TryParse(string? json, out Conversation? conversation, out string error)
    {
        conversation = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = InvalidJson;
            return false;
        }

        Conversation? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Conversation>(json!, Options);
        }
        // JsonDocument defers UTF-16 validation until strings or property names are decoded.
        catch (Exception exception) when (exception is JsonException
            || exception is InvalidOperationException
            || exception is ArgumentException
            || exception is ArgumentNullException)
        {
            error = InvalidJson;
            return false;
        }

        if (parsed is null)
        {
            error = InvalidJson;
            return false;
        }

        conversation = parsed;
        error = string.Empty;
        return true;
    }
}
