using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbpMcp.Registration;

/// <summary>
/// MCP requires a tool's <c>outputSchema</c> and its <c>structuredContent</c> to be JSON objects. Tools
/// that return a collection or a primitive are therefore advertised and returned wrapped as
/// <c>{ "result": … }</c>. This is the single place that rule lives, so the advertised schema
/// (<c>tools/list</c>, <c>_discover</c>) and the returned payload (<c>tools/call</c>) cannot disagree.
/// </summary>
internal static class McpOutputShape
{
    /// <summary>The property that carries a wrapped non-object result.</summary>
    public const string ResultProperty = "result";

    /// <summary>True when the descriptor's schema has a non-object root and is advertised wrapped.</summary>
    public static bool IsWrapped(JsonObject? outputSchema) =>
        outputSchema is not null && !IsObjectSchema(outputSchema);

    /// <summary>The output schema as advertised to MCP clients, or null when the tool has none.</summary>
    public static JsonObject? ToProtocolSchema(JsonObject? outputSchema)
    {
        if (outputSchema is null || IsObjectSchema(outputSchema))
        {
            return outputSchema;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { [ResultProperty] = outputSchema.DeepClone() },
            ["required"] = new JsonArray(ResultProperty),
        };
    }

    /// <summary>
    /// The <c>structuredContent</c> for a successful call, or null when there is no object to send
    /// (the text content block still carries the payload).
    /// </summary>
    public static JsonElement? ToStructuredContent(JsonObject? outputSchema, JsonElement payload)
    {
        if (IsWrapped(outputSchema))
        {
            return JsonSerializer.SerializeToElement(new JsonObject
            {
                [ResultProperty] = JsonNode.Parse(payload.GetRawText()),
            });
        }

        return payload.ValueKind == JsonValueKind.Object ? payload : null;
    }

    private static bool IsObjectSchema(JsonObject schema) =>
        schema["type"] is JsonValue type &&
        type.TryGetValue<string>(out var name) &&
        string.Equals(name, "object", StringComparison.Ordinal);
}
