<!-- shared-rules:start -->

## Shared TerraScale rules

### Shipping code

- No PRs, reviews or merge queue. Push to `main`. Ignore outside contributors' PRs unless asked.
- Fetch `origin` first. Branch from fresh `origin/main` in your own worktree or clone, outside shared checkouts. Commit small changes; no AI attribution.
- Ship with `ts-ship` (fetch, rebase, affected Fast suites on a dev host, push `main`, retry on races). Docs-only, `.cos`-only and `AGENTS.md`-only changes skip tests. Never force-push or delete `main`.
- CI runs on every main push; deploy only green main. If your push makes main red, fix or revert within about 30 minutes. For a red main you did not cause, wait for its owner's fix and rebase.
- Heavy builds, tests, benchmarks and stress repeats use `ts-heavy-any`, never GitHub Actions. Use delta, mariogk-dev or beta (ts-heavy-any balances them); never run anything on or SSH to mariogk-desktop.
- Fast tests run fully parallel. Each area's Fast suite finishes in under 30 s wall time; each single test finishes in under 10 s. A Fast suite over 30 s or a test over 10 s is a bug.
- Fast covers only the happy path and critical paths. Slow keeps only heavy critical tests; delete everything else. Delete at least 80% of tests against the 2026-11-04 policy baseline.
- New features add at most one happy-path and one critical-failure test.
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

# ts-status


- TypeScript Cloudflare Worker with server-rendered HTML, Vite Plus and the Cloudflare Vite plugin; no frontend framework. Plain CSS uses dashboard design tokens and Lightning CSS.
- `src/` contains the application, probes, API, admin and database code; `schema.sql` defines D1 storage; `public/` holds static assets; `scripts/` holds canary recovery tools.
- Install: `npm ci`. Build: `npm run build`. Fast: `npm run check && npm test`; recovery tests: `python3 -m unittest discover -s scripts -p 'test_*.py'`. Lint: `npm run lint`.
- Status history, incidents, subscriptions and probe coordination stay on D1 (`STATUS_DB`) and Durable Objects. This deliberate Ouroboros exception must keep working during a TerraBase outage. Never move its data path to TerraBase.
- ts-orch is authoritative for regional origins. Discover all public regions and placement changes automatically; do not keep a static list, copy probe credentials or infer DNS names from region identities. The public `api.terrascale.tech` host supports regional readiness assertions.
- Every feature is always on. Do not add feature flags, activation settings, deploy toggles, kill switches or tuning knobs. Keep one correct value in code. CI allowances for unavoidable third-party switch names need a one-line reason. Fakes, demo stores and skip-auth belong only in tests and test binaries.
- Every region serves when ts-orch reports it ready, with no enable step. Discover public regional services automatically.
