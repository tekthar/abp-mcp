using System.Reflection;
using System.Text.Json.Nodes;
using AbpMcp.Sample;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Volo.Abp.AspNetCore.Mvc;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// #55: the Library sample registers no assembly, so its tools exist only because abp-mcp found
/// <c>[McpTool]</c> services in the ABP module graph. Also covers the explicit paths that must keep
/// working alongside it, including lambda-only <c>ExposedAssemblies.Create</c> (#9).
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class AssemblyRegistrationTests
{
    private static readonly Assembly SampleAssembly = typeof(LibrarySampleModule).Assembly;

    [Fact]
    public async Task ZeroConfig_DiscoversSampleAssembly_AndRegistersItOnce()
    {
        await using var host = await SampleHost.StartAsync();
        await using var client = await host.ConnectAsync();

        (await client.ListToolsAsync()).Select(t => t.Name).Should().Contain(["Member_List", "Loan_CheckOut"]);
        ConventionalSettingsFor(host).Should().ContainSingle();

        var explain = await ExplainAsync(host);
        Names(explain["auto_discovered_assemblies"]).Should().Equal("AbpMcp.Sample");
    }

    [Fact]
    public async Task AutoDiscoveryOff_AndNothingRegistered_ExposesNoServiceTools()
    {
        // The pre-#55 trap: nothing tells ABP about the assembly, so none of its services become tools.
        // Only the sample's dynamic add-in tool, which needs no assembly, remains.
        await using var host = await SampleHost.StartAsync(opts => opts.AutoDiscoverAssemblies = false);
        await using var client = await host.ConnectAsync();

        (await client.ListToolsAsync()).Select(t => t.Name).Should().Equal("Library_Overview");
        ConventionalSettingsFor(host).Should().BeEmpty();
    }

    [Fact]
    public async Task AutoDiscoveryOff_LambdaOnlyRegistration_IsEnough()
    {
        await using var host = await SampleHost.StartAsync(opts =>
        {
            opts.AutoDiscoverAssemblies = false;
            opts.ExposedAssemblies.Create(SampleAssembly);
        });
        await using var client = await host.ConnectAsync();

        (await client.ListToolsAsync()).Select(t => t.Name).Should().Contain("Member_List");
        ConventionalSettingsFor(host).Should().ContainSingle();
        Names((await ExplainAsync(host))["auto_discovered_assemblies"]).Should().BeEmpty();
    }

    [Fact]
    public async Task HostConventionalControllerSettings_ArePreserved()
    {
        await using var host = await SampleHost.StartAsync(configureBuilder: builder =>
            builder.Services.Configure<AbpAspNetCoreMvcOptions>(o =>
                o.ConventionalControllers.Create(SampleAssembly, s => s.RootPath = "library")));
        await using var client = await host.ConnectAsync();

        ConventionalSettingsFor(host).Should().ContainSingle().Which.RootPath.Should().Be("library");
        (await client.ListToolsAsync()).Select(t => t.Name).Should().Contain("Member_List");
    }

    [Fact]
    public async Task ExplicitAddAbpMcpAssembly_IsNotMarkedAutoDiscovered_OrRegisteredTwice()
    {
        await using var host = await SampleHost.StartAsync(configureBuilder: builder =>
            builder.Services.AddAbpMcpAssembly(SampleAssembly));

        ConventionalSettingsFor(host).Should().ContainSingle();
        var explain = await ExplainAsync(host);
        Names(explain["exposed_assemblies"]).Should().Equal("AbpMcp.Sample");
        Names(explain["auto_discovered_assemblies"]).Should().BeEmpty();
    }

    private static IEnumerable<Volo.Abp.AspNetCore.Mvc.Conventions.ConventionalControllerSetting> ConventionalSettingsFor(SampleHost host) =>
        host.Services.GetRequiredService<IOptions<AbpAspNetCoreMvcOptions>>().Value
            .ConventionalControllers.ConventionalControllerSettings
            .Where(s => s.Assembly == SampleAssembly);

    private static async Task<JsonNode> ExplainAsync(SampleHost host)
    {
        using var http = host.Server.CreateClient();
        return JsonNode.Parse(await http.GetStringAsync("/mcp/_explain"))!;
    }

    private static IEnumerable<string> Names(JsonNode? array) =>
        array!.AsArray().Select(n => n!.GetValue<string>());
}
