using AbpMcp.Addins.XmlDocumentation;
using FluentAssertions;

namespace AbpMcp.Tests;

public class XmlDocumentationStoreTests
{
    private const string Xml = """
        <?xml version="1.0"?>
        <doc>
          <assembly><name>Test</name></assembly>
          <members>
            <member name="M:AbpMcp.Tests.XmlDocumentationStoreTests.Sample.DoThing(System.String)">
              <summary>
                Does a
                thing.
              </summary>
              <param name="value">The value to use.</param>
            </member>
            <member name="M:AbpMcp.Tests.XmlDocumentationStoreTests.IContract.Act">
              <summary>Acts on the contract.</summary>
            </member>
          </members>
        </doc>
        """;

    [Fact]
    public void ResolvesSummaryAndCollapsesWhitespace()
    {
        var store = XmlDocumentationStore.ParseXml(Xml);
        var method = typeof(Sample).GetMethod(nameof(Sample.DoThing))!;

        var doc = store.GetMethod(method);

        doc.Should().NotBeNull();
        doc!.Summary.Should().Be("Does a thing.");
        doc.Parameters["value"].Should().Be("The value to use.");
    }

    [Fact]
    public void ResolvesViaImplementedInterfaceMethod()
    {
        var store = XmlDocumentationStore.ParseXml(Xml);
        var implMethod = typeof(Impl).GetMethod(nameof(Impl.Act))!;

        store.GetMethod(implMethod)!.Summary.Should().Be("Acts on the contract.");
    }

    [Fact]
    public void UndocumentedMethodResolvesToNull()
    {
        var store = XmlDocumentationStore.ParseXml(Xml);

        store.GetMethod(typeof(Sample).GetMethod(nameof(Sample.Undocumented))!).Should().BeNull();
    }

    [Fact]
    public void EmptyStoreResolvesNothing() =>
        XmlDocumentationStore.Empty.GetMethod(typeof(Sample).GetMethod(nameof(Sample.DoThing))!).Should().BeNull();

    private sealed class Sample
    {
        public void DoThing(string value) { _ = value; }
        public void Undocumented() { }
    }

    private interface IContract
    {
        void Act();
    }

    private sealed class Impl : IContract
    {
        public void Act() { }
    }
}
