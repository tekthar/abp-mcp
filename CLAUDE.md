# CLAUDE.md

Project-specific guidance for Claude Code (and any agent) working on `abp-mcp`.

## Project summary

`abp-mcp` is an open-source NuGet package that turns any ABP Framework application into a Model Context Protocol server. It reads ABP's `/api/abp/api-definition` metadata, filters by `[McpTool]`-tagged services, and exposes them as MCP tools with permission-aware visibility and ABP-native error mapping. Target framework: `net9.0`. Current ABP reference version: `9.2.*`. Current MCP SDK: `ModelContextProtocol.AspNetCore` 1.0.

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
4. **No catch-all exception handlers in the dispatcher.** Use the mapping table in `AbpMcpDispatcher.MapException`. Every ABP exception type has a specific translation. Unknown exceptions become `INTERNAL` with no stack trace leaked.
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
- **Integration** (planned): real DI container, `AbpIntegratedTestBase`, in-memory MCP client from the SDK.
- **E2E** (planned): runs the sample host, hits `/mcp` with a real MCP client, verifies tool list and invocation.

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

- `DynamicMcpToolRegistry.Initialize` builds descriptors but the bridge to Microsoft's `IMcpServer` programmatic tool registration is pending the SDK surface stabilizing (tracked upstream in `modelcontextprotocol/csharp-sdk#317`).
- `ToolDescriptorBuilder` emits `{"type": "object"}` for complex DTOs in v0.1. Full DTO walk (nested properties, required-ness, recursion guard) is v1.0.
- `ApiDefinitionReader` does not yet honor `AbpMcpOptions.ExposedAssemblies` — it scans across all loaded ABP modules. Filtering by registered assemblies is queued for the next pass; the API surface is in place so consumers don't need to change anything when it lands.
- No Claude Skill generator yet. That is the Approach C evolution; after v0.1 lands.
- No build-time LLM description enhancement yet. Same — Approach C.

## v0.2 scope (deliberately deferred)

- Type/method predicate funcs (`opts.TypePredicate = type => ...`).
- Include/exclude glob and regex filters per assembly.
- Custom rules engine with Order ranges (0-100 discovery, 100-500 metadata, 500-900 schema, 900+ security).
- ABP Settings Management backing for `DisabledTools` so kill-switch is cross-instance.
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
