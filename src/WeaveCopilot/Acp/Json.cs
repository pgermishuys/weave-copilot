using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WeaveCopilot.Acp;

/// <summary>Source-generated serializer context: wire messages are JsonNode trees, so nothing here uses reflection.</summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
internal sealed partial class AcpJsonContext : JsonSerializerContext;

internal static class Json
{
    public static string Serialize(JsonNode node) => node.ToJsonString(AcpJsonContext.Default.Options);

    public static JsonNode? Parse(string text) => JsonNode.Parse(text);

    /// <summary>Copies a JsonElement (e.g. an SDK `JsonElement?`) into a standalone node.</summary>
    public static JsonNode? FromElement(JsonElement? e) => e is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } v ? JsonNode.Parse(v.GetRawText()) : null;

    public static string? Str(this JsonNode? n, string key) => n is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
