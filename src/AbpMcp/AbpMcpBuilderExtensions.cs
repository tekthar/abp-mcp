using AbpMcp.Addins;
using AbpMcp.Authorization;
using AbpMcp.Diagnostics;
using AbpMcp.Dispatch;
using AbpMcp.Metadata;
using AbpMcp.Notifications;
using AbpMcp.Registration;
using AbpMcp.Settings;
using AbpMcp.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Settings;

namespace AbpMcp;

/// <summary>
/// Public entry points for wiring abp-mcp into an ABP host.
/// </summary>
public static class AbpMcpBuilderExtensions
{
    /// <summary>
    /// Registers abp-mcp services in the DI container.
    /// Call <see cref="MapAbpMcp"/> on the <see cref="IEndpointRouteBuilder"/> to mount the endpoint.
    /// </summary>
    public static IServiceCollection AddAbpMcp(
        this IServiceCollection services,
        Action<AbpMcpOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<AbpMcpOptions>();
        }

        services.TryAddSingleton<IApiDefinitionReader, ApiDefinitionReader>();
        services.TryAddSingleton<IToolDescriptorBuilder, ToolDescriptorBuilder>();
        services.TryAddSingleton<IDynamicMcpToolRegistry, DynamicMcpToolRegistry>();
        services.TryAddTransient<IAbpMcpToolAuthorizer, AbpMcpToolAuthorizer>();
        services.TryAddScoped<IAbpMcpDispatcher, AbpMcpDispatcher>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDisabledToolsProvider, DisabledToolsProvider>();

        // Assembly registration: discover [McpTool] assemblies from the ABP module graph, then make sure
        // every exposed assembly is registered with ConventionalControllers so ABP's api-definition sees it.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<AbpMcpOptions>, AutoDiscoveredAssemblies>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<AbpAspNetCoreMvcOptions>, ConventionalControllersBridge>());

        // The AbpMcp.DisabledTools setting definition. Registered explicitly rather than left to ABP's
        // convention scan, so it exists for hosts that call AddAbpMcp without AbpMcpModule too.
        services.TryAddTransient<AbpMcpSettingDefinitionProvider>();
        services.Configure<AbpSettingOptions>(opts =>
        {
            if (!opts.DefinitionProviders.Contains<AbpMcpSettingDefinitionProvider>())
            {
                opts.DefinitionProviders.Add<AbpMcpSettingDefinitionProvider>();
            }
        });
        services.AddHttpContextAccessor();

        // tools/list_changed support: track live sessions and expose the notifier hook so a host or
        // plugin can rebuild the tool set and tell connected agents to re-read it at runtime.
        services.TryAddSingleton<McpSessionRegistry>();
        services.TryAddSingleton<IAbpMcpToolListChangedNotifier, AbpMcpToolListChangedNotifier>();

        // Built-in add-ins. Registered via TryAddEnumerable so the set stays correct if AddAbpMcp
        // runs more than once, and so host- or module-supplied IAbpMcpAddin registrations sit
        // alongside these rather than replacing them.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAbpMcpAddin, XmlDocDescriptionAddin>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAbpMcpAddin, OutputSchemaAddin>());

        // Microsoft MCP SDK registration. The caller still must have ASP.NET Core wired up.
        // Stateful sessions are pinned explicitly: SDK 2.x defaults to stateless (MCP 2026-07-28,
        // SEP-2567), which has no session to push notifications/tools/list_changed over, so the
        // DisabledTools kill switch and IAbpMcpToolListChangedNotifier could no longer tell
        // connected agents to re-read the tool list.
        services.AddMcpServer().WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateful);

        // Bridge our descriptors to the SDK's tool handlers. This is the only place
        // we touch McpServerOptions; the rest of the codebase is SDK-agnostic.
        services.AddSingleton<IConfigureOptions<McpServerOptions>, AbpMcpHandlerWiring>();

        return services;
    }

    /// <summary>
    /// Mounts the MCP endpoint on the configured path, plus the diagnostic endpoints
    /// <c>{path}/_discover</c> and <c>{path}/_explain</c>. Call after <see cref="AddAbpMcp"/>.
    /// </summary>
    public static IEndpointConventionBuilder MapAbpMcp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<AbpMcpOptions>>()
            .Value;

        var convention = endpoints.MapMcp(options.Path);
        McpOriginGuard.Apply(convention, options);

        if (!options.AllowAnonymous)
        {
            convention.RequireAuthorization();
        }

        // Diagnostic endpoints follow the same auth and origin posture as the MCP endpoint itself.
        // _discover and _explain reveal the full tool surface, parameter schemas, and required
        // permissions, so leaving them anonymous in an otherwise-authed host would be a leak.
        AbpMcpDiagnosticsEndpoints.Map(endpoints, options.Path, requireAuthorization: !options.AllowAnonymous);

        return convention;
    }
}
