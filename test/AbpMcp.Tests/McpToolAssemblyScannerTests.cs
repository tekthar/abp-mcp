using AbpMcp.Attributes;
using AbpMcp.Registration;
using FluentAssertions;

namespace AbpMcp.Tests;

public class McpToolAssemblyScannerTests
{
    [Theory]
    [InlineData(typeof(ClassTagged), true)]
    [InlineData(typeof(MethodTagged), true)]
    [InlineData(typeof(InheritsTaggedBase), true)]
    [InlineData(typeof(InheritsTaggedMethod), true)]
    [InlineData(typeof(AbstractTagged), false)]
    [InlineData(typeof(Untagged), false)]
    [InlineData(typeof(ITaggedContract), false)]
    public void IsMcpToolService_MatchesTheReadersAttributeRules(Type type, bool expected) =>
        McpToolAssemblyScanner.IsMcpToolService(type).Should().Be(expected);

    [Fact]
    public void FindsAssemblyThatDeclaresMcpToolServices() =>
        McpToolAssemblyScanner.FindToolAssemblies([typeof(ClassTagged).Assembly, typeof(ClassTagged).Assembly])
            .Should().ContainSingle().Which.Should().BeSameAs(typeof(ClassTagged).Assembly);

    [Fact]
    public void SkipsAssembliesThatDoNotReferenceAbpMcp() =>
        McpToolAssemblyScanner.FindToolAssemblies([typeof(FactAttribute).Assembly, typeof(string).Assembly])
            .Should().BeEmpty();

    [Fact]
    public void SkipsAbpMcpItself() =>
        McpToolAssemblyScanner.ContainsMcpTools(typeof(McpToolAttribute).Assembly).Should().BeFalse();

    [McpTool]
    private class ClassTagged
    {
    }

    private sealed class MethodTagged
    {
        [McpTool]
        public void Run()
        {
        }
    }

    private sealed class InheritsTaggedBase : ClassTagged
    {
    }

    private class TaggedMethodBase
    {
        [McpTool]
        public virtual void Run()
        {
        }
    }

    private sealed class InheritsTaggedMethod : TaggedMethodBase
    {
        public override void Run()
        {
        }
    }

    [McpTool]
    private abstract class AbstractTagged
    {
    }

    private sealed class Untagged
    {
        public void Run()
        {
        }
    }

    // [McpTool] on an interface is not honored by the reader, so it must not trigger discovery either.
    private interface ITaggedContract
    {
        [McpTool]
        void Run();
    }
}
