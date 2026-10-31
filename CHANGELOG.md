# Changelog

All notable changes to `abp-mcp` are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until v1.0.0 lands, breaking changes can ship in any minor or pre-release version — the surface is still being shaped by real usage.

## [Unreleased]

Nothing yet. The next changes after `v0.3.0-alpha` ship here.

## [0.3.0-alpha] — 2026-10-30

Hardens the MCP path to match ABP's REST path (authorization, validation, unit of work, features, auditing), makes `tools/list` work with every MCP client, and validates the transport. Contains behavior changes; see **Changed**.

### Security

- **`tools/list` and `tools/call` now enforce an app service's full `[Authorize]` model.** Previously only `[Authorize(Policy)]` names were honored: a method policy *replaced* the class policy, and `Roles`, `AuthenticationSchemes` and policy-less (authenticated-only) `[Authorize]` were dropped. Because tools are invoked on the concrete service by reflection, no MVC filter or ABP interceptor filled the gap. Method and class requirements are now combined with ASP.NET Core's own `AuthorizationPolicy.CombineAsync`, matching the parallel HTTP endpoint. Method-level `[AllowAnonymous]` clears requirements as in ABP; class-level `[AllowAnonymous]` does not override an `[Authorize]`, so the check fails closed.
- **Tool arguments are now validated before invocation.** The dispatcher runs ABP's `IMethodInvocationValidator` (DataAnnotations, `IValidatableObject`, registered validation contributors). Before, ABP's `ValidationInterceptor` never ran for sealed or non-virtual app-service methods, so constraints were advertised in the input schema but not enforced. Invalid input returns `VALIDATION_ERROR` and the method is not called.
- **`[McpIgnore]` on a contract-interface method is now honored.** Exposure resolution, and the `/mcp/_explain` report, only looked at the concrete method, so an interface-declared `[McpIgnore]` was silently ignored under a class-level `[McpTool]`.
- **Every tool call now runs in an ABP unit of work.** The MCP endpoint is outside the MVC pipeline, whose UoW filter makes a REST call transactional, and the concrete service is invoked by reflection, so no interceptor opened one either. Each repository call committed on its own, and a tool that failed after a write left that write in place. The dispatcher now begins a unit of work around the call (dynamic add-in tools included) and completes it only on success. As on ABP's REST path, it is transactional unless the parallel action is a `GET` (`Get*`/`GetList*` reads), which run in a non-transactional unit of work. The verb comes from the api-definition (new `ToolDescriptor.HttpMethod`, also shown in `_discover`) and falls back to ABP's method-name convention. It honors `[UnitOfWork]` on the method (`IsDisabled`, `IsTransactional`, isolation level, timeout) and `AbpUnitOfWorkDefaultOptions.TransactionBehavior`, and joins a unit of work that is already active.
- **`[RequiresFeature]`, `[RequiresGlobalFeature]` and audit logging now apply to every tool call.** ABP's feature and auditing interceptors only cover virtual methods on a class proxy, and the MCP endpoint is outside the MVC filters that cover the REST path, so for the non-virtual methods ABP's templates generate a tool call skipped feature checks and left no audit log. The dispatcher now mirrors `AbpFeatureActionFilter` (`FORBIDDEN` with the feature's code; `DISABLED` for a disabled global feature) and `AbpAuditActionFilter` (joins the request's audit scope, or opens and saves one as `AuditingInterceptor` does; honors `AbpAuditingOptions` and `[DisableAuditing]`). Both concerns are marked applied, so a virtual method is not checked or audited twice.
- **The `Origin` header is now validated** on `/mcp`, `/mcp/_discover` and `/mcp/_explain`, as the MCP transport spec requires. A request whose `Origin` is not in the new `AbpMcpOptions.AllowedOrigins` gets `403`, so a web page can no longer drive a local or intranet MCP server through the user's browser (DNS rebinding). Requests without an `Origin` (desktop agents, CLIs, server-to-server) are unaffected.
- **Ended MCP sessions are no longer kept in memory.** The `tools/list_changed` session registry held every session it had seen until a broadcast to it failed, so memory grew with every connect. Sessions are now held weakly and released once the SDK ends them.
- **`/mcp/_discover` lists only the tools the caller is authorized for**, the same gate as `tools/list`, instead of every tool's schema and required permissions. **`/mcp/_explain` is scoped to `ExposedAssemblies`** instead of listing every app and framework service.

### Added

- `IAbpMcpToolAuthorizer` (`AbpMcp.Authorization`) — the single authorization gate shared by `tools/list` and `tools/call`; replaceable via DI.
- `ToolDescriptor.AuthorizeData` — the complete harvested `[Authorize]` data for a tool. `/mcp/_discover` rows gain `requires_authenticated_user`, `required_roles` and `authentication_schemes`.
- `AbpMcpOptions.AllowedOrigins` — browser origins allowed to call the MCP and diagnostics endpoints (`"*"` for any).
- `/mcp/_discover` rows gain `disabled`; `/mcp/_explain` responses gain `exposed_assemblies`.
- End-to-end tests that drive the bundled sample with the MCP SDK's own client ([#54](https://github.com/tekthar/abp-mcp/issues/54)).
- **Cross-instance kill switch** ([#13](https://github.com/tekthar/abp-mcp/issues/13), Stage B). A new global ABP setting, `AbpMcp.DisabledTools` (`AbpMcpSettingNames.DisabledTools`), takes a comma-separated list of tool names to disable. With ABP Setting Management and a shared distributed cache (e.g. Redis), a global value applies to every instance; without it, the value can come from configuration (`Settings:AbpMcp.DisabledTools`). It is combined with `AbpMcpOptions.DisabledTools`, and tenant and user values are ignored. abp-mcp reads it through `ISettingProvider`, so it takes no dependency on the Setting Management module.
- `AbpMcpOptions.DisabledToolsSettingCacheDuration` (default 10 seconds) — how long each instance caches the setting, so `tools/call` does not read the settings store every time.
- `IDisabledToolsProvider` (`AbpMcp.Settings`) — the single source of disabled tools for `tools/list`, `tools/call`, the dispatcher and `/mcp/_discover`; replaceable via DI.

### Changed

- `ToolDescriptor.RequiredPermissions` is now the union of method and class `[Authorize(Policy)]` names.
- `VALIDATION_ERROR` messages now include ABP's per-member validation errors.
- Tools whose service methods carry `Roles`-only or policy-less `[Authorize]` are no longer listed to, or callable by, callers who do not meet those requirements. Hosts that relied on the old, looser behavior will see `FORBIDDEN`.
- Browser-based MCP clients (e.g. the MCP Inspector web UI) must now be listed in `AbpMcpOptions.AllowedOrigins`.
- Nullable value types are described with JSON Schema type unions (`"type": ["integer", "null"]`) instead of OpenAPI's `"nullable": true` ([#52](https://github.com/tekthar/abp-mcp/issues/52)).
- The error-mapping table moved from `AbpMcpDispatcher` to `AbpMcpExceptionMapper`.

### Fixed

- `tools/list` failed for every client when any tool returned a collection: MCP requires object output schemas. Non-object results are now advertised and returned wrapped as `{ "result": … }` ([#49](https://github.com/tekthar/abp-mcp/issues/49)).
- A tool added to `AbpMcpOptions.DisabledTools` at runtime now drops out of `tools/list` and is flagged `disabled` in `/mcp/_discover` ([#13](https://github.com/tekthar/abp-mcp/issues/13), Stage A). Both read `IOptions<AbpMcpOptions>`, a cache separate from the `IOptionsMonitor` instance the dispatcher reads, so the tool was refused on call but still advertised. They now read `IOptionsMonitor<AbpMcpOptions>.CurrentValue`.
- A malformed tool argument now returns `VALIDATION_ERROR` naming the parameter and JSON path, instead of the SDK's generic error ([#50](https://github.com/tekthar/abp-mcp/issues/50)).
- `EntityNotFoundException` maps to `NOT_FOUND` instead of `INTERNAL`; coded authorization failures report their code instead of the CLR default message ([#51](https://github.com/tekthar/abp-mcp/issues/51)).
- The sample listens on `localhost:5000`, matching the README ([#53](https://github.com/tekthar/abp-mcp/issues/53)).

## [0.2.1-alpha] — 2026-10-10

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

[Unreleased]: https://github.com/tekthar/abp-mcp/compare/v0.3.0-alpha...HEAD
[0.3.0-alpha]: https://github.com/tekthar/abp-mcp/compare/v0.2.1-alpha...v0.3.0-alpha
[0.2.1-alpha]: https://github.com/tekthar/abp-mcp/compare/v0.1.0-alpha...v0.2.1-alpha
[0.1.0-alpha]: https://github.com/tekthar/abp-mcp/releases/tag/v0.1.0-alpha
