using Microsoft.Extensions.Options;
using Volo.Abp.AspNetCore.Mvc;

namespace AbpMcp.Registration;

/// <summary>
/// Registers every <see cref="AbpMcpOptions.ExposedAssemblies"/> entry with ABP's
/// <see cref="AbpAspNetCoreMvcOptions.ConventionalControllers"/>, which is what makes ABP's
/// api-definition surface an assembly's app services. Without it,
/// <c>Configure&lt;AbpMcpOptions&gt;(o =&gt; o.ExposedAssemblies.Create(asm))</c> alone silently yields no tools.
/// </summary>
/// <remarks>
/// ABP reads <c>ConventionalControllers</c> when the application initializes, after every options
/// action has run, so a post-configure step is early enough. An assembly the host already registered
/// is left alone, keeping its <c>ConventionalControllers.Create(asm, settings =&gt; ...)</c> settings.
/// </remarks>
internal sealed class ConventionalControllersBridge : IPostConfigureOptions<AbpAspNetCoreMvcOptions>
{
    private readonly IOptions<AbpMcpOptions> _mcpOptions;

    public ConventionalControllersBridge(IOptions<AbpMcpOptions> mcpOptions) => _mcpOptions = mcpOptions;

    public void PostConfigure(string? name, AbpAspNetCoreMvcOptions options)
    {
        var registered = options.ConventionalControllers.ConventionalControllerSettings
            .Select(s => s.Assembly)
            .ToHashSet();

        // Distinct: the host may have registered the same assembly with ExposedAssemblies more than once.
        var unregistered = _mcpOptions.Value.ExposedAssemblies
            .Select(e => e.Assembly)
            .Where(assembly => !registered.Contains(assembly))
            .Distinct()
            .ToList();

        foreach (var assembly in unregistered)
        {
            options.ConventionalControllers.Create(assembly);
        }
    }
}
