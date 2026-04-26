<!--
Thanks for the PR! A few prompts below — they exist because every one of them was a real PR-review back-and-forth that we'd rather have once, here, than spread across comments.

Small PRs (< 400 lines diff) review fast. Big PRs sit. If this one is big, consider splitting.
-->

## What this changes

<!-- One paragraph. What does the diff actually do? Skip the "why" here, that's below. -->

## Why

<!-- The user-facing problem this solves. Quote DESIGN.md or an issue if relevant. -->

Closes #

## How to verify

<!--
Concrete commands a reviewer can paste. "Run dotnet test" is not enough — name the test method, name the curl URL.

Example:
- `dotnet test test/AbpMcp.IntegrationTests --filter "FullyQualifiedName~CheckOutPersistsRowToDatabase"`
- Run `samples/AbpMcp.Sample`, then `curl http://localhost:5000/mcp/_explain?service=Loan` — Loan_CheckOut should now show `included=false` when Member is suspended.
-->

## Checklist

- [ ] CI is green (build + test + pack-smoke).
- [ ] Tests added for the new path. **Bug fixes include a regression test that would have caught the bug** (this is a hard rule).
- [ ] Public API changes have XML doc comments. (`TreatWarningsAsErrors` will fail CI without them.)
- [ ] [`CHANGELOG.md`](../CHANGELOG.md) updated under `## [Unreleased]`.
- [ ] If this changes the public surface (`AbpMcpOptions`, attributes, builder extensions), [`DESIGN.md`](../DESIGN.md) reflects the new shape — or a follow-up issue is opened to reconcile.

## Design-rule compliance

<!-- Tick what applies. Anything that doesn't tick is a design-conversation flag — explain in the PR body. -->

- [ ] Source of truth stays ABP's api-definition (no new reflection-based scanning paths).
- [ ] Tool exposure stays opt-in (no "expose everything" mode).
- [ ] Permission filter on `tools/list` AND permission re-check on `tools/call` are both still in place.
- [ ] No new catch-all exception handler in the dispatcher; new exception types added to `MapException` table if needed.
- [ ] Stays in-process (no sidecar, no stdio transport).
- [ ] Public surface stays ABP-idiomatic (mirrors `[RemoteService]` / `ConventionalControllers` patterns).
- [ ] `/mcp/_discover` and `/mcp/_explain` still ship unconditionally.

## Notes for the reviewer

<!-- Anything else worth flagging: tradeoffs you weighed, things you punted, related issues, follow-ups. -->
