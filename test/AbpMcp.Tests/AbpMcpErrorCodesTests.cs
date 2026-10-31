using AbpMcp.Dispatch;
using FluentAssertions;

namespace AbpMcp.Tests;

/// <summary>
/// The codes are a wire contract agents branch on. Pin each value here with a literal, so renaming or
/// "tidying" a constant fails loudly instead of silently changing what clients receive.
/// </summary>
public class AbpMcpErrorCodesTests
{
    [Fact]
    public void WireValuesAreStable()
    {
        AbpMcpErrorCodes.ValidationError.Should().Be("VALIDATION_ERROR");
        AbpMcpErrorCodes.Forbidden.Should().Be("FORBIDDEN");
        AbpMcpErrorCodes.NotFound.Should().Be("NOT_FOUND");
        AbpMcpErrorCodes.UnknownTool.Should().Be("UNKNOWN_TOOL");
        AbpMcpErrorCodes.Disabled.Should().Be("DISABLED");
        AbpMcpErrorCodes.BusinessError.Should().Be("BUSINESS_ERROR");
        AbpMcpErrorCodes.Cancelled.Should().Be("CANCELLED");
        AbpMcpErrorCodes.Internal.Should().Be("INTERNAL");
    }

    [Fact]
    public void EveryCodeIsPinned()
    {
        // A new constant must be added to WireValuesAreStable too.
        typeof(AbpMcpErrorCodes).GetFields().Should().HaveCount(8);
    }
}
