using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Metadata;

namespace AbpMcp.Registration;

/// <summary>
/// MCP requires a tool's <c>outputSchema</c> and its <c>structuredContent</c> to be JSON objects.
/// A tool returning a list, a scalar, or <c>object</c> is therefore wrapped as
/// <c>{ "result": &lt;value&gt; }</c>, with the schema wrapped to match.
/// </summary>
/// <remarks>
/// Both <c>tools/list</c> and <c>tools/call</c> go through this one type, so the advertised schema
/// and the returned payload cannot disagree about whether a tool is wrapped. The decision is made
/// from the descriptor's schema, not the runtime payload: a tool advertised as wrapped is always
/// returned wrapped, even if one call happens to produce an object.
/// </remarks>
internal static class StructuredResultEnvelope
{
    /// <summary>The single property that carries a non-object result.</summary>
    public const string ResultProperty = "result";

    /// <summary>The output schema to advertise for <paramref name="descriptor"/>, or null for none.</summary>
    public static JsonObject? GetOutputSchema(ToolDescriptor descriptor)
    {
        var schema = descriptor.OutputSchema;
        if (schema is null || IsObjectSchema(schema))
        {
            return schema;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { [ResultProperty] = schema.DeepClone() },
            ["required"] = new JsonArray(ResultProperty),
        };
    }

    /// <summary>The structured content to return for <paramref name="payload"/>.</summary>
    public static JsonElement WrapPayload(ToolDescriptor descriptor, JsonElement payload)
    {
        var wrap = descriptor.OutputSchema is { } schema
            ? !IsObjectSchema(schema)
            : payload.ValueKind != JsonValueKind.Object;

        if (!wrap)
        {
            return payload;
        }

        var envelope = new JsonObject { [ResultProperty] = JsonNode.Parse(payload.GetRawText()) };
        return JsonSerializer.SerializeToElement(envelope);
    }

    private static bool IsObjectSchema(JsonObject schema) =>
        schema["type"] is JsonValue type && type.TryGetValue<string>(out var name) && name == "object";
}
