using System.Text.Json;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// The subset of JSON Schema that abp-mcp emits (<c>type</c> including unions, <c>properties</c>,
/// <c>required</c>, <c>items</c>, <c>additionalProperties</c>, <c>enum</c>), checked the way a validating
/// MCP client checks <c>structuredContent</c> against <c>outputSchema</c>. Returns every violation, so a
/// failing test names the path, as the TypeScript SDK's error does.
/// </summary>
internal static class SchemaConformance
{
    public static IReadOnlyList<string> Violations(JsonElement schema, JsonElement value, string path = "")
    {
        var violations = new List<string>();
        Check(schema, value, path, violations);
        return violations;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> violations)
    {
        if (schema.TryGetProperty("type", out var type) && !TypeMatches(type, value))
        {
            violations.Add($"{path}: {value.ValueKind} does not match type {type.GetRawText()}");
            return;
        }

        if (schema.TryGetProperty("enum", out var values) &&
            !values.EnumerateArray().Any(v => JsonElement.DeepEquals(v, value)))
        {
            violations.Add($"{path}: {value.GetRawText()} is not one of {values.GetRawText()}");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            CheckObject(schema, value, path, violations);
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                Check(items, item, $"{path}/{index++}", violations);
            }
        }
    }

    private static void CheckObject(JsonElement schema, JsonElement value, string path, List<string> violations)
    {
        var properties = schema.TryGetProperty("properties", out var p) ? p : default;
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(n => n.GetString()!))
            {
                if (!value.TryGetProperty(name, out _))
                {
                    violations.Add($"{path}: missing required property '{name}'");
                }
            }
        }

        foreach (var property in value.EnumerateObject())
        {
            var childPath = $"{path}/{property.Name}";
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                Check(propertySchema, property.Value, childPath, violations);
            }
            else if (schema.TryGetProperty("additionalProperties", out var additional))
            {
                if (additional.ValueKind == JsonValueKind.False)
                {
                    violations.Add($"{childPath}: additional property not allowed");
                }
                else if (additional.ValueKind == JsonValueKind.Object)
                {
                    Check(additional, property.Value, childPath, violations);
                }
            }
        }
    }

    private static bool TypeMatches(JsonElement type, JsonElement value) => type.ValueKind == JsonValueKind.Array
        ? type.EnumerateArray().Any(t => Is(t.GetString()!, value))
        : Is(type.GetString()!, value);

    private static bool Is(string type, JsonElement value) => type switch
    {
        "null" => value.ValueKind == JsonValueKind.Null,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        _ => true,
    };
}
