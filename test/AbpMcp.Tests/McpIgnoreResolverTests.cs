using AbpMcp.Attributes;
using AbpMcp.Metadata;
using FluentAssertions;

namespace AbpMcp.Tests;

/// <summary>
/// Regression: <c>[McpIgnore]</c> placed on an app-service contract interface method was silently
/// ignored (attribute lookup on the concrete method never walks implemented interfaces), so with a
/// class-level <c>[McpTool]</c> the method the developer hid stayed exposed and callable, and
/// <c>_explain</c> reported it as included. Both now go through <see cref="McpIgnoreResolver"/>.
/// </summary>
public class McpIgnoreResolverTests
{
    [Fact]
    public void InterfaceDeclaredIgnoreHidesTheImplementingMethod()
    {
        var member = McpIgnoreResolver.FindIgnoringMember(
            typeof(HiddenService), typeof(HiddenService).GetMethod(nameof(HiddenService.Secret))!);

        member.Should().NotBeNull();
        member!.DeclaringType.Should().Be(typeof(IHiddenContract));
    }

    [Fact]
    public void InterfaceDeclaredIgnoreHidesAnInheritedImplementation()
    {
        var member = McpIgnoreResolver.FindIgnoringMember(
            typeof(DerivedHiddenService), typeof(DerivedHiddenService).GetMethod(nameof(HiddenService.Secret))!);

        member.Should().NotBeNull();
    }

    [Fact]
    public void ConcreteDeclaredIgnoreStillWins()
    {
        var method = typeof(HiddenService).GetMethod(nameof(HiddenService.ConcreteHidden))!;

        McpIgnoreResolver.FindIgnoringMember(typeof(HiddenService), method).Should().BeSameAs(method);
    }

    [Fact]
    public void UnannotatedMethodIsNotIgnored()
    {
        McpIgnoreResolver.FindIgnoringMember(
            typeof(HiddenService), typeof(HiddenService).GetMethod(nameof(HiddenService.Visible))!)
            .Should().BeNull();
    }

    private interface IHiddenContract
    {
        [McpIgnore]
        void Secret();

        void Visible();
    }

    [McpTool]
    private class HiddenService : IHiddenContract
    {
        public void Secret() { }

        public void Visible() { }

        [McpIgnore]
        public void ConcreteHidden() { }
    }

    private sealed class DerivedHiddenService : HiddenService
    {
    }
}
