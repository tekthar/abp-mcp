# Dynamic add-ins & schema requirements

**Status:** shipping in `[Unreleased]` · **Provenance:** requirements extracted from a real ABP deployment (an inspection/violations platform, ~180 `[McpTool]` methods) that forked and evolved `abp-mcp` under production pressure. This document is both the spec that drove the work and the guide to the resulting feature.

The deployment taught two things:

1. **The advertised schema was lying to the agent.** Complex DTOs collapsed to `{"type":"object"}`, so an agent could not tell what to send; `Dictionary<,>` was emitted as an array of key/value pairs; enums were advertised by name but the runtime demanded the integer. The team papered over this with hand-written tool descriptions and runtime error hints. The fix is to make the schema *true*.
2. **A single generator can't anticipate every app.** The deployment needed to add tools and reshape metadata that no attribute could express. The answer is a first-class extension surface: **dynamic add-ins**.

---

## Part 1 — The dynamic add-in model

An add-in is an ordered, DI-registered contributor that runs over the discovered tool set once at startup, before the server answers its first `tools/list`.

```csharp
public interface IAbpMcpAddin
{
    int Order { get; }
    void Contribute(AbpMcpToolBuildContext context);
}
```

Register any number; they run in ascending `Order`:

```csharp
public override void ConfigureServices(ServiceConfigurationContext context)
{
    context.Services.AddSingleton<IAbpMcpAddin, MyAddin>();
}
```

**Ordering bands** (mirror the pipeline stages):

| Band | Stage | Typical use |
|------|-------|-------------|
| `< 100` | discovery | add or remove tools |
| `100–499` | metadata | names, descriptions, permissions |
| `500–899` | schema | input/output schema shaping |
| `≥ 900` | security | final visibility / hardening passes |

### The build context

`AbpMcpToolBuildContext` is the mutable working set, seeded with the tools discovered from ABP application services. Mutation goes through methods (not a raw list) so the unique-name invariant always holds:

- `AddTool(ToolDescriptor)` — add a tool; throws on a duplicate name.
- `RemoveTool(string name) → bool`
- `Enrich(string name, Func<ToolDescriptor, ToolDescriptor> transform) → bool` — replace a descriptor in place (`d => d with { ... }`); may not change the name.
- `FindTool(string name)`, `Tools` (read-only view).
- `Options`, `Services` (the **root** provider — for build-time singletons; per-call work belongs in a tool handler).

### Enriching an existing tool

```csharp
public sealed class DescriptionPrefixAddin : IAbpMcpAddin
{
    public int Order => 200; // metadata band
    public void Contribute(AbpMcpToolBuildContext context)
    {
        foreach (var name in context.Tools.Select(t => t.Name).ToArray())
            context.Enrich(name, d => d with { Description = $"[catalog] {d.Description}" });
    }
}
```

### Contributing a dynamic tool

A dynamic tool is backed by a handler instead of a service method. It dispatches through the **same** path as a service method: the disabled-tool check and permission pre-check run first, then the handler executes with the **request scope** (resolve `ICurrentUser`, repositories, application services from `invocation.Services`), and any exception is mapped to an MCP error exactly as a service method's would be.

```csharp
public sealed class OverviewAddin : IAbpMcpAddin
{
    public int Order => 50; // discovery band
    public void Contribute(AbpMcpToolBuildContext context) =>
        context.AddTool(new ToolDescriptor
        {
            Name = "Library_Overview",
            Description = "Aggregate catalog counts.",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            ParameterNames = Array.Empty<string>(),
            RequiredPermissions = Array.Empty<string>(),
            Handler = async (invocation, ct) =>
            {
                var db = invocation.Services.GetRequiredService<LibraryDbContext>();
                return JsonSerializer.SerializeToElement(new { titles = await db.Titles.CountAsync(ct) });
            },
        });
}
```

See [`samples/AbpMcp.Sample/Library/LibraryInsightsAddin.cs`](../samples/AbpMcp.Sample/Library/LibraryInsightsAddin.cs) for the full runnable example.

### Built-in add-ins

Both are registered by default and demonstrate the two enrichment styles.

- **`XmlDocDescriptionAddin`** (metadata band, order 200) — fills tool and parameter descriptions from the host's XML doc comments (`<summary>`/`<param>`), looking through to the contract interface. Description quality is the biggest single lever on how well an agent uses a tool, and ABP services already carry these docs, so this surfaces them for free instead of the mechanical `Invoke Service.Method.` fallback. An explicit `[McpTool(Description = ...)]` always wins; a silent no-op when no XML doc file is present. Opt out with `AbpMcpOptions.UseXmlDocumentation = false`.
- **`OutputSchemaAddin`** (schema band, order 500) — advertises a JSON Schema for each method-backed tool's structured result, derived from the return type — unwrapping `Task<T>`/`ValueTask<T>` and walking ABP result wrappers (`PagedResultDto<T>` → `{ items, totalCount }`, `IReadOnlyList<T>` → array). Dynamic tools set their own `OutputSchema` if they want one. Opt out with `AbpMcpOptions.IncludeOutputSchema = false`.

### Changing the tool set at runtime

Add-ins run once at startup. When the tool set must change *after* that — a plugin loads, or a host swaps in its own `IDynamicMcpToolRegistry` and mutates it — two things have to happen: rebuild the catalog, and tell connected agents.

- `IDynamicMcpToolRegistry.Refresh()` re-reads discovery, re-runs the add-in pipeline, and swaps the tool set in atomically (concurrent readers see the old set or the new one, never a torn state). Unlike `Initialize()` it always rebuilds and skips the "at least one tool" startup guard.
- `IAbpMcpToolListChangedNotifier.NotifyToolListChangedAsync()` does both in one call: it refreshes the registry, then broadcasts MCP's `notifications/tools/list_changed` to every connected session so agents re-read `tools/list` without reconnecting.

```csharp
// after a plugin's tools change:
await services.GetRequiredService<IAbpMcpToolListChangedNotifier>()
    .NotifyToolListChangedAsync(cancellationToken);
```

Server-initiated notifications need a session to push to, so delivery requires a **non-stateless** HTTP transport (abp-mcp's default). Under the stateless transport each request is its own session and there is nothing to notify after the response; `Refresh()` still works, and the next `tools/list` returns the new set.

---

## Part 2 — Schema requirements catalog

Every row is a real DTO shape from the source deployment. "Status" is this package's behavior after this pass.

| # | Real-world shape | Required schema behavior | Status |
|---|------------------|--------------------------|--------|
| 1 | Complex DTO parameter (`Create(CreateFooDto input)`) | Walk into named properties | **Done** |
| 2 | Nested DTO / `List<Dto>` | Recurse; array of walked items | **Done** |
| 3 | Self-referential graph (AND/OR/NOT filter tree) | Cycle guard + depth cap → opaque fallback | **Done** (`MaxDepth = 8`) |
| 4 | `Dictionary<string, T>` property bag (JSONB) | `object` + `additionalProperties: <T>` | **Done** (was array of `{key,value}`) |
| 5 | `[JsonExtensionData]` catch-all | Open parent `additionalProperties`, not a literal property | **Done** |
| 6 | `object` / `JsonElement` value | Permissive any-value schema (`{}`) | **Done** |
| 7 | Enum | String names in schema **and** they round-trip at runtime | **Done** (`JsonStringEnumConverter` in dispatcher) |
| 8 | `[Required]` | `required` array | **Done** |
| 9 | `[Range]`/`[StringLength]`/`[MinLength]`/`[MaxLength]`/`[RegularExpression]`/`[EmailAddress]` | `minimum`/`maximum`/`min|maxLength`/`min|maxItems`/`pattern`/`format` | **Done** |
| 10 | `[JsonPropertyName]` / `[JsonIgnore]` | Honor custom name / omit | **Done** |
| 11 | Non-nullable body DTO parameter | `required` (NRT-aware) | **Done** |
| 12 | `DateOnly`/`TimeOnly`/`TimeSpan`/`Uri` | `string` + `date`/`time`/`uri` format | **Done** |
| 13 | `PagedResultDto<T>` / `ListResultDto<T>` return | Output schema advertised | **Done** (`OutputSchemaAddin`) |
| 14 | Polymorphic "union by presence" (several optional sub-DTOs, any combination) | Walk each optional sub-object | **Partial** — walked as optional objects; no discriminator/`oneOf`. Tracked. |
| 15 | Spatial tuples (`double[]` bbox = `[minLat,minLng,maxLat,maxLng]`; GeoJSON `List<double[]>`) | Structural array-of-number | **Partial** — shape is correct; positional semantics live in the description. Tracked. |
| 16 | File uploads (`IFormFile`, `Stream`, `IRemoteStreamContent`, `byte[]`) | Detect and skip / special-case | **Tracked** (see Part 3) |

**Deliberate non-goal:** property-level required-ness follows `[Required]` only, not NRT non-null. ABP does not enforce NRT non-null at runtime, so requiring it in the schema would force an agent to send a value the server treats as optional. Method *parameters* are different — the call itself requires them — so parameter required-ness *is* NRT-aware (#11).

---

## Part 3 — Tracked follow-ups

Learned from the same deployment; not in this pass. Several are now natural add-ins rather than core changes.

- **`DiscoverySource.ApplicationServices`** — reflect application-service implementations directly, for hosts that use explicit controllers (so ABP's api-definition does not describe app services). Note: this crosses the current design rule "source of truth is the api-definition, not reflection" and needs an explicit decision.
- **Dispatch through the contract interface** — resolve the interface (not the concrete class) so ABP's `AuthorizationInterceptor` (plus validation, UoW, auditing) runs on the call. Enforcement parity with a REST call.
- **Authorization metadata harvesting** — union class + method `[Authorize]`, honor `[AllowAnonymous]`, record authentication-only tools.
- **ABP-localized error mapping** — resolve coded `BusinessException`s to their localized text via `IExceptionToErrorInfoConverter`; surface `AbpValidationException` per-member detail; treat guard-clause `ArgumentException` as a caller error.
- **RFC 9728 OAuth resource metadata** — an `AuthorizationPolicy` hook that emits `resource_metadata` on a 401, for MCP OAuth discovery.
- **`ServerInstructions`** — server-level guidance advertised in the `initialize` response.
- **File-type handling** — detect `IFormFile`/`Stream`/`IRemoteStreamContent` and skip them (or offer a JSON alternative), rather than emitting a garbage schema. A natural discovery/schema-band add-in.

> **Shipped since first draft:** description enrichment from XML docs (`XmlDocDescriptionAddin`) — the metadata-band add-in that proves the "tracked follow-up → add-in" path. See Part 1.
