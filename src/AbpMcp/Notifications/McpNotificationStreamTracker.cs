using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace AbpMcp.Notifications;

/// <summary>
/// Keeps <see cref="McpSessionRegistry"/> in step with the notification streams clients hold open on the
/// endpoints <c>MapAbpMcp</c> mounts: a session is registered when the SDK accepts its GET stream and
/// unregistered when that request ends.
/// </summary>
/// <remarks>
/// Once the SDK has validated a request's session id and caller, it publishes the session's
/// <see cref="McpServer"/> as a request feature. The tracker reads it when the stream's <c>200</c> response
/// starts, which is before the client sees the stream open, so a notification sent after that point reaches it.
/// </remarks>
internal static class McpNotificationStreamTracker
{
    /// <summary>Wrap every endpoint built by <paramref name="builder"/> with the stream tracking.</summary>
    public static void Apply(IEndpointConventionBuilder builder, McpSessionRegistry registry)
    {
        builder.Add(endpoint =>
        {
            var next = endpoint.RequestDelegate;
            if (next is null)
            {
                return;
            }

            endpoint.RequestDelegate = context => HttpMethods.IsGet(context.Request.Method)
                ? TrackAsync(context, next, registry)
                : next(context);
        });
    }

    private static async Task TrackAsync(HttpContext context, RequestDelegate next, McpSessionRegistry registry)
    {
        var stream = new StreamRegistration(registry);
        context.Response.OnStarting(() =>
        {
            if (context.Response.StatusCode == StatusCodes.Status200OK && context.Features.Get<McpServer>() is { } server)
            {
                stream.Open(server);
            }

            return Task.CompletedTask;
        });

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            stream.Close();
        }
    }

    /// <summary>
    /// One GET request's registration. <see cref="Close"/> also covers a response that only starts after the
    /// request delegate returned: the stream is over by then, so it is never registered.
    /// </summary>
    private sealed class StreamRegistration(McpSessionRegistry registry)
    {
        private readonly object _gate = new();
        private IDisposable? _registration;
        private bool _closed;

        public void Open(McpServer server)
        {
            lock (_gate)
            {
                if (!_closed && _registration is null)
                {
                    _registration = registry.Add(server);
                }
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                _registration?.Dispose();
                _registration = null;
            }
        }
    }
}
