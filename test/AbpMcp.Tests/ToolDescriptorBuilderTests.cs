using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AbpMcp.Metadata;
using FluentAssertions;

namespace AbpMcp.Tests;

public class JsonSchemaMapperTests
{
    [Theory]
    [InlineData(typeof(int), "integer")]
    [InlineData(typeof(long), "integer")]
    [InlineData(typeof(short), "integer")]
    [InlineData(typeof(double), "number")]
    [InlineData(typeof(decimal), "number")]
    [InlineData(typeof(bool), "boolean")]
    [InlineData(typeof(string), "string")]
    public void MapsPrimitives(Type type, string expectedJsonType)
    {
        var schema = JsonSchemaMapper.Map(type);

        schema["type"]!.GetValue<string>().Should().Be(expectedJsonType);
    }

    [Fact]
    public void MapsGuidAsStringUuid()
    {
        var schema = JsonSchemaMapper.Map(typeof(Guid));

        schema["type"]!.GetValue<string>().Should().Be("string");
        schema["format"]!.GetValue<string>().Should().Be("uuid");
    }

    [Fact]
    public void MapsDateTimeAsStringDateTime()
    {
        var schema = JsonSchemaMapper.Map(typeof(DateTime));

        schema["type"]!.GetValue<string>().Should().Be("string");
        schema["format"]!.GetValue<string>().Should().Be("date-time");
    }

    [Fact]
    public void MapsNullableIntWithNullableFlag()
    {
        var schema = JsonSchemaMapper.Map(typeof(int?));

        schema["type"]!.GetValue<string>().Should().Be("integer");
        schema["nullable"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void MapsArraysOfPrimitives()
    {
        var schema = JsonSchemaMapper.Map(typeof(int[]));

        schema["type"]!.GetValue<string>().Should().Be("array");
        schema["items"]!.AsObject()["type"]!.GetValue<string>().Should().Be("integer");
    }

    [Fact]
    public void MapsEnumerableOfPrimitives()
    {
        var schema = JsonSchemaMapper.Map(typeof(IEnumerable<string>));

        schema["type"]!.GetValue<string>().Should().Be("array");
        schema["items"]!.AsObject()["type"]!.GetValue<string>().Should().Be("string");
    }

    [Fact]
    public void MapsEnumsAsStringWithValues()
    {
        var schema = JsonSchemaMapper.Map(typeof(SampleEnum));

        schema["type"]!.GetValue<string>().Should().Be("string");
        var values = schema["enum"]!.AsArray();
        values.Should().HaveCount(3);
        values.Select(v => v!.GetValue<string>()).Should().BeEquivalentTo("Alpha", "Beta", "Gamma");
    }

    [Fact]
    public void MapsDateOnlyAsStringDate()
    {
        var schema = JsonSchemaMapper.Map(typeof(DateOnly));

        schema["type"]!.GetValue<string>().Should().Be("string");
        schema["format"]!.GetValue<string>().Should().Be("date");
    }

    [Fact]
    public void WalksComplexTypeIntoNamedProperties()
    {
        var schema = JsonSchemaMapper.Map(typeof(ComplexDto));

        schema["type"]!.GetValue<string>().Should().Be("object");
        var props = schema["properties"]!.AsObject();
        props.Should().ContainKey("name");
        props.Should().ContainKey("count");
        props["count"]!["type"]!.GetValue<string>().Should().Be("integer");

        // Required-ness follows [Required], not NRT non-null.
        schema["required"]!.AsArray().Select(v => v!.GetValue<string>()).Should().Contain("name");
    }

    [Fact]
    public void WalksNestedComplexProperty()
    {
        var schema = JsonSchemaMapper.Map(typeof(ComplexDto));

        var child = schema["properties"]!["child"]!.AsObject();
        child["type"]!.GetValue<string>().Should().Be("object");
        var childProps = child["properties"]!.AsObject();
        childProps.Should().ContainKey("label");
        childProps["kind"]!["enum"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public void WalksCollectionOfComplexType()
    {
        var schema = JsonSchemaMapper.Map(typeof(ComplexDto));

        var items = schema["properties"]!["items"]!.AsObject();
        items["type"]!.GetValue<string>().Should().Be("array");
        items["items"]!["type"]!.GetValue<string>().Should().Be("object");
        items["items"]!["properties"]!.AsObject().Should().ContainKey("label");
    }

    [Fact]
    public void MapsDictionaryToObjectWithAdditionalProperties()
    {
        var schema = JsonSchemaMapper.Map(typeof(ComplexDto));

        var bag = schema["properties"]!["bag"]!.AsObject();
        bag["type"]!.GetValue<string>().Should().Be("object");
        // The value type is object, so additionalProperties is the permissive (empty) schema — but the
        // key point is it is NOT emitted as an array of {key,value} pairs.
        bag.Should().ContainKey("additionalProperties");
        bag.Should().NotContainKey("items");
    }

    [Fact]
    public void MapsTypedDictionaryValueSchema()
    {
        var schema = JsonSchemaMapper.Map(typeof(TypedBagDto));

        var scores = schema["properties"]!["scores"]!.AsObject();
        scores["type"]!.GetValue<string>().Should().Be("object");
        scores["additionalProperties"]!["type"]!.GetValue<string>().Should().Be("integer");
    }

    [Fact]
    public void MapsObjectToPermissiveSchema()
    {
        var schema = JsonSchemaMapper.Map(typeof(ComplexDto));

        // object => any JSON value => empty schema (no "type"), never a misleading empty object schema.
        var anything = schema["properties"]!["anything"]!.AsObject();
        anything.Should().BeEmpty();
    }

    [Fact]
    public void HonorsJsonPropertyName()
    {
        var schema = JsonSchemaMapper.Map(typeof(ComplexDto));

        var props = schema["properties"]!.AsObject();
        props.Should().ContainKey("custom_name");
        props.Should().NotContainKey("renamed");
    }

    [Fact]
    public void SkipsJsonIgnoreProperties()
    {
        var schema = JsonSchemaMapper.Map(typeof(ComplexDto));

        schema["properties"]!.AsObject().Should().NotContainKey("hidden");
    }

    [Fact]
    public void JsonExtensionDataOpensAdditionalPropertiesInsteadOfLiteralProperty()
    {
        var schema = JsonSchemaMapper.Map(typeof(ComplexDto));

        schema["properties"]!.AsObject().Should().NotContainKey("extra");
        schema["additionalProperties"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void EmitsRangeConstraints()
    {
        var schema = JsonSchemaMapper.Map(typeof(ComplexDto));

        var ranged = schema["properties"]!["ranged"]!.AsObject();
        ranged["minimum"]!.GetValue<double>().Should().Be(1);
        ranged["maximum"]!.GetValue<double>().Should().Be(90);
    }

    [Fact]
    public void EmitsStringLengthConstraints()
    {
        var schema = JsonSchemaMapper.Map(typeof(ComplexDto));

        var bounded = schema["properties"]!["bounded"]!.AsObject();
        bounded["maxLength"]!.GetValue<int>().Should().Be(120);
        bounded["minLength"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void RecursionGuardCollapsesSelfReferenceInsteadOfOverflowing()
    {
        // Must return (not stack overflow); the self-referential child collapses to an opaque object.
        var schema = JsonSchemaMapper.Map(typeof(RecursiveNode));

        var next = schema["properties"]!["next"]!.AsObject();
        next["type"]!.GetValue<string>().Should().Be("object");
        next.Should().NotContainKey("properties");
    }

    [Fact]
    public void MapParameterEmitsParameterLevelConstraints()
    {
        var parameter = typeof(JsonSchemaMapperTests)
            .GetMethod(nameof(RangedParam), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetParameters()[0];

        var schema = JsonSchemaMapper.MapParameter(parameter);

        schema["minimum"]!.GetValue<double>().Should().Be(-90);
        schema["maximum"]!.GetValue<double>().Should().Be(90);
    }

    [Theory]
    [InlineData(0, true)]   // ComplexDto body, non-nullable reference => required
    [InlineData(1, false)]  // string? optionalRef => optional
    [InlineData(2, true)]   // int value => required
    [InlineData(3, false)]  // int withDefault = 5 => optional
    public void IsRequiredParameter_HonorsNullabilityAndDefaults(int index, bool expected)
    {
        var parameter = typeof(JsonSchemaMapperTests)
            .GetMethod(nameof(SampleParams), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetParameters()[index];

        JsonSchemaMapper.IsRequiredParameter(parameter).Should().Be(expected);
    }

    private void SampleParams(ComplexDto body, string? optionalRef, int value, int withDefault = 5) { }

    private void RangedParam([Range(-90, 90)] double lat) { }

    private enum SampleEnum { Alpha, Beta, Gamma }

    private sealed class NestedDto
    {
        public string Label { get; set; } = string.Empty;
        public SampleEnum Kind { get; set; }
    }

    private sealed class ComplexDto
    {
        [Required] public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public NestedDto? Child { get; set; }
        public List<NestedDto> Items { get; set; } = new();
        public Dictionary<string, object> Bag { get; set; } = new();
        public object? Anything { get; set; }
        [JsonPropertyName("custom_name")] public string Renamed { get; set; } = string.Empty;
        [Range(1, 90)] public int Ranged { get; set; }
        [StringLength(120, MinimumLength = 2)] public string Bounded { get; set; } = string.Empty;
        [JsonIgnore] public string Hidden { get; set; } = string.Empty;
        [JsonExtensionData] public Dictionary<string, object>? Extra { get; set; }
    }

    private sealed class TypedBagDto
    {
        public Dictionary<string, int> Scores { get; set; } = new();
    }

    private sealed class RecursiveNode
    {
        public string Value { get; set; } = string.Empty;
        public RecursiveNode? Next { get; set; }
    }
}

public class DefaultToolNameNormalizerTests
{
    [Fact]
    public void StripsAppServiceSuffixAndAsyncSuffix()
    {
        var serviceType = typeof(ProductAppService);
        var method = serviceType.GetMethod(nameof(ProductAppService.CreateAsync))!;

        var actual = DefaultToolNameNormalizer.Normalize(new ToolNamingContext
        {
            ServiceType = serviceType,
            Method = method,
        });

        actual.Should().Be("Product_Create");
    }

    [Fact]
    public void StripsServiceSuffix()
    {
        var serviceType = typeof(OrderService);
        var method = serviceType.GetMethod(nameof(OrderService.GetList))!;

        var actual = DefaultToolNameNormalizer.Normalize(new ToolNamingContext
        {
            ServiceType = serviceType,
            Method = method,
        });

        actual.Should().Be("Order_GetList");
    }

    [Fact]
    public void AppliesPrefix()
    {
        var serviceType = typeof(ProductAppService);
        var method = serviceType.GetMethod(nameof(ProductAppService.CreateAsync))!;

        var actual = DefaultToolNameNormalizer.Normalize(new ToolNamingContext
        {
            ServiceType = serviceType,
            Method = method,
            ConfiguredPrefix = "myapp_",
        });

        actual.Should().Be("myapp_Product_Create");
    }

    [Fact]
    public void CustomNormalizerReplacesDefault()
    {
        // Composing on top of the default — verify the option-shaped surface works.
        var options = new AbpMcpOptions
        {
            ToolNameNormalizer = ctx => "custom_" + ctx.Method.Name,
        };

        var name = options.ToolNameNormalizer(new ToolNamingContext
        {
            ServiceType = typeof(ProductAppService),
            Method = typeof(ProductAppService).GetMethod(nameof(ProductAppService.CreateAsync))!,
        });

        name.Should().Be("custom_CreateAsync");
    }

    private sealed class ProductAppService
    {
        public void CreateAsync() { }
    }

    private sealed class OrderService
    {
        public void GetList() { }
    }
}

public class ExposedAssembliesTests
{
    [Fact]
    public void StartsEmpty()
    {
        var collection = new ExposedAssembliesCollection();

        collection.IsEmpty.Should().BeTrue();
        collection.Count.Should().Be(0);
    }

    [Fact]
    public void CreateRegistersAssembly()
    {
        var collection = new ExposedAssembliesCollection();
        var asm = typeof(ExposedAssembliesTests).Assembly;

        var entry = collection.Create(asm);

        entry.Assembly.Should().BeSameAs(asm);
        collection.IsEmpty.Should().BeFalse();
        collection.Should().ContainSingle();
    }

    [Fact]
    public void CreateInvokesConfigurator()
    {
        var collection = new ExposedAssembliesCollection();
        var configured = false;

        collection.Create(typeof(ExposedAssembliesTests).Assembly, _ => configured = true);

        configured.Should().BeTrue();
    }
}
