# CLAUDE.md

Project-specific guidance for Claude Code (and any agent) working on `abp-mcp`.

## Project summary

`abp-mcp` is an open-source NuGet package that turns any ABP Framework application into a Model Context Protocol server. It reads ABP's `/api/abp/api-definition` metadata, filters by `[McpTool]`-tagged services, and exposes them as MCP tools with permission-aware visibility and ABP-native error mapping. Multi-targeted: `net9.0` (ABP `9.2.*`) and `net10.0` (ABP `10.1.*`) — per-target versions live in `Directory.Build.props` as `$(AbpVersion)`/`$(EfCoreVersion)`/`$(MsExtVersion)`. Current MCP SDK: `ModelContextProtocol.AspNetCore` 1.0.

## Architecture at a glance

```
Program.cs ──▶ AddAbpMcp + AbpMcpModule
                        │
                        ▼
        IApiDefinitionReader ──▶ IToolDescriptorBuilder
                        │                 │
                        ▼                 ▼
                 IDynamicMcpToolRegistry  (initialized at app start)
                        │
                        ▼
        /mcp endpoint (Microsoft SDK) ──▶ IAbpMcpDispatcher
```

Every piece has exactly one job; none of them owns more than ~200 LOC.

## Non-negotiable design rules

1. **Source of truth is ABP's api-definition, not OpenAPI and not raw reflection.** If a change would make us re-implement metadata extraction, stop and reconsider.
2. **Tool exposure is opt-in.** A service without `[McpTool]` is invisible. Do not add "expose everything" modes without an explicit design change.
3. **Permission check before invoke. Permission filter before list.** Both paths. Test both.
4. **No catch-all exception handlers in the dispatcher.** Use the mapping table in `AbpMcpExceptionMapper`. Every ABP exception type has a specific translation. Unknown exceptions become `INTERNAL` with no stack trace leaked.
5. **In-process only for v0.1 and v1.0.** No sidecar processes. No stdio transport. Streamable HTTP only.
6. **ABP-idiomatic public surface.** Attribute names mirror `[RemoteService]` ergonomics (`[McpTool]`/`[McpIgnore]`). Options collection mirrors `ConventionalControllers.Create(assembly, ...)`. `Configure<AbpMcpOptions>` works in `PreConfigureServices`. If a new option breaks this convention, find another way.
7. **`/mcp/_discover` and `/mcp/_explain` endpoints stay shipped.** They are the diff between "auto-generator that frustrates devs" and "auto-generator that delights them." Do not move them behind a feature flag, do not lazy-load them, do not skimp on the explain reasons.

## Testing

```bash
dotnet test
```

Regression rule: every bug fix lands with a regression test that would have caught the bug. No exceptions.

Test tiers:
- **Unit** (`test/AbpMcp.Tests`): xUnit, FluentAssertions, NSubstitute. Pure logic: Reader parses JSON, Builder emits schemas, naming conventions, enum maps.
- **Integration** (`test/AbpMcp.IntegrationTests`): real ABP container (`AbpIntegratedTest`), invoking through `IAbpMcpDispatcher` and asserting side effects (DB rows, audit logs).
- **E2E** (`test/AbpMcp.IntegrationTests/EndToEnd`): boots the Library sample on an in-memory `TestServer` (`SampleHost`) and drives `/mcp` with the SDK's own `McpClient`. Anything that crosses the SDK seam (`tools/list` shape, `structuredContent`, transport/origin rules, session lifetime) needs a test here — descriptor-level tests cannot see it. Test classes that boot the sample join `[Collection(SampleHostCollection.Name)]`.

## Code conventions

- `Nullable` is enabled everywhere. `TreatWarningsAsErrors` is on. Fix warnings; do not suppress them.
- XML docs are required for every public type and member (the project flags `GenerateDocumentationFile`).
- Keep each core component in its own file, under ~200 LOC. If a file grows beyond that, the component probably wants to be split.
- Prefer explicit over clever. Keep JSON schema mapping in one named function per type family in `JsonSchemaMapper` — not a giant switch.

## Build

```bash
dotnet build
```

Local NuGet pack (dry run):

```bash
dotnet pack src/AbpMcp/AbpMcp.csproj -c Release -o artifacts/
```

## Known stubs and open work (as of v0.1)

- `DynamicMcpToolRegistry.Initialize` builds descriptors and `AbpMcpHandlerWiring` surfaces them via the MCP SDK's `ListTools`/`CallTool` handlers. A future move to the SDK's programmatic tool-registration API (if/when it stabilizes — tracked upstream in `modelcontextprotocol/csharp-sdk#317`) is a one-file swap behind `IDynamicMcpToolRegistry`.
- `JsonSchemaMapper` walks complex DTOs recursively (nested properties, enums, dictionaries, collections, DataAnnotation constraints). `required` follows ABP's `[Required]` contract, and a cycle guard plus a depth cap of 8 collapse self-referencing or very deep graphs to an opaque `{"type": "object"}`. Emitting `$ref`/`$defs` for recursion is not implemented.
- `ApiDefinitionReader` honors `AbpMcpOptions.ExposedAssemblies` as a filter (registered → restrict; empty → scan everything ABP gave us). The convenience extension `services.AddAbpMcpAssembly(asm)` registers with both `AbpAspNetCoreMvcOptions.ConventionalControllers` and `AbpMcpOptions.ExposedAssemblies` in one call. The lambda-style `Configure<AbpMcpOptions>(o => o.ExposedAssemblies.Create(asm))` alone does NOT register with `ConventionalControllers`, so it silently yields zero tools. The planned fix is module-graph auto-discovery (#55, v0.2), which supersedes the lambda-only fix (#9, closed).
- No Claude Skill generator yet. That is the Approach C evolution; after v0.1 lands.
- No build-time LLM description enhancement yet. Same — Approach C.

## v0.2 scope (deliberately deferred)

- Type/method predicate funcs (`opts.TypePredicate = type => ...`).
- Include/exclude glob and regex filters per assembly.
- Auto-discover `[McpTool]` assemblies from the ABP module graph (#55).
- Custom rules engine with Order ranges (0-100 discovery, 100-500 metadata, 500-900 schema, 900+ security).
- Roslyn analyzer to flag `[McpTool]` on non-AppService at compile time (v0.3).

## When suggesting changes

- Read `DESIGN.md` first. It has the premises, the scope, and what's explicitly out.
- When adding a new component, add its interface under `src/AbpMcp/<area>/I<Component>.cs` and its default implementation as `internal sealed`.
- Do not swallow exceptions in the dispatcher. If you find yourself writing `catch (Exception)`, check the mapping table.

## Skill routing

When the user's request matches an available skill, invoke it via the Skill tool. The skill has multi-step workflows, checklists, and quality gates that produce better results than an ad-hoc answer.

- Product ideas, "is this worth building" → `/office-hours`
- Strategy, scope, "think bigger" → `/plan-ceo-review`
- Architecture, "does this design make sense" → `/plan-eng-review`
- Bugs, "why is this broken" → `/investigate`
- "Look at my changes" → `/review`
- Ship, create a PR → `/ship`
