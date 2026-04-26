# Contributing to abp-mcp

Thanks for considering a contribution. This project is small, opinionated, and moves fast — please skim this whole file before opening a non-trivial PR. The non-negotiable design rules at the bottom are not suggestions; they're the reason the codebase stays small.

## TL;DR

```bash
git clone https://github.com/tekthar/abp-mcp.git
cd abp-mcp
dotnet restore
dotnet test
dotnet run --project samples/AbpMcp.Sample
# then in another terminal:
curl http://localhost:5000/mcp/_discover
```

If `dotnet test` is green and the sample's `/mcp/_discover` returns 15 tools, you're set up.

## Repo layout

```
abp-mcp/
├── src/AbpMcp/                           # the library (this is what ships on NuGet)
├── samples/AbpMcp.Sample/                # runnable demo (Library domain — Title, Edition, Member, Loan)
├── test/AbpMcp.Tests/                    # unit tests (pure logic — schema mapping, naming)
├── test/AbpMcp.IntegrationTests/         # integration tests (seed → invoke → verify-DB pattern)
├── DESIGN.md                             # premises + scope decisions (read this first)
├── CLAUDE.md                             # project guidance for Claude Code & humans
└── CHANGELOG.md                          # what shipped when
```

## Picking what to work on

- **Good first issues** are tagged [`good first issue`](https://github.com/tekthar/abp-mcp/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22). Most are documentation, sample improvements, or small reader/builder edge cases.
- **For anything bigger**, please open a discussion or a draft issue first. We will not silently merge a 500-line PR that wasn't talked about — not because it's bad, but because we want to lock in scope before either of us spends time.
- **Before adding a feature**, check [DESIGN.md](DESIGN.md) — it lists what's deliberately out of scope (e.g., "Approach C — LLM-enhanced descriptions" was scoped out for v1.0 based on community feedback). Re-opening a scoped-out item needs a design conversation first.

## Making the change

1. Branch off `main`. Conventional commits-ish messages, lower-case prefix, present tense:
   - `feat: opt-in McpTool prefix override on AbpMcpOptions`
   - `fix: handle nullable enum parameters in JsonSchemaMapper`
   - `docs: clarify ExposedAssemblies vs ConventionalControllers`
   - `chore:` / `ci:` / `test:` for non-functional changes
2. Write the test first when you can. The integration test pattern (seed DB → invoke through dispatcher → assert DB state) is in `test/AbpMcp.IntegrationTests/Tools/DispatcherInvocationTests.cs` — copy that shape.
3. **Bug fixes ship with a regression test that would have caught the bug.** No exceptions. This is a hard rule.
4. Run `dotnet test` locally before pushing.
5. Open a PR against `main`. CI will build, test, and pack-smoke. All three need to be green for review.

## PR review

- Small PRs review fast. Big PRs sit. If a PR exceeds ~400 lines of diff, expect a request to split it.
- Update [`CHANGELOG.md`](CHANGELOG.md) under `## [Unreleased]` in the same PR.
- Public API changes need an XML doc comment. The build is `TreatWarningsAsErrors`; missing docs on a `public` type or member fail CI.
- Reviewer will look for: design-rule compliance (below), test coverage of the new path, and whether the change moves toward or away from the scope in DESIGN.md.

## Non-negotiable design rules

These come from `CLAUDE.md`. They exist because every one of them was a real bug somewhere in some auto-generator before this project. Push back if you disagree, but do it in an issue before in a PR.

1. **Source of truth is ABP's api-definition, not OpenAPI and not raw reflection.** If a change makes us re-implement metadata extraction, stop and reconsider.
2. **Tool exposure is opt-in.** A service without `[McpTool]` is invisible. No "expose everything" mode without an explicit design change.
3. **Permission check before invoke. Permission filter before list.** Both paths. Test both.
4. **No catch-all exception handlers in the dispatcher.** Use the mapping table in `AbpMcpDispatcher.MapException`. Unknown exceptions become `INTERNAL` — never leak a stack trace.
5. **In-process only for v0.1 and v1.0.** No sidecar processes. No stdio transport. Streamable HTTP only.
6. **ABP-idiomatic public surface.** Attributes mirror `[RemoteService]` ergonomics. Options collection mirrors `ConventionalControllers.Create(...)`. `Configure<AbpMcpOptions>` works in `PreConfigureServices`. If a new option breaks this convention, find another way.
7. **`/mcp/_discover` and `/mcp/_explain` endpoints stay shipped.** They are the diff between an auto-generator that frustrates devs and one that delights them. Do not move them behind a feature flag, do not lazy-load, do not skimp on the explain reasons.

## Code conventions

- `Nullable` enabled everywhere. `TreatWarningsAsErrors` on. Fix warnings; do not suppress them.
- XML docs required for every `public` type and member.
- Each core component (`ApiDefinitionReader`, `ToolDescriptorBuilder`, `DynamicMcpToolRegistry`, `AbpMcpDispatcher`, `AbpMcpHandlerWiring`) lives in its own file, ~200 LOC max. If a file grows past that, the component probably wants to be split.
- Prefer explicit over clever. JSON schema mapping lives in named functions per type family in `JsonSchemaMapper`, not a giant switch.
- Internal types are `internal sealed`. Public extension points are an `I<Component>` interface plus a default implementation.

## Reporting bugs

Open a [bug report](https://github.com/tekthar/abp-mcp/issues/new?template=bug_report.yml). The template asks for:
- ABP version, .NET SDK version
- A minimal `[McpTool]` service that reproduces it
- Output of `curl http://localhost:5000/mcp/_explain` for the affected service (this is the single most useful debugging input)
- Expected vs actual behavior

## Reporting security issues

Don't open a public issue for a security problem. See [`SECURITY.md`](SECURITY.md).

## Code of conduct

Be kind. Disagree with ideas, not with people. Don't be the reason someone leaves the .NET community. We follow the spirit of the [Contributor Covenant](https://www.contributor-covenant.org/version/2/1/code_of_conduct/) — if you see behavior that violates it, email the maintainers (see SECURITY.md for contact).

## License

By contributing, you agree your contribution is licensed under the [MIT License](LICENSE).
