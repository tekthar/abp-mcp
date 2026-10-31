using Microsoft.Extensions.Options;
using Volo.Abp.Modularity;

namespace AbpMcp.Registration;

/// <summary>
/// Adds every ABP module assembly that declares <c>[McpTool]</c> services to
/// <see cref="AbpMcpOptions.ExposedAssemblies"/>, so a host only has to <c>[DependsOn]</c> the module.
/// Runs after all <c>Configure&lt;AbpMcpOptions&gt;</c> calls and skips assemblies already registered.
/// </summary>
internal sealed class AutoDiscoveredAssemblies : IPostConfigureOptions<AbpMcpOptions>
{
    private readonly IModuleContainer _modules;

    public AutoDiscoveredAssemblies(IModuleContainer modules) => _modules = modules;

    public void PostConfigure(string? name, AbpMcpOptions options)
    {
        if (!options.AutoDiscoverAssemblies)
        {
            return;
        }

        var candidates = _modules.Modules.SelectMany(m => m.AllAssemblies);
        foreach (var assembly in McpToolAssemblyScanner.FindToolAssemblies(candidates))
        {
            if (!options.ExposedAssemblies.Contains(assembly))
            {
                options.ExposedAssemblies.Create(assembly).IsAutoDiscovered = true;
            }
        }
    }
}
