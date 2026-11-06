using System.Runtime.CompilerServices;
using AbpMcp.Attributes;

// The tagging attributes moved to AbpMcp.Abstractions (#98). Forward them so assemblies compiled
// against AbpMcp 0.4.x, which recorded the attributes as living in AbpMcp.dll, still resolve them.
[assembly: TypeForwardedTo(typeof(McpToolAttribute))]
[assembly: TypeForwardedTo(typeof(McpIgnoreAttribute))]
