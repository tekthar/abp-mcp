# abp-mcp

[![CI](https://github.com/tekthar/abp-mcp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/tekthar/abp-mcp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/AbpMcp.svg?logo=nuget)](https://www.nuget.org/packages/AbpMcp)
[![License: MIT](https://img.shields.io/github/license/tekthar/abp-mcp.svg)](LICENSE)

> Auto-generate a Model Context Protocol (MCP) server from your ABP Framework application.
> One NuGet, one line, and every `[McpTool]`-tagged Application Service is reachable by Claude, Cursor, and every MCP-compatible agent.
> In-process. Permission-aware. Multi-tenant aware.

**Status:** pre-alpha (v0.1). Phase 1 scaffolding in place. Not yet published to NuGet.

## Why

Every ABP app already declares its business logic as `IApplicationService` with typed DTOs, permission attributes, XML docs, and multi-tenancy awareness. That is richer metadata than any OpenAPI spec. Generic OpenAPI → MCP converters produce low-quality servers that confuse agents. `abp-mcp` skips the OpenAPI middleman entirely and generates from ABP's own API description pipeline — the same one that powers ABP's TS/C# proxy generators.

## Quickstart (once v0.1 ships)

Install the NuGet:

```bash
dotnet add package AbpMcp
```

Add the module and wire the endpoint in your ABP host's `Program.cs`:

```csharp
builder.Services.AddAbpMcp(opts =>
{
    opts.Path = "/mcp";
});

// ... build the app ...

app.MapAbpMcp();
```

Tag the services you want agent-accessible:

```csharp
[McpTool]
public class ProductAppService : ApplicationService, IProductAppService
{
    [Authorize("Products.Create")]
    public Task<ProductDto> CreateAsync(CreateProductDto input) => /* ... */;
}
```

Point Claude (or any MCP client) at `https://your-host/mcp` with a bearer token from your ABP identity server, and the agent can call every exposed tool with the same permissions as a regular user.

## Try the sample (60 seconds, zero setup)

The repo ships with a runnable ABP host that demonstrates the full surface against a small library domain (Titles, Editions, Members, Loans) seeded with classic books.

```bash
dotnet run --project samples/AbpMcp.Sample
```

Then in another terminal:

```bash
# What tools are live?
curl http://localhost:5000/mcp/_discover | jq '.tools[].name'
# → Catalog_SearchTitles, Catalog_GetTitle, Catalog_AddTitle,
#   Catalog_AddEdition, Catalog_ListAvailableEditions,
#   Loan_CheckOut, Loan_Return, Loan_Renew, Loan_ListForMember,
#   Loan_ListOverdue, Member_Register, Member_Get, Member_List,
#   Member_Suspend, Member_Reinstate

# Why isn't a particular service showing up?
curl 'http://localhost:5000/mcp/_explain?service=Loan'
```

Point Claude Desktop (or any MCP client) at `http://localhost:5000/mcp` and the
agent can search the catalog, register members, and check books out — every call
hitting a real `IApplicationService` and persisting through EF Core.

## Design

See [DESIGN.md](DESIGN.md) for the full design document:
- Problem statement and premises
- Approaches considered (reflection runtime, source generator, LLM-enhanced descriptions)
- Test plan
- Distribution plan

## Repository layout

```
abp-mcp/
├── src/
│   └── AbpMcp/                    # the library
│       ├── AbpMcpModule.cs         # ABP module
│       ├── AbpMcpBuilderExtensions.cs  # AddAbpMcp / MapAbpMcp
│       ├── AbpMcpOptions.cs
│       ├── Attributes/             # [McpTool], [McpIgnore]
│       ├── Metadata/               # ApiDefinitionReader, ToolDescriptorBuilder
│       ├── Registration/           # DynamicMcpToolRegistry
│       └── Dispatch/               # AbpMcpDispatcher
├── samples/
│   └── AbpMcp.Sample/              # runnable Library host (15 [McpTool] methods, seeded)
├── test/
│   ├── AbpMcp.Tests/               # unit tests (schema mapping, naming, options shape)
│   └── AbpMcp.IntegrationTests/    # seed-DB → invoke-tool → verify-DB integration tests
├── .github/
│   ├── workflows/                  # CI build+test, release-on-tag (signs + publishes)
│   ├── ISSUE_TEMPLATE/             # bug + feature forms
│   └── PULL_REQUEST_TEMPLATE.md
├── DESIGN.md                       # premises, alternatives, scope decisions
├── CHANGELOG.md                    # release notes
├── CONTRIBUTING.md                 # setup + design rules + PR process
├── SECURITY.md                     # private vulnerability reporting
├── CLAUDE.md                       # project guidance for agents & humans
├── LICENSE                         # MIT
└── README.md
```

## Contributing

Pre-alpha. Direct PRs welcome — please skim [CONTRIBUTING.md](CONTRIBUTING.md) before opening anything non-trivial. The non-negotiable design rules and the regression-test requirement are in there.

Open invitations:
- JSON Schema mapping for complex DTOs (recursion handling, polymorphism)
- Streamable HTTP transport config refinements
- Sample host integration against eShopOnAbp

Every bug fix lands with a regression test. No exceptions.

## Other docs

- [DESIGN.md](DESIGN.md) — premises, alternatives considered, scope decisions
- [CHANGELOG.md](CHANGELOG.md) — what shipped when
- [CONTRIBUTING.md](CONTRIBUTING.md) — setup, design rules, PR process
- [SECURITY.md](SECURITY.md) — vulnerability reporting (do NOT open a public issue for security)
- [CLAUDE.md](CLAUDE.md) — project guidance for Claude Code & humans

## License

[MIT](LICENSE).
