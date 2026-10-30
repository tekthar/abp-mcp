using AbpMcp.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Features;
using Volo.Abp.GlobalFeatures;

namespace AbpMcp.Dispatch;

/// <summary>
/// The feature checks ABP's MVC filters apply to the parallel REST action: <c>[RequiresGlobalFeature]</c> on
/// the service and <c>[RequiresFeature]</c> on the service or method. ABP's interceptors only cover virtual
/// methods on a class proxy, and the MCP endpoint is outside the MVC pipeline, so without this a tool
/// call could reach a feature-gated method the REST endpoint refuses.
/// </summary>
internal static class AbpMcpFeatureGate
{
    /// <summary>Throws when the tool's service or method is gated by a feature that is not enabled.</summary>
    public static async Task CheckAsync(IServiceProvider services, ToolDescriptor descriptor)
    {
        if (descriptor.ServiceType is not null &&
            !GlobalFeatureHelper.IsGlobalFeatureEnabled(descriptor.ServiceType, out var globalFeature))
        {
            throw new AbpMcpToolException(
                "DISABLED",
                $"Tool '{descriptor.Name}' belongs to the disabled global feature '{globalFeature?.Name}'.");
        }

        if (descriptor.Method is not null)
        {
            // Throws AbpAuthorizationException (code Volo.Feature:010001) when a required feature is off.
            await services.GetRequiredService<IMethodInvocationFeatureCheckerService>()
                .CheckAsync(new MethodInvocationFeatureCheckerContext(descriptor.Method))
                .ConfigureAwait(false);
        }
    }
}
