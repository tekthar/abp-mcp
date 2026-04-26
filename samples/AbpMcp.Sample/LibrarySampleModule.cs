using AbpMcp.Sample.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;

namespace AbpMcp.Sample;

/// <summary>
/// Sample ABP module hosting the Library domain. Wires:
///   - Autofac as the DI container (ABP standard)
///   - the standard ABP MVC stack (so the api-definition provider is available to abp-mcp)
///   - abp-mcp itself, scoped to this assembly via <c>ExposedAssemblies</c>
///   - a deterministic in-memory <see cref="LibraryDbContext"/> seeded with classic titles
/// </summary>
[DependsOn(
    typeof(AbpAutofacModule),
    typeof(AbpAspNetCoreMvcModule),
    typeof(AbpMcpModule))]
public sealed class LibrarySampleModule : AbpModule
{
    private const string DemoDatabaseName = "abp-mcp-library-sample";

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;

        services.AddDbContext<LibraryDbContext>(opts => opts.UseInMemoryDatabase(DemoDatabaseName));

        // One call does both wirings: registers this assembly with ABP's
        // ConventionalControllers (so the api-definition provider can see its app services)
        // AND with abp-mcp's ExposedAssemblies filter (so the MCP scan scopes to it).
        services.AddAbpMcpAssembly(typeof(LibrarySampleModule).Assembly);

        Configure<AbpMcpOptions>(opts =>
        {
            opts.Path = "/mcp";
            opts.AllowAnonymous = true;            // sample only — production hosts inherit ABP auth
            opts.RequireAtLeastOneTool = true;     // we ship three [McpTool] services so this should hold
        });
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var app = context.GetApplicationBuilder();
        app.UseRouting();
        app.UseConfiguredEndpoints(endpoints =>
        {
            endpoints.MapGet("/", () =>
                "abp-mcp library sample.\n" +
                "  - MCP endpoint:        /mcp\n" +
                "  - Tool discovery:      /mcp/_discover\n" +
                "  - Exposure decisions:  /mcp/_explain\n" +
                "Try:  curl http://localhost:5000/mcp/_discover\n");

            endpoints.MapAbpMcp();
        });

        SeedAsync(context.ServiceProvider).GetAwaiter().GetResult();
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        await LibrarySeedData.EnsureSeededAsync(db).ConfigureAwait(false);
    }
}
