using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AbpMcp.Settings;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using Volo.Abp.Settings;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// #13 Stage B: the <see cref="AbpMcpSettingNames.DisabledTools"/> ABP setting disables tools for
/// <c>tools/list</c>, <c>tools/call</c> and <c>/mcp/_discover</c> without a restart. The global value
/// is stored through <see cref="ISettingStore"/>, the seam ABP Setting Management fills in production,
/// so this is the path a value set once and shared by every instance takes.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class DisabledToolsSettingTests : IAsyncLifetime
{
    private const string Tool = "Member_List";

    private readonly InMemoryGlobalSettingStore _store = new();
    private SampleHost _host = null!;

    public async Task InitializeAsync() => _host = await SampleHost.StartAsync(
        // No local cache, so the test sees each change on the next request.
        opts => opts.DisabledToolsSettingCacheDuration = TimeSpan.Zero,
        builder => builder.Services.Replace(ServiceDescriptor.Singleton<ISettingStore>(_store)));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task GlobalSetting_DisablesAndReenablesToolWithoutRestart()
    {
        await using var client = await _host.ConnectAsync();
        (await client.ListToolsAsync()).Select(t => t.Name).Should().Contain(Tool);

        _store.SetGlobal(AbpMcpSettingNames.DisabledTools, $"Loan_CheckOut, {Tool}");

        (await client.ListToolsAsync()).Select(t => t.Name).Should().NotContain([Tool, "Loan_CheckOut"]);
        var call = await client.CallToolAsync(Tool);
        call.IsError.Should().BeTrue();
        ((TextContentBlock)call.Content[0]).Text.Should().StartWith("[DISABLED]");

        _store.SetGlobal(AbpMcpSettingNames.DisabledTools, null);

        (await client.ListToolsAsync()).Select(t => t.Name).Should().Contain(Tool);
        (await client.CallToolAsync(Tool)).IsError.Should().NotBe(true);
    }

    [Fact]
    public async Task GlobalSetting_IsFlaggedInDiscover()
    {
        _store.SetGlobal(AbpMcpSettingNames.DisabledTools, Tool);
        using var http = _host.Server.CreateClient();

        var body = JsonNode.Parse(await http.GetStringAsync("/mcp/_discover"))!;

        var row = body["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == Tool)!;
        row["disabled"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task TenantValueIsIgnored_TheKillSwitchIsGlobal()
    {
        // The sample runs host-side (no current tenant), which ABP stores under a null provider key.
        _store.Set(AbpMcpSettingNames.DisabledTools, TenantSettingValueProvider.ProviderName, providerKey: null, Tool);
        await using var client = await _host.ConnectAsync();

        (await client.ListToolsAsync()).Select(t => t.Name).Should().Contain(Tool);
    }

    /// <summary>
    /// Stand-in for ABP Setting Management's store: values keyed by setting name, provider name and key.
    /// </summary>
    private sealed class InMemoryGlobalSettingStore : ISettingStore
    {
        private readonly ConcurrentDictionary<(string Name, string? Provider, string? Key), string> _values = new();

        public void SetGlobal(string name, string? value) =>
            Set(name, GlobalSettingValueProvider.ProviderName, providerKey: null, value);

        public void Set(string name, string providerName, string? providerKey, string? value)
        {
            if (value is null)
            {
                _values.TryRemove((name, providerName, providerKey), out _);
            }
            else
            {
                _values[(name, providerName, providerKey)] = value;
            }
        }

        public Task<string?> GetOrNullAsync(string name, string? providerName, string? providerKey) =>
            Task.FromResult(_values.TryGetValue((name, providerName, providerKey), out var value) ? value : null);

        public Task<List<SettingValue>> GetAllAsync(string[] names, string? providerName, string? providerKey) =>
            Task.FromResult(names
                .Select(n => new SettingValue(n, _values.TryGetValue((n, providerName, providerKey), out var v) ? v : null))
                .ToList());
    }
}

/// <summary>
/// The setting can also come from configuration (<c>Settings:AbpMcp.DisabledTools</c>), so a host
/// without Setting Management can still disable tools from <c>appsettings.json</c>.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class DisabledToolsConfigurationTests : IAsyncLifetime
{
    private SampleHost _host = null!;

    public async Task InitializeAsync() => _host = await SampleHost.StartAsync(
        configureBuilder: builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Settings:" + AbpMcpSettingNames.DisabledTools] = "Member_List",
        }));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ConfigurationValue_HidesTool()
    {
        await using var client = await _host.ConnectAsync();

        (await client.ListToolsAsync()).Select(t => t.Name).Should().NotContain("Member_List").And.Contain("Member_Get");
    }
}
