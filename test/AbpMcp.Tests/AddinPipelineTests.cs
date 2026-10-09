using System.Text.Json.Nodes;
using AbpMcp.Addins;
using AbpMcp.Metadata;
using FluentAssertions;

namespace AbpMcp.Tests;

public class AbpMcpToolBuildContextTests
{
    [Fact]
    public void AddTool_MakesToolFindableAndListed()
    {
        var context = NewContext();

        context.AddTool(Tool("New_Tool"));

        context.FindTool("New_Tool").Should().NotBeNull();
        context.Tools.Select(t => t.Name).Should().Contain("New_Tool");
    }

    [Fact]
    public void AddTool_DuplicateName_Throws()
    {
        var context = NewContext(Tool("Dupe"));

        var act = () => context.AddTool(Tool("Dupe"));

        act.Should().Throw<AbpMcpConfigurationException>().WithMessage("*Dupe*");
    }

    [Fact]
    public void RemoveTool_RemovesAndReturnsTrue()
    {
        var context = NewContext(Tool("A"), Tool("B"));

        context.RemoveTool("A").Should().BeTrue();

        context.FindTool("A").Should().BeNull();
        context.Tools.Select(t => t.Name).Should().ContainSingle().Which.Should().Be("B");
    }

    [Fact]
    public void RemoveTool_ThenAddSameName_Succeeds()
    {
        // The name index must be rebuilt after removal so the freed name is reusable.
        var context = NewContext(Tool("A"));

        context.RemoveTool("A").Should().BeTrue();
        var act = () => context.AddTool(Tool("A"));

        act.Should().NotThrow();
        context.FindTool("A").Should().NotBeNull();
    }

    [Fact]
    public void RemoveTool_Unknown_ReturnsFalse() =>
        NewContext().RemoveTool("nope").Should().BeFalse();

    [Fact]
    public void Enrich_TransformsExistingDescriptor()
    {
        var context = NewContext(Tool("A"));

        context.Enrich("A", d => d with { Description = "changed" }).Should().BeTrue();

        context.FindTool("A")!.Description.Should().Be("changed");
    }

    [Fact]
    public void Enrich_Unknown_ReturnsFalse() =>
        NewContext().Enrich("nope", d => d).Should().BeFalse();

    [Fact]
    public void Enrich_RenamingTool_Throws()
    {
        var context = NewContext(Tool("A"));

        var act = () => context.Enrich("A", d => d with { Name = "B" });

        act.Should().Throw<AbpMcpConfigurationException>();
    }

    private static AbpMcpToolBuildContext NewContext(params ToolDescriptor[] tools) =>
        new(tools, new AbpMcpOptions(), new NullServiceProvider());

    private static ToolDescriptor Tool(string name) => new()
    {
        Name = name,
        Description = "desc",
        InputSchema = new JsonObject { ["type"] = "object" },
        ParameterNames = Array.Empty<string>(),
        RequiredPermissions = Array.Empty<string>(),
    };

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

public class OutputSchemaAddinTests
{
    [Fact]
    public void SetsOutputSchemaFromUnwrappedReturnType()
    {
        var context = NewContext(MethodTool("GetAsync"));

        new OutputSchemaAddin().Contribute(context);

        var schema = context.FindTool("Sample_Get")!.OutputSchema;
        schema.Should().NotBeNull();
        var props = schema!["properties"]!.AsObject();
        props.Should().ContainKey("value");
        props["count"]!["type"]!.GetValue<string>().Should().Be("integer");
    }

    [Fact]
    public void SkipsNonGenericTaskReturn()
    {
        var context = NewContext(MethodTool("DoAsync", "Sample_Do"));

        new OutputSchemaAddin().Contribute(context);

        context.FindTool("Sample_Do")!.OutputSchema.Should().BeNull();
    }

    [Fact]
    public void SkipsDynamicToolWithoutMethod()
    {
        var dynamicTool = new ToolDescriptor
        {
            Name = "Dyn",
            Description = "d",
            InputSchema = new JsonObject { ["type"] = "object" },
            ParameterNames = Array.Empty<string>(),
            RequiredPermissions = Array.Empty<string>(),
            Handler = (_, _) => Task.FromResult(default(System.Text.Json.JsonElement)),
        };
        var context = NewContext(dynamicTool);

        new OutputSchemaAddin().Contribute(context);

        context.FindTool("Dyn")!.OutputSchema.Should().BeNull();
    }

    [Fact]
    public void RespectsIncludeOutputSchemaFalse()
    {
        var context = new AbpMcpToolBuildContext(
            new[] { MethodTool("GetAsync") },
            new AbpMcpOptions { IncludeOutputSchema = false },
            new NullServiceProvider());

        new OutputSchemaAddin().Contribute(context);

        context.FindTool("Sample_Get")!.OutputSchema.Should().BeNull();
    }

    private static AbpMcpToolBuildContext NewContext(params ToolDescriptor[] tools) =>
        new(tools, new AbpMcpOptions(), new NullServiceProvider());

    private static ToolDescriptor MethodTool(string methodName, string toolName = "Sample_Get") => new()
    {
        Name = toolName,
        Description = "desc",
        ServiceType = typeof(SampleService),
        Method = typeof(SampleService).GetMethod(methodName)!,
        InputSchema = new JsonObject { ["type"] = "object" },
        ParameterNames = Array.Empty<string>(),
        RequiredPermissions = Array.Empty<string>(),
    };

    private sealed class SampleService
    {
        public Task<SampleResult> GetAsync() => Task.FromResult(new SampleResult());

        public Task DoAsync() => Task.CompletedTask;
    }

    private sealed class SampleResult
    {
        public string Value { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
