using System.Text.Json.Nodes;
using AbpMcp.Gateway;
using AbpMcp.Metadata;
using FluentAssertions;

namespace AbpMcp.Tests;

/// <summary>#119: the keyword search behind <c>mcp_search_tools</c>. No embeddings: tokens, field weights, coverage.</summary>
public class ToolSearchIndexTests
{
    private static readonly ToolDescriptor CheckOut = Tool("Loan_CheckOut", "Check out an edition to a member for a number of days.", typeof(Library.LoanAppService));
    private static readonly ToolDescriptor Overdue = Tool("Loan_ListOverdue", "List active loans that are past their due date.", typeof(Library.LoanAppService));
    private static readonly ToolDescriptor Suspend = Tool("Member_Suspend", "Suspend a member with a reason.", typeof(Library.MemberAppService));
    private static readonly ToolDescriptor Reinstate = Tool("Member_Reinstate", "Reinstate a suspended member so they can check out again.", typeof(Library.MemberAppService));
    private static readonly ToolDescriptor Overview = Tool("Library_Overview", "Aggregate catalog counts: titles, editions, members, and active loans.");

    private static readonly ToolSearchIndex Index = new([CheckOut, Overdue, Suspend, Reinstate, Overview]);

    [Fact]
    public void NameMatch_RanksAboveDescriptionMatch()
    {
        // "suspend" is in Member_Suspend's name and only in Member_Reinstate's description ("suspended").
        Names(Index.Search("suspend")).Should().StartWith(["Member_Suspend", "Member_Reinstate"]);
    }

    [Fact]
    public void ServiceMatch_RanksAboveDescriptionMatch()
    {
        // Neither name says "loan": Refund belongs to LoanAppService, Fees_Report only mentions loans in its description.
        var index = new ToolSearchIndex([
            Tool("Fees_Report", "Report the fees charged on every loan."),
            Tool("Refund", "Refund a fee.", typeof(Library.LoanAppService)),
        ]);

        Names(index.Search("loan")).Should().Equal("Refund", "Fees_Report");
    }

    [Fact]
    public void TwoTokenQuery_RequiresBothTokens()
    {
        Names(Index.Search("member suspend")).Should().StartWith("Member_Suspend")
            .And.NotContain("Loan_CheckOut", "it mentions a member but nothing about suspending");
        Index.Search("member banana").Should().BeEmpty();
    }

    [Fact]
    public void LongerQuery_RequiresSixtyPercentOfItsTokens()
    {
        // 3 tokens need 2: "member" and "suspend" match, "banana" does not.
        Names(Index.Search("member suspend banana")).Should().Contain("Member_Suspend");
        // Only 1 of 3.
        Index.Search("member banana cherry").Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    [InlineData(10, 6)]
    public void RequiredMatches_FollowsTheCoverageRule(int tokens, int required) =>
        ToolSearchIndex.RequiredMatches(tokens).Should().Be(required);

    [Theory]
    [InlineData("checkout")]
    [InlineData("check out")]
    [InlineData("CheckOut")]
    [InlineData("loan_check_out")]
    [InlineData("Loan.CheckOut")]
    public void CamelSnakeAndDottedForms_AllFindTheTool(string query) =>
        Names(Index.Search(query)).Should().StartWith("Loan_CheckOut");

    [Fact]
    public void ExactName_RanksFirst() => Names(Index.Search("Loan_ListOverdue")).First().Should().Be("Loan_ListOverdue");

    [Fact]
    public void Prefix_Matches() => Names(Index.Search("reinst")).Should().Equal("Member_Reinstate");

    [Fact]
    public void PluralsAndStopWords_DoNotGetInTheWay() =>
        Names(Index.Search("list the overdue loans")).Should().StartWith("Loan_ListOverdue");

    [Fact]
    public void QueryWithNoTokens_FindsNothing() => Index.Search("?? !").Should().BeEmpty();

    [Fact]
    public void Ties_AreOrderedByName()
    {
        var index = new ToolSearchIndex([Tool("B_Thing", "widget"), Tool("A_Thing", "widget")]);

        Names(index.Search("widget")).Should().Equal("A_Thing", "B_Thing");
    }

    [Fact]
    public void For_ReusesTheIndexOfTheSameToolSet_AndBuildsANewOneForARefreshedSet()
    {
        IReadOnlyList<ToolDescriptor> tools = [CheckOut];
        IReadOnlyList<ToolDescriptor> refreshed = [CheckOut, Suspend];

        ToolSearchIndex.For(tools).Should().BeSameAs(ToolSearchIndex.For(tools));
        Names(ToolSearchIndex.For(refreshed).Search("suspend")).Should().Equal("Member_Suspend");
    }

    private static IEnumerable<string> Names(IEnumerable<ToolSearchMatch> matches) => matches.Select(m => m.Tool.Name);

    private static ToolDescriptor Tool(string name, string description, Type? service = null) => new()
    {
        Name = name,
        Description = description,
        ServiceType = service,
        InputSchema = new JsonObject { ["type"] = "object" },
        ParameterNames = [],
        RequiredPermissions = [],
    };

    /// <summary>Stand-ins for the services the tools belong to; only their names are indexed.</summary>
    private static class Library
    {
        public sealed class LoanAppService;

        public sealed class MemberAppService;
    }
}

public class ToolSearchTokenizerTests
{
    [Theory]
    [InlineData("checkOut", new[] { "check", "Out" })]
    [InlineData("HTTPServer", new[] { "HTTP", "Server" })]
    [InlineData("getV2Items", new[] { "get", "V", "2", "Items" })]
    [InlineData("plain", new[] { "plain" })]
    public void SplitCase_SplitsCamelPascalAndDigits(string run, string[] parts) =>
        ToolSearchTokenizer.SplitCase(run).Should().Equal(parts);

    [Fact]
    public void FieldTokens_SplitOnSeparators_KeepJoinedRuns_AndDropStopWords() =>
        ToolSearchTokenizer.FieldTokens("Loan_CheckOut for the member.list v2")
            .Should().BeEquivalentTo(["loan", "check", "out", "checkout", "member", "list", "v2"]);

    [Theory]
    [InlineData("loans", "loan")]
    [InlineData("entries", "entry")]
    [InlineData("status", "status")]
    [InlineData("address", "address")]
    [InlineData("analysis", "analysis")]
    [InlineData("bus", "bus")]
    public void Stem_RemovesPlurals(string word, string stem) => ToolSearchTokenizer.Stem(word).Should().Be(stem);

    [Fact]
    public void QueryTokens_DropStopWords_UnlessNothingElseIsLeft()
    {
        ToolSearchTokenizer.QueryTokens("to be or not").Should().Equal("not");
        ToolSearchTokenizer.QueryTokens("to be").Should().Equal("to", "be");
    }
}
