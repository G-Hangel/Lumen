using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumen.Core.Bridge;

public sealed class BridgeMessage
{
    public int Version { get; set; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public BridgePayload Payload { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class BridgePayload
{
    public string? Text { get; set; }
    public string? Code { get; set; }
    public string? Culture { get; set; }
    public string? Background { get; set; }
    public string? Foreground { get; set; }
    public string? Accent { get; set; }
}
