using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Metadata;
using AbpMcp.Registration;
using FluentAssertions;

namespace AbpMcp.Tests;

public class StructuredResultEnvelopeTests
{
    [Fact]
    public void ObjectSchemaAndPayloadPassThroughUnchanged()
    {
        var tool = Tool(new JsonObject { ["type"] = "object" });

        StructuredResultEnvelope.GetOutputSchema(tool).Should().BeSameAs(tool.OutputSchema);
        StructuredResultEnvelope.WrapPayload(tool, Json("""{"a":1}""")).GetRawText().Should().Be("""{"a":1}""");
    }

    [Fact]
    public void ArraySchemaIsWrappedInRequiredResultProperty()
    {
        var tool = Tool(new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } });

        var schema = StructuredResultEnvelope.GetOutputSchema(tool)!;

        schema["type"]!.GetValue<string>().Should().Be("object");
        schema["required"]![0]!.GetValue<string>().Should().Be("result");
        schema["properties"]!["result"]!["type"]!.GetValue<string>().Should().Be("array");
        StructuredResultEnvelope.WrapPayload(tool, Json("""["x"]""")).GetRawText().Should().Be("""{"result":["x"]}""");
    }

    [Fact]
    public void PermissiveSchemaWrapsEvenAnObjectPayload()
    {
        // `object` return types map to {} (any value). Wrapping follows the advertised schema, so
        // the payload shape never depends on what one call happened to return.
        var tool = Tool(new JsonObject());

        StructuredResultEnvelope.WrapPayload(tool, Json("""{"a":1}""")).GetRawText().Should().Be("""{"result":{"a":1}}""");
    }

    [Fact]
    public void WithoutSchemaOnlyNonObjectPayloadsAreWrapped()
    {
        var tool = Tool(outputSchema: null);

        StructuredResultEnvelope.GetOutputSchema(tool).Should().BeNull();
        StructuredResultEnvelope.WrapPayload(tool, Json("""{"a":1}""")).GetRawText().Should().Be("""{"a":1}""");
        StructuredResultEnvelope.WrapPayload(tool, Json("null")).GetRawText().Should().Be("""{"result":null}""");
    }

    private static ToolDescriptor Tool(JsonObject? outputSchema) => new()
    {
        Name = "Sample_Tool",
        Description = "test",
        InputSchema = new JsonObject { ["type"] = "object" },
        OutputSchema = outputSchema,
        ParameterNames = Array.Empty<string>(),
        RequiredPermissions = Array.Empty<string>(),
    };

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
