using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace AbpMcp.Transport;

/// <summary>
/// Enforces <see cref="AbpMcpOptions.AllowedOrigins"/> on the endpoints <c>MapAbpMcp</c> mounts. The MCP
/// transport spec requires servers to validate <c>Origin</c> and answer an invalid one with 403; without
/// it, any web page the user visits can reach a localhost or intranet MCP server through their browser.
/// </summary>
internal static class McpOriginGuard
{
    private const string AnyOrigin = "*";

    /// <summary>Wrap every endpoint built by <paramref name="builder"/> with the origin check.</summary>
    public static void Apply(IEndpointConventionBuilder builder, AbpMcpOptions options)
    {
        builder.Add(endpoint =>
        {
            var next = endpoint.RequestDelegate;
            if (next is null)
            {
                return;
            }

            endpoint.RequestDelegate = context => IsAllowed(context.Request, options)
                ? next(context)
                : RejectAsync(context);
        });
    }

    /// <summary>
    /// True for requests without an <c>Origin</c> (non-browser clients) and for listed origins. The request's
    /// own host is deliberately not trusted: under DNS rebinding the attacker's Origin and Host match.
    /// </summary>
    internal static bool IsAllowed(HttpRequest request, AbpMcpOptions options)
    {
        var origin = request.Headers[HeaderNames.Origin].ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        return options.AllowedOrigins.Contains(AnyOrigin) || options.AllowedOrigins.Contains(origin.TrimEnd('/'));
    }

    private static Task RejectAsync(HttpContext context)
    {
        // The spec allows a JSON-RPC error with no id as the 403 body; it tells a client why it was refused.
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(
            """{"jsonrpc":"2.0","error":{"code":-32600,"message":"Origin not allowed. Add it to AbpMcpOptions.AllowedOrigins."}}""");
    }
}
