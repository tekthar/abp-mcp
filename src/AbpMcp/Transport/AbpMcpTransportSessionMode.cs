using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

namespace AbpMcp.Transport;

/// <summary>
/// Sets the MCP SDK's transport session mode from <see cref="AbpMcpOptions.Stateless"/>: stateful by default
/// (SDK 2.x defaults to stateless, which cannot deliver <c>tools/list_changed</c>), stateless when the host
/// asks for it. Registered by <c>AddAbpMcp</c> as an ordinary <see cref="IConfigureOptions{TOptions}"/>, so a
/// host's own <c>Configure&lt;HttpServerTransportOptions&gt;</c> registered later still runs after it and wins.
/// </summary>
internal sealed class AbpMcpTransportSessionMode : IConfigureOptions<HttpServerTransportOptions>
{
    private readonly IOptions<AbpMcpOptions> _options;

    public AbpMcpTransportSessionMode(IOptions<AbpMcpOptions> options) => _options = options;

    public void Configure(HttpServerTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.SessionMode = _options.Value.Stateless
            ? HttpServerSessionMode.Stateless
            : HttpServerSessionMode.Stateful;
    }
}
