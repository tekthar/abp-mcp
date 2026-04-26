using Volo.Abp;

namespace AbpMcp;

/// <summary>
/// Thrown at startup when abp-mcp detects a misconfiguration that would produce a broken server.
/// </summary>
public sealed class AbpMcpConfigurationException : AbpException
{
    /// <inheritdoc/>
    public AbpMcpConfigurationException(string message) : base(message) { }

    /// <inheritdoc/>
    public AbpMcpConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}
