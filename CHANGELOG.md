# Changelog

All notable changes to `abp-mcp` are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until v1.0.0 lands, breaking changes can ship in any minor or pre-release version — the surface is still being shaped by real usage.

## [Unreleased]

Nothing yet. The next changes after `v0.2.0-alpha` ship here.

## [0.2.0-alpha] — 2026-10-09

Driven by requirements extracted from a real ABP deployment (see [docs/dynamic-addins.md](docs/dynamic-addins.md)). Three themes: make the advertised schema *true*, add a first-class extension surface, and support .NET 10 / ABP 10.

### Added

- **.NET 10 / ABP 10 support** — the package now multi-targets `net9.0` (referencing ABP `9.2.*`) and `net10.0` (referencing ABP `10.1.*`). Existing net9 users are unaffected; net10 hosts get a build against the matching ABP major. The full test suite runs on both targets.
- **Dynamic add-in pipeline (`IAbpMcpAddin`, `AbpMcpToolBuildContext`)** — ordered, DI-registered contributors that run over the discovered tool set at startup. An add-in can enrich existing tools (descriptions, schemas, permissions) *and contribute brand-new dynamic tools* that are not one-to-one wrappers over a service method. Register any number with `services.AddSingleton<IAbpMcpAddin, MyAddin>()`. Recommended ordering bands: `<100` discovery, `100–499` metadata, `500–899` schema, `≥900` security.
- **Dynamic (handler-backed) tools** — `AbpMcpToolHandler` + `AbpMcpToolInvocation`. A dynamic tool runs through the dispatcher with request-scoped DI, the same disabled/permission pre-check, and the same ABP exception → MCP error mapping as a service method. `ToolDescriptor` gains `Handler` and `OutputSchema`; `ServiceType`/`Method` are now optional (null for dynamic tools).
- **Built-in `OutputSchemaAddin`** — advertises a JSON Schema for each method-backed tool's structured result, derived from its return type (unwraps `Task<T>`/`ValueTask<T>`, walks `PagedResultDto<T>`/`ListResultDto<T>` naturally). Opt out via `AbpMcpOptions.IncludeOutputSchema = false`. Method-backed tools now carry `outputSchema` in `tools/list`.
- **Built-in `XmlDocDescriptionAddin`** — fills tool and parameter descriptions from the host's XML doc comments (`<summary>`/`<param>`, emitted when `GenerateDocumentationFile` is on), looking through to the contract interface. Turns the mechanical `Invoke Service.Method.` fallback into the real documentation your services already carry. An explicit `[McpTool(Description = ...)]` always wins; a no-op when no XML doc file is present. Opt out via `AbpMcpOptions.UseXmlDocumentation = false`.
- **`JsonSchemaMapper` is now public** — add-ins can map their own types (output schemas, dynamic-tool inputs) with the exact rules the core uses.
- **Runtime tool-set changes + `tools/list_changed`** — `IDynamicMcpToolRegistry.Refresh()` rebuilds the catalog on demand (re-reads discovery, re-runs the add-in pipeline, swaps atomically), and the new `IAbpMcpToolListChangedNotifier` hook refreshes and then broadcasts MCP's `notifications/tools/list_changed` to connected sessions so agents re-read `tools/list` without reconnecting. This is what lets a plugin's tools appear or disappear at runtime. Delivery requires the non-stateless HTTP transport (the default).
- **Sample**: `LibraryInsightsAddin` contributes a dynamic `Library_Overview` tool that aggregates catalog counts — a runnable example of the extension surface.
- **Docs**: README documents the `[McpTool(Description = ...)]` and `[McpTool(Name = ...)]` override patterns with examples and a `_discover` verification step (resolves #15).

### Changed

- **Complex DTO parameters are now walked into full JSON Schema** instead of collapsing to `{"type":"object"}` — nested objects, collections of complex types, and enums, guarded by a cycle check and depth cap for self-referential graphs. This resolves the headline v0.1.0-alpha limitation.
- **Enums now round-trip.** The schema advertises enums by string name (as before), and the dispatcher registers a `JsonStringEnumConverter` so those names actually deserialize (integers are still accepted) and results render the same names. Previously the schema advertised names the dispatcher could not read.
- `Dictionary<K,V>` maps to an `object` with `additionalProperties` (was mis-emitted as an array of `{key,value}` pairs); `[JsonExtensionData]` opens `additionalProperties`; `object`/`JsonElement`/`JsonNode` map to the permissive any-value schema.
- Property names honor `[JsonPropertyName]`; `[JsonIgnore]` properties are omitted from the schema.
- DataAnnotations surface as schema constraints: `[Range]` → `minimum`/`maximum`, `[StringLength]`/`[MinLength]`/`[MaxLength]` → length or item bounds, `[RegularExpression]` → `pattern`, `[EmailAddress]` → `format: email`.
- Parameter required-ness is nullable-reference-type aware — a non-optional body DTO parameter (e.g. `Create(CreateFooDto input)`) is now correctly reported as `required`.
- Added scalar mappings: `DateOnly` → `date`, `TimeOnly`/`TimeSpan` → `time`, `Uri` → `uri`.
- `GET /mcp/_discover` now reports each tool's `kind` (`service` or `dynamic`) and its `output_schema`.

### Fixed

- Enum-bearing tools previously advertised a string-name schema the dispatcher could not deserialize (System.Text.Json's Web default demanded the underlying integer), so passing any enum argument failed. Input now matches the advertised schema.

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
- **`services.AddAbpMcpAssembly(typeof(MyModule).Assembly)`** — convenience extension that registers an assembly with both `AbpAspNetCoreMvcOptions.ConventionalControllers` (so ABP's api-definition provider sees its app services) AND `AbpMcpOptions.ExposedAssemblies` (so the abp-mcp scan scopes to it). Removes the "I configured one but not the other and got zero tools" trap.
- **Permission-aware tool listing and dispatch** — every `tools/list` filters by the caller's granted permissions; every `tools/call` re-checks at the dispatcher boundary (defense in depth).
- **ABP exception → MCP error mapping** — `AbpValidationException`, `AbpAuthorizationException`, `BusinessException`, `UserFriendlyException`, and `OperationCanceledException` all translate to specific MCP error codes; unknown exceptions become `INTERNAL` with no stack-trace leakage.
- **Diagnostic endpoints** (always shipped, never feature-flagged):
  - `GET /mcp/_discover` — list every registered tool with its name, description, JSON schema, and required permissions
  - `GET /mcp/_explain?service=...` — see exactly why each candidate service method was included or excluded
  - Both follow the same auth posture as the MCP endpoint: when `AllowAnonymous = false`, `_discover` and `_explain` require authentication too (they reveal the tool surface, parameter schemas, and required permissions, so anonymous access in an authed host would be a leak).
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
- `services.AddAbpMcpAssembly(asm)` already removes the two-step registration friction for the common case. Letting `AbpMcpOptions.ExposedAssemblies.Create(asm)` *itself* auto-register with `ConventionalControllers` (so the lambda-style `Configure<AbpMcpOptions>(...)` form needs no companion call either) is queued for v0.2.

[Unreleased]: https://github.com/tekthar/abp-mcp/compare/v0.2.0-alpha...HEAD
[0.2.0-alpha]: https://github.com/tekthar/abp-mcp/compare/v0.1.0-alpha...v0.2.0-alpha
[0.1.0-alpha]: https://github.com/tekthar/abp-mcp/releases/tag/v0.1.0-alpha
