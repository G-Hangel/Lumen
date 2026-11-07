using System;
using System.Collections.Generic;

namespace Lumen.Core.Domain;

public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed class Message
{
    public Message(MessageRole role, IReadOnlyList<ContentPart> parts, string? modelId = null)
    {
        if (!Enum.IsDefined(typeof(MessageRole), role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown message role.");
        }

        if (parts is null)
        {
            throw new ArgumentNullException(nameof(parts));
        }

        if (parts.Count == 0)
        {
            throw new ArgumentException("A message must carry at least one content part.", nameof(parts));
        }

        Role = role;
        Parts = parts;
        ModelId = modelId;
    }

    public MessageRole Role { get; }
    public IReadOnlyList<ContentPart> Parts { get; }

    // Set for assistant messages so a later turn can tell which model produced them.
    public string? ModelId { get; }
}

public sealed class Conversation
{
    public Conversation(
        string id,
        ChatMode mode,
        IReadOnlyList<Message> messages,
        string? title = null,
        string? modelId = null,
        ReasoningIntent reasoningIntent = ReasoningIntent.Off,
        DateTimeOffset createdAtUtc = default,
        DateTimeOffset updatedAtUtc = default)
    {
        Id = string.IsNullOrEmpty(id) ? throw new ArgumentNullException(nameof(id)) : id;
        if (!Enum.IsDefined(typeof(ChatMode), mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown chat mode.");
        }

        if (!Enum.IsDefined(typeof(ReasoningIntent), reasoningIntent))
        {
            throw new ArgumentOutOfRangeException(nameof(reasoningIntent), reasoningIntent, "Unknown reasoning intent.");
        }

        Mode = mode;
        Messages = messages ?? throw new ArgumentNullException(nameof(messages));
        Title = title;
        ModelId = modelId;
        ReasoningIntent = reasoningIntent;

        // Parameters are typed to match the properties exactly, so System.Text.Json
        // can bind this constructor; default means "stamp it now".
        CreatedAtUtc = createdAtUtc == default ? DateTimeOffset.UtcNow : createdAtUtc;
        UpdatedAtUtc = updatedAtUtc == default ? CreatedAtUtc : updatedAtUtc;
    }

    public string Id { get; }
    public ChatMode Mode { get; }
    public IReadOnlyList<Message> Messages { get; }
    public string? Title { get; }
    public string? ModelId { get; }
    public ReasoningIntent ReasoningIntent { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }

    public Conversation WithMessage(Message message)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var messages = new List<Message>(Messages) { message };
        return new Conversation(Id, Mode, messages, Title, ModelId, ReasoningIntent, CreatedAtUtc, DateTimeOffset.UtcNow);
    }
}
