using AbpMcp.Settings;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Volo.Abp.Settings;

namespace AbpMcp.Tests;

public class DisabledToolsProviderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ,; \n")]
    public void Parse_EmptyValues_YieldNoTools(string? value) =>
        DisabledToolsProvider.Parse(value).Should().BeEmpty();

    [Fact]
    public void Parse_SplitsOnCommaSemicolonAndNewline_AndTrims() =>
        DisabledToolsProvider.Parse(" Loan_CheckOut, Member_Suspend;Catalog_AddTitle\r\nMember_List ")
            .Should().BeEquivalentTo("Loan_CheckOut", "Member_Suspend", "Catalog_AddTitle", "Member_List");

    [Fact]
    public void Parse_IsCaseSensitive() =>
        DisabledToolsProvider.Parse("Loan_CheckOut,loan_checkout").Should().HaveCount(2);

    [Fact]
    public async Task CombinesOptionsAndSetting()
    {
        var fixture = new Fixture(setting: "Loan_CheckOut");
        fixture.Options.DisabledTools.Add("Member_List");

        var disabled = await fixture.Provider.GetDisabledToolsAsync();

        disabled.Should().BeEquivalentTo("Loan_CheckOut", "Member_List");
    }

    [Fact]
    public async Task OptionsChangesApplyImmediately_EvenWhileSettingIsCached()
    {
        var fixture = new Fixture(setting: null);
        await fixture.Provider.GetDisabledToolsAsync();

        fixture.Options.DisabledTools.Add("Member_List");

        (await fixture.Provider.GetDisabledToolsAsync()).Should().Contain("Member_List");
    }

    [Fact]
    public async Task CachesSettingForConfiguredDuration_ThenRereads()
    {
        var fixture = new Fixture(setting: "A");
        fixture.Options.DisabledToolsSettingCacheDuration = TimeSpan.FromSeconds(10);

        (await fixture.Provider.GetDisabledToolsAsync()).Should().BeEquivalentTo("A");

        fixture.Setting = "B";
        fixture.Time.Advance(TimeSpan.FromSeconds(9));
        (await fixture.Provider.GetDisabledToolsAsync()).Should().BeEquivalentTo("A");

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        (await fixture.Provider.GetDisabledToolsAsync()).Should().BeEquivalentTo("B");

        await fixture.Settings.Received(2).GetOrNullAsync(AbpMcpSettingNames.DisabledTools);
    }

    [Fact]
    public async Task ZeroCacheDuration_ReadsSettingEveryTime()
    {
        var fixture = new Fixture(setting: "A");
        fixture.Options.DisabledToolsSettingCacheDuration = TimeSpan.Zero;

        await fixture.Provider.GetDisabledToolsAsync();
        fixture.Setting = "B";

        (await fixture.Provider.GetDisabledToolsAsync()).Should().BeEquivalentTo("B");
    }

    [Fact]
    public async Task SettingReadFailure_Propagates_SoTheRequestFailsClosed()
    {
        var fixture = new Fixture(setting: "A");
        fixture.Options.DisabledToolsSettingCacheDuration = TimeSpan.Zero;
        await fixture.Provider.GetDisabledToolsAsync();

        fixture.Settings.GetOrNullAsync(AbpMcpSettingNames.DisabledTools).ThrowsAsync(new InvalidOperationException("store down"));

        // Not served from the last known value: that could expose a tool disabled since it was read.
        var act = () => fixture.Provider.GetDisabledToolsAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SettingReadFailure_IsNotCached_NextRequestRereads()
    {
        var fixture = new Fixture(setting: null);
        fixture.Settings.GetOrNullAsync(AbpMcpSettingNames.DisabledTools).ThrowsAsync(new InvalidOperationException("store down"));
        var act = () => fixture.Provider.GetDisabledToolsAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();

        fixture.Settings.GetOrNullAsync(AbpMcpSettingNames.DisabledTools).Returns("A");

        (await fixture.Provider.GetDisabledToolsAsync()).Should().BeEquivalentTo("A");
    }

    private sealed class Fixture
    {
        public Fixture(string? setting)
        {
            Setting = setting;
            Settings.GetOrNullAsync(AbpMcpSettingNames.DisabledTools).Returns(_ => Setting);

            var services = new ServiceCollection().AddSingleton(Settings).BuildServiceProvider();
            var monitor = Substitute.For<IOptionsMonitor<AbpMcpOptions>>();
            monitor.CurrentValue.Returns(Options);

            Provider = new DisabledToolsProvider(
                services.GetRequiredService<IServiceScopeFactory>(),
                monitor,
                Time);
        }

        public string? Setting { get; set; }

        public ISettingProvider Settings { get; } = Substitute.For<ISettingProvider>();

        public AbpMcpOptions Options { get; } = new();

        public ManualTimeProvider Time { get; } = new();

        public DisabledToolsProvider Provider { get; }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
