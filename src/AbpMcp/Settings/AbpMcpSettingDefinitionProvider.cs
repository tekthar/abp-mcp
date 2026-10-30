using Volo.Abp.Settings;

namespace AbpMcp.Settings;

/// <summary>
/// Defines <see cref="AbpMcpSettingNames.DisabledTools"/>. ABP discovers this provider by convention.
/// </summary>
/// <remarks>
/// The setting is limited to the global, configuration and default value providers. The kill switch
/// is global by design (per-tenant disable is out of scope), and keeping tenant and user values out
/// lets <see cref="DisabledToolsProvider"/> cache one value for every caller.
/// </remarks>
internal sealed class AbpMcpSettingDefinitionProvider : SettingDefinitionProvider
{
    public override void Define(ISettingDefinitionContext context)
    {
        context.Add(new SettingDefinition(AbpMcpSettingNames.DisabledTools)
            .WithProviders(
                DefaultValueSettingValueProvider.ProviderName,
                ConfigurationSettingValueProvider.ProviderName,
                GlobalSettingValueProvider.ProviderName));
    }
}
