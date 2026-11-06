using System.Text.Json.Nodes;
using AbpMcp.Dispatch;
using AbpMcp.Metadata;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.Data;

namespace AbpMcp.Tests;

/// <summary>
/// A lost optimistic-concurrency race maps to <c>CONFLICT</c> with a fixed recovery hint, not to
/// <c>INTERNAL</c>, so an agent knows that re-reading the record and retrying will succeed (#97).
/// </summary>
public class ExceptionMapperConflictTests
{
    private static readonly ToolDescriptor Descriptor = new()
    {
        Name = "Book_Update",
        Description = "Updates a book.",
        InputSchema = new JsonObject { ["type"] = "object" },
        ParameterNames = [],
        RequiredPermissions = [],
    };

    [Fact]
    public void AbpDbConcurrencyException_MapsToConflict_WithTheFixedMessage()
    {
        var ex = new AbpDbConcurrencyException("Database operation expected to affect 1 row(s) but actually affected 0 row(s) in table AppBooks.");

        var mapped = AbpMcpExceptionMapper.Map(Descriptor, NullLogger.Instance, ex);

        mapped.Code.Should().Be(AbpMcpErrorCodes.Conflict);
        mapped.Message.Should().Be(
            "The record was changed by someone else since it was read. Read it again and retry with the new values.");
        mapped.Message.Should().NotContain("AppBooks", because: "the exception's own message can name tables and columns");
        mapped.InnerException.Should().BeSameAs(ex);
    }
}
