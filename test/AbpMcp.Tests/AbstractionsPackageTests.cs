using System.Reflection;
using System.Runtime.Loader;
using AbpMcp.Attributes;
using AbpMcp.Fixtures.Application;
using AbpMcp.Registration;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AbpMcp.Tests;

/// <summary>
/// #98: the tagging attributes live in the dependency-free AbpMcp.Abstractions, and AbpMcp type-forwards them
/// so code and binaries built against AbpMcp 0.4.x keep working.
/// </summary>
public class AbstractionsPackageTests
{
    private static readonly Assembly AbpMcpAssembly = typeof(AbpMcpModule).Assembly;

    [Theory]
    [InlineData(typeof(McpToolAttribute))]
    [InlineData(typeof(McpIgnoreAttribute))]
    public void Attribute_LivesInAbstractions_AndAbpMcpForwardsIt(Type attribute)
    {
        attribute.Assembly.GetName().Name.Should().Be("AbpMcp.Abstractions");
        attribute.Namespace.Should().Be("AbpMcp.Attributes", "the move must stay source compatible");

        AbpMcpAssembly.GetForwardedTypes().Should().Contain(attribute);
        AbpMcpAssembly.GetType(attribute.FullName!, throwOnError: true).Should().BeSameAs(attribute);
    }

    [Fact]
    public void Abstractions_ReferencesNothingButTheRuntime() =>
        typeof(McpToolAttribute).Assembly.GetReferencedAssemblies()
            .Select(r => r.Name)
            .Should().OnlyContain(name => name != null && name.StartsWith("System.", StringComparison.Ordinal),
                "AbpMcp.Abstractions must not pull ABP, ASP.NET Core or the MCP SDK into a *.Application project");

    [Fact]
    public void Scanner_FindsAnAssemblyThatReferencesOnlyAbstractions()
    {
        var fixture = typeof(QuoteAppService).Assembly;
        fixture.GetReferencedAssemblies().Select(r => r.Name).Should().NotContain("AbpMcp");

        McpToolAssemblyScanner.FindToolAssemblies([fixture]).Should().ContainSingle().Which.Should().BeSameAs(fixture);
    }

    [Fact]
    public void BinaryCompiledAgainstAbpMcp04_StillResolvesTheAttributes()
    {
        // Stand-in for AbpMcp 0.4.x: an assembly named AbpMcp that declares the attributes itself.
        var legacyAbpMcp = Compile("AbpMcp", """
            [assembly: System.Reflection.AssemblyVersion("0.4.0.0")]
            namespace AbpMcp.Attributes
            {
                public sealed class McpToolAttribute : System.Attribute { public string? Name { get; init; } }
                public sealed class McpIgnoreAttribute : System.Attribute { }
            }
            """);
        var consumer = Compile("LegacyToolConsumer", """
            namespace LegacyToolConsumer
            {
                [AbpMcp.Attributes.McpTool(Name = "legacy_tool")]
                public sealed class LegacyAppService
                {
                    [AbpMcp.Attributes.McpIgnore]
                    public void Hidden() { }
                }
            }
            """, MetadataReference.CreateFromImage(legacyAbpMcp));

        // Loaded the way a host loads it: the reference to "AbpMcp" binds to the real AbpMcp.dll,
        // which forwards both attributes to AbpMcp.Abstractions.
        var context = new AssemblyLoadContext("legacy-consumer", isCollectible: true);
        try
        {
            using var image = new MemoryStream(consumer);
            var service = context.LoadFromStream(image).GetType("LegacyToolConsumer.LegacyAppService", throwOnError: true)!;

            service.GetCustomAttribute<McpToolAttribute>()!.Name.Should().Be("legacy_tool");
            service.GetMethod("Hidden")!.IsDefined(typeof(McpIgnoreAttribute)).Should().BeTrue();
            McpToolAssemblyScanner.IsMcpToolService(service).Should().BeTrue();
        }
        finally
        {
            context.Unload();
        }
    }

    private static byte[] Compile(string assemblyName, string source, params MetadataReference[] extra)
    {
        var runtime = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) ||
                        Path.GetFileName(p) == "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p));

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            runtime.Concat(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine, result.Diagnostics));
        return output.ToArray();
    }
}
