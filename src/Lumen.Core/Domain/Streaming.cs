using System;
using System.Text.Json;

namespace Lumen.Core.Domain;

public enum StreamEventKind
{
    TextDelta,
    ReasoningDelta,
    ToolCallStart,
    ToolCallArgsDelta,
    ToolCallEnd,
    Usage,
    Done,
    Error
}

public enum FinishReason
{
    Stop,
    MaxTokens,
    ToolUse,
    ContentFiltered,
    Cancelled,
    Other
}

public abstract class StreamEvent
{
    public abstract StreamEventKind Kind { get; }
}

public sealed class TextDelta : StreamEvent
{
    public TextDelta(string text) => Text = text ?? throw new ArgumentNullException(nameof(text));

    public override StreamEventKind Kind => StreamEventKind.TextDelta;
    public string Text { get; }
}

public sealed class ReasoningDelta : StreamEvent
{
    public ReasoningDelta(string text) => Text = text ?? throw new ArgumentNullException(nameof(text));

    public override StreamEventKind Kind => StreamEventKind.ReasoningDelta;
    public string Text { get; }
}

public sealed class ToolCallStart : StreamEvent
{
    public ToolCallStart(string id, string name)
    {
        Id = string.IsNullOrEmpty(id) ? throw new ArgumentNullException(nameof(id)) : id;
        Name = string.IsNullOrEmpty(name) ? throw new ArgumentNullException(nameof(name)) : name;
    }

    public override StreamEventKind Kind => StreamEventKind.ToolCallStart;
    public string Id { get; }
    public string Name { get; }
}

public sealed class ToolCallArgsDelta : StreamEvent
{
    public ToolCallArgsDelta(string id, string chunk)
    {
        Id = string.IsNullOrEmpty(id) ? throw new ArgumentNullException(nameof(id)) : id;
        Chunk = chunk ?? throw new ArgumentNullException(nameof(chunk));
    }

    public override StreamEventKind Kind => StreamEventKind.ToolCallArgsDelta;
    public string Id { get; }
    public string Chunk { get; }
}

public sealed class ToolCallEnd : StreamEvent
{
    public ToolCallEnd(string id, JsonElement parsedArgs)
    {
        Id = string.IsNullOrEmpty(id) ? throw new ArgumentNullException(nameof(id)) : id;
        if (parsedArgs.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Parsed tool arguments must be a JSON object.", nameof(parsedArgs));
        }

        ParsedArgs = parsedArgs;
    }

    public override StreamEventKind Kind => StreamEventKind.ToolCallEnd;
    public string Id { get; }
    public JsonElement ParsedArgs { get; }
}

public sealed class Usage : StreamEvent
{
    public Usage(int inputTokens, int outputTokens, int? reasoningTokens = null)
    {
        if (inputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputTokens), inputTokens, "Token counts must not be negative.");
        }

        if (outputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputTokens), outputTokens, "Token counts must not be negative.");
        }

        if (reasoningTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reasoningTokens), reasoningTokens, "Token counts must not be negative.");
        }

        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        ReasoningTokens = reasoningTokens;
    }

    public override StreamEventKind Kind => StreamEventKind.Usage;
    public int InputTokens { get; }
    public int OutputTokens { get; }
    public int? ReasoningTokens { get; }
}

public sealed class Done : StreamEvent
{
    public Done(FinishReason finishReason)
    {
        if (!Enum.IsDefined(typeof(FinishReason), finishReason))
        {
            throw new ArgumentOutOfRangeException(nameof(finishReason), finishReason, "Unknown finish reason.");
        }

        FinishReason = finishReason;
    }

    public override StreamEventKind Kind => StreamEventKind.Done;
    public FinishReason FinishReason { get; }
}

public sealed class StreamError : StreamEvent
{
    public StreamError(string code, string message)
    {
        Code = string.IsNullOrEmpty(code) ? throw new ArgumentNullException(nameof(code)) : code;
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    public override StreamEventKind Kind => StreamEventKind.Error;
    public string Code { get; }

    // Must never carry a credential: providers echo request bodies in some errors.
    public string Message { get; }
}
