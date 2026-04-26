using AbpMcp.Diagnostics;
using AbpMcp.Dispatch;
using AbpMcp.Metadata;
using AbpMcp.Registration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

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
        services.TryAddScoped<IAbpMcpDispatcher, AbpMcpDispatcher>();
        services.AddHttpContextAccessor();

        // Microsoft MCP SDK registration. The caller still must have ASP.NET Core wired up.
        services.AddMcpServer().WithHttpTransport();

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

        if (!options.AllowAnonymous)
        {
            convention.RequireAuthorization();
        }

        // Diagnostic endpoints. Always require authentication (or follow AllowAnonymous)
        // because exposure decisions and parameter schemas reveal application structure.
        AbpMcpDiagnosticsEndpoints.Map(endpoints, options.Path);

        return convention;
    }
}
