using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumen.Core.Domain;

public abstract class ContentPart
{
    public abstract string Kind { get; }
}

public sealed class TextPart : ContentPart
{
    public TextPart(string text) => Text = text ?? throw new ArgumentNullException(nameof(text));

    public override string Kind => "text";
    public string Text { get; }
}

public sealed class ImagePart : ContentPart
{
    public ImagePart(string mediaType, string base64Data, ImageDetail detail = ImageDetail.Auto)
    {
        if (string.IsNullOrEmpty(mediaType) || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Media type must be an image type.", nameof(mediaType));
        }

        try
        {
            if (Convert.FromBase64String(base64Data).Length == 0)
            {
                throw new ArgumentException("Image data must not be empty.", nameof(base64Data));
            }
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Image data must be valid base64.", nameof(base64Data), exception);
        }

        MediaType = mediaType;
        Base64Data = base64Data;
        Detail = detail;
    }

    public override string Kind => "image";
    public string MediaType { get; }
    public string Base64Data { get; }
    public ImageDetail Detail { get; }
}

public enum ImageDetail
{
    Auto,
    Low,
    High
}

public sealed class ToolCallPart : ContentPart
{
    public ToolCallPart(string id, string name, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool call arguments must be a JSON object.", nameof(arguments));
        }

        Id = string.IsNullOrEmpty(id) ? throw new ArgumentNullException(nameof(id)) : id;
        Name = string.IsNullOrEmpty(name) ? throw new ArgumentNullException(nameof(name)) : name;
        Arguments = arguments;
    }

    public override string Kind => "toolCall";
    public string Id { get; }
    public string Name { get; }
    public JsonElement Arguments { get; }
}

public sealed class ToolResultPart : ContentPart
{
    public ToolResultPart(string invocationId, bool isError, string content)
    {
        InvocationId = string.IsNullOrEmpty(invocationId) ? throw new ArgumentNullException(nameof(invocationId)) : invocationId;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        IsError = isError;
    }

    public override string Kind => "toolResult";
    public string InvocationId { get; }
    public bool IsError { get; }
    public string Content { get; }
}

public sealed class ReasoningPart : ContentPart
{
    public ReasoningPart(string text, string? providerState = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        ProviderState = providerState;
    }

    public override string Kind => "reasoning";
    public string Text { get; }

    // Opaque provider state (Anthropic signature, Gemini thoughtSignature,
    // encrypted OpenAI reasoning) that must be replayed verbatim next turn.
    public string? ProviderState { get; }
}

public sealed class UnknownPart : ContentPart
{
    public UnknownPart(string kind, JsonElement raw)
    {
        Kind = string.IsNullOrEmpty(kind) ? throw new ArgumentNullException(nameof(kind)) : kind;
        Raw = raw;
    }

    public override string Kind { get; }
    public JsonElement Raw { get; }
}

public sealed class ContentPartConverter : JsonConverter<ContentPart>
{
    private static readonly IReadOnlyDictionary<string, Func<JsonElement, ContentPart>> Readers =
        new Dictionary<string, Func<JsonElement, ContentPart>>(StringComparer.Ordinal)
        {
            ["text"] = element => new TextPart(RequireString(element, "text")),
            ["image"] = element => new ImagePart(
                RequireString(element, "mediaType"),
                RequireString(element, "base64Data"),
                element.TryGetProperty("detail", out var detail)
                    ? detail.Deserialize<ImageDetail>(ConversationSerializer.Options)
                    : ImageDetail.Auto),
            ["toolCall"] = element => new ToolCallPart(
                RequireString(element, "id"),
                RequireString(element, "name"),
                RequireProperty(element, "arguments").Clone()),
            ["toolResult"] = element => new ToolResultPart(
                RequireString(element, "invocationId"),
                element.TryGetProperty("isError", out var isError) && isError.GetBoolean(),
                RequireString(element, "content")),
            ["reasoning"] = element => new ReasoningPart(
                RequireString(element, "text"),
                element.TryGetProperty("providerState", out var state) && state.ValueKind == JsonValueKind.String
                    ? state.GetString()
                    : null)
        };

    public override ContentPart? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A content part must be a JSON object.");
        }

        var kind = element.TryGetProperty("kind", out var kindProperty) && kindProperty.ValueKind == JsonValueKind.String
            ? kindProperty.GetString()!
            : throw new JsonException("A content part must declare a string 'kind'.");

        if (!Readers.TryGetValue(kind, out var read))
        {
            return new UnknownPart(kind, element.Clone());
        }

        try
        {
            return read(element);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException($"Content part '{kind}' is invalid: {exception.Message}", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, ContentPart value, JsonSerializerOptions options)
    {
        if (value is UnknownPart unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        switch (value)
        {
            case TextPart text:
                writer.WriteString("text", text.Text);
                break;
            case ImagePart image:
                writer.WriteString("mediaType", image.MediaType);
                writer.WriteString("base64Data", image.Base64Data);
                writer.WriteString("detail", Enum.GetName(typeof(ImageDetail), image.Detail));
                break;
            case ToolCallPart call:
                writer.WriteString("id", call.Id);
                writer.WriteString("name", call.Name);
                writer.WritePropertyName("arguments");
                call.Arguments.WriteTo(writer);
                break;
            case ToolResultPart result:
                writer.WriteString("invocationId", result.InvocationId);
                writer.WriteBoolean("isError", result.IsError);
                writer.WriteString("content", result.Content);
                break;
            case ReasoningPart reasoning:
                writer.WriteString("text", reasoning.Text);
                if (reasoning.ProviderState is not null)
                {
                    writer.WriteString("providerState", reasoning.ProviderState);
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported content part: {value.GetType().Name}");
        }

        writer.WriteEndObject();
    }

    private static string RequireString(JsonElement element, string name)
    {
        var property = RequireProperty(element, name);
        return property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new JsonException($"Property '{name}' must be a string.");
    }

    private static JsonElement RequireProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
            ? property
            : throw new JsonException($"Property '{name}' is required.");
}
