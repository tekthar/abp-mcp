using System.Text.Json;
using AbpMcp.Dispatch;
using AbpMcp.IntegrationTests.Books;
using AbpMcp.Registration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AbpMcp.IntegrationTests.Tools;

/// <summary>
/// Exercises the dynamic add-in pipeline end to end: a registered <c>IAbpMcpAddin</c> contributes a
/// handler-backed tool and enriches a discovered one, and the built-in output-schema add-in advertises
/// return schemas. All observed through the same registry + dispatcher an agent drives.
/// </summary>
public sealed class AddinPipelineTests : AbpMcpIntegrationTestBase
{
    [Fact]
    public void DynamicTool_ContributedByAddin_IsDiscovered()
    {
        var registry = ServiceProvider.GetRequiredService<IDynamicMcpToolRegistry>();
        registry.Initialize();

        registry.Tools.Select(t => t.Name).Should().Contain("Test_Greet");
    }

    [Fact]
    public async Task DynamicTool_InvokesThroughDispatcher_WithRequestScopedDi()
    {
        await RunAsAuthenticatedAsync(async services =>
        {
            // Seed two books; the handler resolves BookDbContext from the request scope and counts them.
            var db = services.GetRequiredService<BookDbContext>();
            db.Books.AddRange(
                new Book { Id = Guid.NewGuid(), Title = "A", Author = "x", Year = 2001 },
                new Book { Id = Guid.NewGuid(), Title = "B", Author = "y", Year = 2002 });
            await db.SaveChangesAsync();

            var dispatcher = services.GetRequiredService<IAbpMcpDispatcher>();
            var registry = services.GetRequiredService<IDynamicMcpToolRegistry>();
            registry.Initialize();
            registry.TryGetByName("Test_Greet", out var descriptor).Should().BeTrue();

            using var args = JsonDocument.Parse("""{ "name": "World" }""");
            var result = await dispatcher.InvokeAsync(descriptor!, args.RootElement, CancellationToken.None);

            result.GetProperty("greeting").GetString().Should().Be("Hello, World");
            result.GetProperty("knownBooks").GetInt32().Should().Be(2);
            return true;
        });
    }

    [Fact]
    public void Addin_EnrichesDiscoveredToolDescription()
    {
        var registry = ServiceProvider.GetRequiredService<IDynamicMcpToolRegistry>();
        registry.Initialize();

        registry.TryGetByName("Book_GetList", out var descriptor).Should().BeTrue();
        descriptor!.Description.Should().StartWith("ENRICHED:");
    }

    [Fact]
    public void OutputSchemaAddin_AdvertisesReturnSchema_ForServiceTool()
    {
        var registry = ServiceProvider.GetRequiredService<IDynamicMcpToolRegistry>();
        registry.Initialize();

        registry.TryGetByName("Book_Create", out var create).Should().BeTrue();
        create!.OutputSchema.Should().NotBeNull();

        // BookDto is walked: the enum surfaces as string names in the output schema too.
        var props = create.OutputSchema!["properties"]!.AsObject();
        props.Should().ContainKey("title");
        props["genre"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()).Should().Contain("Fiction");
    }

    [Fact]
    public void OutputSchemaAddin_SkipsDynamicToolWithoutMethod()
    {
        var registry = ServiceProvider.GetRequiredService<IDynamicMcpToolRegistry>();
        registry.Initialize();

        registry.TryGetByName("Test_Greet", out var greet).Should().BeTrue();
        greet!.OutputSchema.Should().BeNull();
    }

    [Fact]
    public void XmlDocAddin_FillsDescriptionAndParamFromContractDocs()
    {
        // IBookAppService.CreateAsync carries <summary>/<param> XML docs; the add-in surfaces them.
        var registry = ServiceProvider.GetRequiredService<IDynamicMcpToolRegistry>();
        registry.Initialize();

        registry.TryGetByName("Book_Create", out var create).Should().BeTrue();
        create!.Description.Should().Be("Creates a new book in the catalog.");

        var input = create.InputSchema["properties"]!["input"]!.AsObject();
        input["description"]!.GetValue<string>().Should().Be("The book to create.");
    }
}
