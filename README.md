# abp-mcp

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
│   └── AbpMcp.Sample/              # minimal ABP host to demo against
├── test/
│   └── AbpMcp.Tests/               # xUnit tests
├── DESIGN.md
├── CLAUDE.md
└── README.md
```

## Contributing

Pre-alpha. Direct PRs welcome for:
- JSON Schema mapping for complex DTOs (recursion handling, polymorphism)
- Streamable HTTP transport config
- Sample host integration against eShopOnAbp

Every bug fix lands with a regression test. No exceptions.

## License

MIT.
