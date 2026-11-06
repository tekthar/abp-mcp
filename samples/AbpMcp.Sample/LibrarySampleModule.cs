using AbpMcp.Addins;
using AbpMcp.Sample.Auth;
using AbpMcp.Sample.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.AspNetCore.Authentication.JwtBearer;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;

namespace AbpMcp.Sample;

/// <summary>
/// Sample ABP module hosting the Library domain. Wires:
///   - Autofac as the DI container (ABP standard)
///   - the standard ABP MVC stack (so the api-definition provider is available to abp-mcp)
///   - abp-mcp itself, which auto-discovers this assembly's <c>[McpTool]</c> services
///   - a deterministic in-memory <see cref="LibraryDbContext"/> seeded with classic titles
/// </summary>
[DependsOn(
    typeof(AbpAutofacModule),
    typeof(AbpAspNetCoreMvcModule),
    typeof(AbpAspNetCoreAuthenticationJwtBearerModule),
    typeof(AbpMcpModule))]
public sealed class LibrarySampleModule : AbpModule
{
    private const string DemoDatabaseName = "abp-mcp-library-sample";

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;

        services.AddDbContext<LibraryDbContext>(opts => opts.UseInMemoryDatabase(DemoDatabaseName));

        // No assembly registration needed: abp-mcp finds this module's [McpTool] services in the
        // ABP module graph and registers the assembly with ConventionalControllers itself.

        // A dynamic add-in that contributes a tool not backed by any single application service.
        // This is the extension seam: register any number of IAbpMcpAddin to grow the MCP surface.
        services.AddSingleton<IAbpMcpAddin, LibraryInsightsAddin>();

        // Grants for Library.Members.Delete come from configuration (Sample:Auth:Grants), through ABP's own
        // permission checker. The anonymous sample has no grants, so Member_Delete stays hidden there.
        services.AddSingleton<IPermissionStore, SamplePermissionStore>();

        // Sample:Auth:Issuer switches the sample to authenticated, set up as docs/oauth.md describes.
        var auth = context.Services.GetConfiguration().GetSection(SampleAuthOptions.Section).Get<SampleAuthOptions>() ?? new SampleAuthOptions();
        Configure<SampleAuthOptions>(context.Services.GetConfiguration().GetSection(SampleAuthOptions.Section));
        if (auth.Enabled)
        {
            SampleAuthentication.Configure(services, auth);
        }

        Configure<AbpMcpOptions>(opts =>
        {
            opts.Path = "/mcp";
            opts.AllowAnonymous = !auth.Enabled;   // the default sample is anonymous; production hosts inherit ABP auth
            opts.AuthorizationPolicy = auth.Enabled ? SampleAuthentication.PolicyName : null;
            opts.RequireAtLeastOneTool = true;     // we ship three [McpTool] services so this should hold
        });
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var app = context.GetApplicationBuilder();
        app.UseRouting();

        // The standard ABP host pipeline. The sample allows anonymous MCP calls, so these are no-ops here,
        // but a host that turns AllowAnonymous off (or sets AbpMcpOptions.AuthorizationPolicy) needs them.
        app.UseAuthentication();
        app.UseAuthorization();

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
