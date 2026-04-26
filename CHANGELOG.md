# Changelog

All notable changes to `abp-mcp` are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until v1.0.0 lands, breaking changes can ship in any minor or pre-release version — the surface is still being shaped by real usage.

## [Unreleased]

Nothing yet. The next changes after `v0.1.0-alpha` ship here.

## [0.1.0-alpha] — 2026-04-26

First public pre-release. Functional but pre-alpha — API surface will move based on early feedback.

### Added

- **Core library (`AbpMcp`)** — adds `builder.Services.AddAbpMcp()` and `app.MapAbpMcp()` to expose ABP application services as MCP tools, in-process, alongside the host's existing endpoints.
- **`[McpTool]` / `[McpIgnore]` attributes** — opt-in tool exposure that mirrors ABP's `[RemoteService]` ergonomics. A service without `[McpTool]` is invisible to MCP clients.
- **`AbpMcpOptions`** — shaped to match ABP's `AbpAspNetCoreMvcOptions.ConventionalControllers` so it feels native:
  - `ExposedAssemblies.Create(typeof(MyModule).Assembly)` — scoped scanning per assembly
  - `ToolNameNormalizer` — pluggable naming strategy (default strips `AppService`/`Async`/etc.)
  - `DisabledTools` — runtime kill-switch (hot-reload via `IOptionsMonitor`)
  - `AllowAnonymous`, `RequireAtLeastOneTool`, `Path`, `ToolNamePrefix`
- **Permission-aware tool listing and dispatch** — every `tools/list` filters by the caller's granted permissions; every `tools/call` re-checks at the dispatcher boundary (defense in depth).
- **ABP exception → MCP error mapping** — `AbpValidationException`, `AbpAuthorizationException`, `BusinessException`, `UserFriendlyException`, and `OperationCanceledException` all translate to specific MCP error codes; unknown exceptions become `INTERNAL` with no stack-trace leakage.
- **Diagnostic endpoints** (always shipped, never feature-flagged):
  - `GET /mcp/_discover` — list every registered tool with its name, description, JSON schema, and required permissions
  - `GET /mcp/_explain?service=...` — see exactly why each candidate service method was included or excluded
- **Sample host (`samples/AbpMcp.Sample`)** — a runnable ABP host with a small library domain (`Title`, `Edition`, `Member`, `Loan`) and 15 `[McpTool]`-decorated methods across `CatalogAppService`, `MemberAppService`, `LoanAppService`. Seeded with six classic titles (Hobbit, Dune, Pride and Prejudice, Gatsby, 1984, Foundation) on first boot.
- **Test suites:**
  - 21 unit tests (`test/AbpMcp.Tests`) — JSON schema mapping, naming conventions, options shape
  - 4 integration tests (`test/AbpMcp.IntegrationTests`) — seed-DB → invoke-tool → verify-DB-state, plus disabled-tool kill-switch coverage
- **CI** — GitHub Actions builds, tests, and pack-smokes every push and PR; a separate workflow signs and publishes on `v*` tags.
- **Package signing** — every published package is signed by the registered tekthar code-signing certificate (timestamped via DigiCert). Required by the `tekthar` nuget.org organization policy.

### Known limitations (deliberate, in v0.1.0-alpha scope)

- `ToolDescriptorBuilder` emits `{"type": "object"}` for complex DTOs. Recursive property walks with required-ness and cycle protection land in v1.0.
- LLM-enhanced tool descriptions and a paired Claude Skill (Approach C) are deferred until usage data tells us where mechanical descriptions actually fail. See [DESIGN.md](DESIGN.md) for rationale.
- No Roslyn analyzer yet to flag `[McpTool]` on non-`IApplicationService` types at compile time. Targeted for v0.3.
- The api-definition reader does not yet auto-register assemblies with ABP's `ConventionalControllers` convention — users opt in with one extra line. Likely v0.2.

[Unreleased]: https://github.com/tekthar/abp-mcp/compare/v0.1.0-alpha...HEAD
[0.1.0-alpha]: https://github.com/tekthar/abp-mcp/releases/tag/v0.1.0-alpha
