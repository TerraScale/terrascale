<!-- shared-rules:start -->

## Shared TerraScale rules

### Shipping code

- No PRs, reviews or merge queue. Push to `main`. Ignore outside contributors' PRs unless asked.
- Fetch `origin` first. Branch from fresh `origin/main` in your own worktree or clone, outside shared checkouts. Commit small changes; no AI attribution.
- Before every push: `git fetch origin && git rebase origin/main`, then pass the changed area's Fast tests on a dev host with `ts-heavy-any <cmd> --repo <repo> --branch <ref>`. Push `git push origin HEAD:main`; rebase and retry a non-fast-forward rejection. Never force-push or delete `main`.
- CI runs on every main push; deploy only green main. If your push makes main red, fix or revert within about 30 minutes. For a red main you did not cause, wait for its owner's fix and rebase.
- Heavy builds, tests, benchmarks and stress repeats use `ts-heavy-any`, never GitHub Actions. Use mariogk-dev or beta; never run anything on or SSH to mariogk-desktop.
- Prove flaky fixes with 5 repeats; benchmarks with 3 measured repeats in `benchmark/bench/`. For page changes, report before/after screenshots.
- StorageFormat.cs bumps and production resets belong to the lane in `.cos/lanes.md`; other lanes put `Needs format bump` in the commit message.

### Agents and decisions

- Use gpt-6.1-sol (Codex, high effort; low for work under 10 minutes) for most work. Use claude-haiku-5-5 for bounded work of at most 20 minutes; never Haiku 4.5 or the `haiku` alias. Reserve claude-opus-5-5 for engine/replication, auth/security design, billing correctness, production infrastructure or work Sol already failed.
- Decide and continue. Record `DECISION: <choice>. Reason: <why>` in commits.
- Workers never ask the user. Send the parent `NEED Q-<ID>: <question> | options: 1) <recommended> 2) ... | DECIDED: 1, continuing`. Only the COS asks the user.
- Block only for a user-only secret/login, new paid capacity, or deleting serving infrastructure or unrecoverable data. Start login device flows yourself, show URL/code, then finish after the user enters them.

### Secrets and hard gates

- Runtime secrets: OCI Vault in gru via instance principal. CI secrets: GitHub Actions. Dev secrets: `ts-dev-secret get <name> | <consumer>` or `ts-dev-secret env <name> -- <cmd>`.
- Never expose secret values in chat, arguments, history, logs, git or commits. Use stdin or a 0600 file. Credential installs are one-command handoffs the user runs.
- Ask first before new paid capacity, security permissions or credentials.
- ts-auth and ts-dashboard store data in TerraBase through ts-dotnet; keep the SDK current with TerraBase's API.
- Outside the monorepo, never download or run TerraBase binaries in tests, CI or helpers; use HTTP fakes or contract fixtures. Live ts-status API monitoring is allowed. Never publish TerraBase binaries.
- Use GitHub and `gh`; no polling loops. Never revive ts-reviewer or ts-cos.

<!-- shared-rules:end -->

# ts-js

## Layout and checks

- Public package: `@terrascale/sdk`. Keep `package.json` at `0.0.X` and `package-lock.json` in sync until the owner lifts this restriction.
- Source is JavaScript ES modules with JSDoc in `src/`; `npm run build` generates TypeScript declarations in `types/`. Use native Node/npm tools, not a TypeScript source rewrite.
- Use Node 22 or 24 and the npm version in `packageManager`. Install with `npm ci`.
- Fast: `npm test`; build: `npm run build`; static checks: `npm run typecheck` and `npm run check:v1-route-manifest`; browser tests: `npm run test:browser`.
- Current exports are root, `admin`, `management`, `ts-auth`, `react`, `svelte` and Node-only `codegen`. Keep credentials and server-only operations out of browser bundles. Do not add Dashboard-named or generic request entrypoints.

## Public API

- The C# TerraBase producer owns the project-scoped v1 database API. Preserve `/v1/projects/{project}/databases/{db}` scope, Bearer `tb_` keys and named producer scopes.
- Land the owning service's versioned REST, WebSocket or SSE contract first. Verify method/message, endpoint, operation identity, parameters, media types, field types/nullability, security/scope, success/error/partial outcomes, retry/idempotency, pagination/checkpoints and generated contract/route parity. A typed build alone proves no parity.
- Never invent routes, DTOs, headers, protocol frames or server behavior. TerraScale data transport stays typed: no arbitrary paths, untyped JSON casts, generic requests, custom headers or consumer-specific escape hatches.
- Preserve supported public compatibility; breaking behavior needs a versioned contract and release plan. Backend cutovers do not authorize breaking `/api/v1` routes.
- Consumers use public operations with customer authorization: no private entrypoints, tenant bypass, direct database access or in-process transport.
- Do not extend retired Rust tenant/shard routes, `tbk1` keys, frozen Rust inputs, Loro payloads or the server-ack-only sync v2 corpus. They are not current producer evidence.

## Reactive and retry rules

- Durable mutations reach the server; no offline durable writes, mutation queue, local CRDT actors or deferred merge. Reversible optimistic presentation uses one stable idempotency identity.
- Subscribe to typed, bounded TQL queries with authorized snapshots and insert/update/patch/remove/move/reset operations. Checkpoints are opaque authorized-view positions, not Loro frontiers or replication vectors.
- WebSocket delivery is the fast path; checkpoint resume and snapshot bootstrap establish correctness. Cover permission changes, auth expiry, region movement, slow consumers, reconnect and expired history.
- Retry only documented transient failures safe to repeat. Mutations need stable idempotency. Respect cancellation, deadlines, `Retry-After` and overload; never auto-retry validation, authentication, authorization or deterministic conflicts.

## Contract evidence

- REST changes require OpenAPI/route-manifest drift checks. Reactive changes require generated schema, byte vectors and the current C# producer/JavaScript semantic corpus.
- Live API checks use authorized, isolated service allocations and published contracts. Report unavailable live gates as skipped; never fabricate evidence. Keep qualification claims separate from HTTP fixture unit tests.

## Configuration

- Keep features always on; no feature flags, enable switches, opt-ins or kill switches. Keep one correct value in code, with no new tuning knobs.
- Fakes, demo stores and skip-auth belong only in test code and test binaries.
- Keep the switch-style configuration check; unavoidable third-party names need a narrow allowance and a one-line reason.
- README.md records implementation and qualification gaps. Do not claim unshipped behavior or bypass authorization, tenant scope, registered callbacks, payment consent or safety guards.
