using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Dispatch;
using AbpMcp.Metadata;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Entities;

namespace AbpMcp.IntegrationTests.Tools;

/// <summary>
/// Exceptions raised inside a tool reach the agent through the mapping table, never as INTERNAL when
/// ABP gives them a specific meaning (#51).
/// </summary>
public sealed class ErrorMappingTests : AbpMcpIntegrationTestBase
{
    private sealed class Widget;

    [Fact]
    public async Task EntityNotFound_MapsToNotFound_WithEntityNameAndId()
    {
        var id = Guid.NewGuid();
        var error = await InvokeThrowingAsync(new EntityNotFoundException(typeof(Widget), id));

        error.Code.Should().Be("NOT_FOUND");
        error.Message.Should().Be($"There is no Widget with id = {id}.");
    }

    [Fact]
    public async Task CodedAuthorizationFailure_NamesTheCode_InsteadOfTheClrDefaultMessage()
    {
        var error = await InvokeThrowingAsync(
            new AbpAuthorizationException(code: "Volo.Feature:010001").WithData("FeatureNames", "Library.Premium"));

        error.Code.Should().Be("FORBIDDEN");
        error.Message.Should().Be("Not authorized (Volo.Feature:010001): FeatureNames=Library.Premium.");
    }

    private Task<AbpMcpToolException> InvokeThrowingAsync(Exception toThrow) =>
        RunAsAuthenticatedAsync(async services =>
        {
            var descriptor = new ToolDescriptor
            {
                Name = "Throwing_Tool",
                Description = "Throws the exception under test.",
                InputSchema = new JsonObject { ["type"] = "object" },
                ParameterNames = [],
                RequiredPermissions = [],
                Handler = (_, _) => throw toThrow,
            };

            var dispatcher = services.GetRequiredService<IAbpMcpDispatcher>();
            var act = () => dispatcher.InvokeAsync(descriptor, JsonDocument.Parse("{}").RootElement, CancellationToken.None);
            return (await act.Should().ThrowAsync<AbpMcpToolException>()).Which;
        });
}
