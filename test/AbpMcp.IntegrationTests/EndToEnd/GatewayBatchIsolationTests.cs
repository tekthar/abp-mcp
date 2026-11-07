using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Addins;
using AbpMcp.Gateway;
using AbpMcp.Metadata;
using AbpMcp.Sample;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.Authorization;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// Regression: every <c>mcp_call_tools</c> call shared the HTTP request's DI scope, so a call that changed a
/// request-scoped <see cref="LibraryDbContext"/> and then refused had its change saved by the next call of a parallel
/// batch. A direct <c>tools/call</c> never did, because its scope ends with its request. Each gateway call now runs in
/// a scope of its own.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class GatewayBatchIsolationTests : IAsyncLifetime
{
    private const string RenameThenRefuse = "Fixture_RenameThenRefuse";
    private static readonly Guid DemoMember = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private SampleHost _host = null!;

    public async Task InitializeAsync() => _host = await SampleHost.StartAsync(
        o => o.ToolListing.OnDemand("Catalog_*"),
        builder => builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAbpMcpAddin, RenameThenRefuseAddin>()));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ARefusedCallsChange_IsNotSavedByTheNextCallOfAParallelBatch()
    {
        await using var client = await _host.ConnectAsync();
        var before = MemberName();
        var title = $"Isolation {Guid.NewGuid():N}";

        var result = await OnDemandToolsTests.GatewayAsync(client, AbpMcpGatewayToolNames.CallMany, new
        {
            calls = new object[]
            {
                new { name = RenameThenRefuse, arguments = new { name = "CHANGED-BY-BATCH" } },
                new
                {
                    name = "Catalog_AddTitle",
                    arguments = new { input = new { name = title, author = "Test Author", firstPublishedYear = 1999, genre = "Test" } },
                },
            },
            mode = "parallel",
        });

        result["results"]!.AsArray().Select(r => r!["status"]!.GetValue<string>()).Should().Equal("error", "ok");
        result["results"]![0]!["content"]![0]!["text"]!.GetValue<string>().Should().StartWith("[FORBIDDEN]");
        TitleExists(title).Should().BeTrue("the second call ran and saved its own change");
        MemberName().Should().Be(before, "the refused call's change stayed in its own scope and was never saved");
    }

    private string MemberName()
    {
        using var scope = _host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Members.Single(m => m.Id == DemoMember).Name;
    }

    private bool TitleExists(string name)
    {
        using var scope = _host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Titles.Any(t => t.Name == name);
    }

    /// <summary>
    /// A tool that changes tracked state through the request-scoped DbContext and then refuses, as an ownership check
    /// that runs after loading the entity would. Its change must never be saved.
    /// </summary>
    private sealed class RenameThenRefuseAddin : IAbpMcpAddin
    {
        public int Order => 50;

        public void Contribute(AbpMcpToolBuildContext context) => context.AddTool(new ToolDescriptor
        {
            Name = RenameThenRefuse,
            Description = "Renames the demo member, then refuses: the caller does not own it.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" } },
            },
            ParameterNames = ["name"],
            RequiredPermissions = Array.Empty<string>(),
            Handler = RenameThenRefuseAsync,
        });

        private static async Task<JsonElement> RenameThenRefuseAsync(AbpMcpToolInvocation invocation, CancellationToken cancellationToken)
        {
            var db = invocation.Services.GetRequiredService<LibraryDbContext>();
            var member = await db.Members.FindAsync([DemoMember], cancellationToken).ConfigureAwait(false);
            member!.Name = invocation.Arguments.GetProperty("name").GetString()!;
            throw new AbpAuthorizationException("Only the member's owner may rename them.");
        }
    }
}
