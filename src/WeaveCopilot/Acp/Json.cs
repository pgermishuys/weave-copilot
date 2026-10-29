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
    /// <summary>Serializes a wire message, omitting null fields (but not a null `result`: `_fleet/work/output` answers null for unknown work; raw tool input/output is passed through untouched).</summary>
    public static string Serialize(JsonNode node)
    {
        Prune(node);
        return node.ToJsonString(AcpJsonContext.Default.Options);
    }

    private static void Prune(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Where(kv => kv.Value is null && kv.Key is not ("id" or "result")).Select(kv => kv.Key).ToList()) o.Remove(key);
                foreach (var (key, value) in o) if (key is not ("rawInput" or "rawOutput")) Prune(value);
                break;
            case JsonArray a:
                foreach (var item in a) Prune(item);
                break;
        }
    }

    public static JsonNode? Parse(string text) => JsonNode.Parse(text);

    /// <summary>Copies a JsonElement (e.g. an SDK `JsonElement?`) into a standalone node.</summary>
    public static JsonNode? FromElement(JsonElement? e) => e is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } v ? JsonNode.Parse(v.GetRawText()) : null;

    public static string? Str(this JsonNode? n, string key) => n is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
