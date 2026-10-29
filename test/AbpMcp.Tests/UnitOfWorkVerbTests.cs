using System.Text.Json.Nodes;
using AbpMcp.Dispatch;
using AbpMcp.Metadata;
using FluentAssertions;

namespace AbpMcp.Tests;

/// <summary>
/// Every tools/call arrives as a POST, so the dispatcher picks transactional vs non-transactional by
/// the parallel REST action's verb (ABP: GET is non-transactional), not by the MCP request's own.
/// </summary>
public class UnitOfWorkVerbTests
{
    [Theory]
    [InlineData("GET", nameof(Fixture.CreateAsync), true)]       // api-definition verb wins over the name
    [InlineData("POST", nameof(Fixture.GetListAsync), false)]
    [InlineData("get", nameof(Fixture.CreateAsync), true)]
    [InlineData(null, nameof(Fixture.GetListAsync), true)]        // no verb: ABP's Get* naming convention
    [InlineData(null, nameof(Fixture.CreateAsync), false)]
    [InlineData(null, nameof(Fixture.DeleteAsync), false)]
    public void ClassifiesReadsByTheRestVerb(string? httpMethod, string methodName, bool expectedRead)
    {
        AbpMcpDispatcher.IsReadAction(Descriptor(httpMethod, typeof(Fixture).GetMethod(methodName)))
            .Should().Be(expectedRead);
    }

    [Fact]
    public void DynamicToolWithoutVerbIsTreatedAsAWrite()
    {
        AbpMcpDispatcher.IsReadAction(Descriptor(httpMethod: null, method: null)).Should().BeFalse();
    }

    private static ToolDescriptor Descriptor(string? httpMethod, System.Reflection.MethodInfo? method) => new()
    {
        Name = "t",
        Description = "t",
        Method = method,
        HttpMethod = httpMethod,
        InputSchema = new JsonObject(),
        ParameterNames = Array.Empty<string>(),
        RequiredPermissions = Array.Empty<string>(),
    };

    private sealed class Fixture
    {
        public Task GetListAsync() => Task.CompletedTask;

        public Task CreateAsync() => Task.CompletedTask;

        public Task DeleteAsync() => Task.CompletedTask;
    }
}
