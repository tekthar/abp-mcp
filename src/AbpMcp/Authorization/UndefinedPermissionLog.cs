using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AbpMcp.Authorization;

/// <summary>
/// Warns about a tool that requires a permission or policy this host does not define, once per tool and name.
/// <c>tools/list</c> authorizes every tool on every call, so warning there each time would flood the log.
/// </summary>
internal sealed class UndefinedPermissionLog
{
    private readonly ConcurrentDictionary<(string Tool, string Permission), byte> _reported = new();
    private readonly ILogger<UndefinedPermissionLog> _logger;

    public UndefinedPermissionLog(ILogger<UndefinedPermissionLog> logger) => _logger = logger;

    /// <summary>Warn that <paramref name="tool"/> requires the undefined <paramref name="permission"/>, unless already warned.</summary>
    public void Report(string tool, string permission)
    {
        if (_reported.TryAdd((tool, permission), 0))
        {
            _logger.LogWarning(
                "abp-mcp tool {Tool} requires '{Permission}', which is not defined as a permission or policy on this server, " +
                "so every caller is refused the tool. Check the name, and that the module defining it is loaded in this host.",
                tool,
                permission);
        }
    }
}
