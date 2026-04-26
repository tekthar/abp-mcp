using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.AspNetCore.Mvc;

namespace AbpMcp;

/// <summary>
/// Convenience extensions that fold the two-step assembly registration (ABP's
/// <see cref="AbpAspNetCoreMvcOptions.ConventionalControllers"/> plus abp-mcp's
/// <see cref="AbpMcpOptions.ExposedAssemblies"/>) into a single call.
/// </summary>
public static class AbpMcpServiceCollectionExtensions
{
    /// <summary>
    /// Registers <paramref name="assembly"/> for exposure through abp-mcp in one line:
    /// it is added to <see cref="AbpMcpOptions.ExposedAssemblies"/> so abp-mcp's reader
    /// scopes to it, AND to ABP's <see cref="AbpAspNetCoreMvcOptions.ConventionalControllers"/>
    /// so the api-definition pipeline actually surfaces its application services in the first
    /// place. The two-call form (<c>Configure&lt;AbpAspNetCoreMvcOptions&gt;</c> +
    /// <c>Configure&lt;AbpMcpOptions&gt;</c>) remains supported for hosts that need finer control.
    /// </summary>
    /// <example>
    /// <code>
    /// public override void ConfigureServices(ServiceConfigurationContext context)
    /// {
    ///     context.Services.AddAbpMcpAssembly(typeof(MyAppApplicationModule).Assembly);
    /// }
    /// </code>
    /// </example>
    public static IServiceCollection AddAbpMcpAssembly(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        services.Configure<AbpAspNetCoreMvcOptions>(opts =>
        {
            opts.ConventionalControllers.Create(assembly);
        });

        services.Configure<AbpMcpOptions>(opts =>
        {
            opts.ExposedAssemblies.Create(assembly);
        });

        return services;
    }
}
