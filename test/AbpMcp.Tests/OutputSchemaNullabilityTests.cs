using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using AbpMcp.Metadata;
using FluentAssertions;

namespace AbpMcp.Tests;

/// <summary>
/// #86: output schemas must admit <c>null</c> where the result can carry it, or clients that validate
/// <c>structuredContent</c> (the TypeScript MCP SDK, so Claude Code) reject the call.
/// </summary>
public class OutputSchemaNullabilityTests
{
    [Fact]
    public void NullableReferenceProperty_AdmitsNull()
    {
        var schema = Property(JsonSchemaMapper.MapOutput(typeof(AnnotatedDto)), "suspensionReason");

        Types(schema).Should().Equal("string", "null");
    }

    [Fact]
    public void NonNullReferenceProperty_StaysNonNull()
    {
        var schema = Property(JsonSchemaMapper.MapOutput(typeof(AnnotatedDto)), "name");

        schema["type"]!.GetValue<string>().Should().Be("string");
    }

    [Fact]
    public void ObliviousReferenceProperty_AdmitsNull()
    {
        var schema = JsonSchemaMapper.MapOutput(typeof(ObliviousDto));

        Types(Property(schema, "note")).Should().Equal("string", "null");
        Types(Property(schema, "child")).Should().Equal("object", "null");
    }

    [Fact]
    public void NullableValueTypeProperty_StillAdmitsNull()
    {
        var schema = Property(JsonSchemaMapper.MapOutput(typeof(AnnotatedDto)), "returnedAt");

        Types(schema).Should().Equal("string", "null");
    }

    [Fact]
    public void NullableNestedObject_AdmitsNull_AndKeepsItsProperties()
    {
        var schema = Property(JsonSchemaMapper.MapOutput(typeof(AnnotatedDto)), "manager");

        Types(schema).Should().Equal("object", "null");
        schema["properties"]!.AsObject().ContainsKey("name").Should().BeTrue();
    }

    [Fact]
    public void CollectionElements_FollowTheirAnnotation()
    {
        var schema = JsonSchemaMapper.MapOutput(typeof(AnnotatedDto));

        Types(Property(schema, "maybeTags")["items"]!.AsObject()).Should().Equal("string", "null");
        Property(schema, "tags")["items"]!["type"]!.GetValue<string>().Should().Be("string");
        Types(Property(schema, "maybeCodes")["items"]!.AsObject()).Should().Equal("string", "null");
    }

    [Fact]
    public void DictionaryValues_FollowTheirAnnotation()
    {
        var schema = Property(JsonSchemaMapper.MapOutput(typeof(AnnotatedDto)), "labels");

        Types(schema["additionalProperties"]!.AsObject()).Should().Equal("string", "null");
    }

    [Fact]
    public void ConstraintsStillApply_ToANullableProperty()
    {
        var schema = Property(JsonSchemaMapper.MapOutput(typeof(AnnotatedDto)), "suspensionReason");

        schema["maxLength"]!.GetValue<int>().Should().Be(200);
    }

    [Fact]
    public void Root_StaysAnObject()
    {
        JsonSchemaMapper.MapOutput(typeof(AnnotatedDto))["type"]!.GetValue<string>().Should().Be("object");
        JsonSchemaMapper.MapOutput(typeof(ObliviousDto))["type"]!.GetValue<string>().Should().Be("object");
    }

    [Fact]
    public void InputSchemas_AreUnchanged()
    {
        var schema = JsonSchemaMapper.Map(typeof(AnnotatedDto));

        Property(schema, "suspensionReason")["type"]!.GetValue<string>().Should().Be("string");
        Property(JsonSchemaMapper.Map(typeof(ObliviousDto)), "note")["type"]!.GetValue<string>().Should().Be("string");
    }

    private static JsonObject Property(JsonObject schema, string name) =>
        schema["properties"]![name]!.AsObject();

    private static string?[] Types(JsonObject schema) =>
        schema["type"]!.AsArray().Select(t => t?.GetValue<string>()).ToArray();

    public sealed class AnnotatedDto
    {
        public string Name { get; set; } = "";

        [StringLength(200)]
        public string? SuspensionReason { get; set; }

        public DateTime? ReturnedAt { get; set; }

        public PersonDto? Manager { get; set; }

        public List<string> Tags { get; set; } = [];

        public List<string?> MaybeTags { get; set; } = [];

        public string?[] MaybeCodes { get; set; } = [];

        public Dictionary<string, string?> Labels { get; set; } = [];
    }

    public sealed class PersonDto
    {
        public string Name { get; set; } = "";
    }
}

#nullable disable
/// <summary>A DTO compiled without nullable annotations: its reference properties can be null.</summary>
public sealed class ObliviousDto
{
    /// <summary>Unannotated string.</summary>
    public string Note { get; set; }

    /// <summary>Unannotated nested object.</summary>
    public OutputSchemaNullabilityTests.PersonDto Child { get; set; }
}
#nullable restore
