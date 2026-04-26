using System.Text.Json.Nodes;
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
    public void MapsUnknownComplexTypeAsOpaqueObject()
    {
        var schema = JsonSchemaMapper.Map(typeof(SampleDto));

        schema["type"]!.GetValue<string>().Should().Be("object");
    }

    private enum SampleEnum { Alpha, Beta, Gamma }

    private sealed record SampleDto(string Name, int Count);
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
