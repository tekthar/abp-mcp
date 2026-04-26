using AbpMcp.Registration;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Modularity;

namespace AbpMcp;

/// <summary>
/// ABP module that registers abp-mcp services.
/// Depends on <c>AbpAspNetCoreMvcModule</c> to ensure ABP's api-definition provider is available.
/// </summary>
/// <remarks>
/// Module ceremony mirrors ABP's own modules:
/// <list type="bullet">
///   <item><see cref="PreConfigureServices"/> registers the default options shape so other modules can
///         <c>Configure&lt;AbpMcpOptions&gt;(...)</c> in their <c>ConfigureServices</c> without race concerns.</item>
///   <item><see cref="ConfigureServices"/> wires the AbpMcp services into DI.</item>
///   <item><see cref="OnApplicationInitialization"/> warms the registry so misconfiguration fails at boot,
///         not on the first agent call.</item>
/// </list>
/// </remarks>
[DependsOn(typeof(AbpAspNetCoreMvcModule))]
public sealed class AbpMcpModule : AbpModule
{
    /// <inheritdoc/>
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        // Establish the options shape early so dependent modules can call Configure<AbpMcpOptions>
        // before our ConfigureServices runs. ABP-idiomatic ceremony.
        context.Services.AddOptions<AbpMcpOptions>();
    }

    /// <inheritdoc/>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpMcp();
    }

    /// <inheritdoc/>
    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var registry = context.ServiceProvider.GetRequiredService<IDynamicMcpToolRegistry>();
        registry.Initialize();
    }
}
