<!-- shared-rules:start -->

## Shared TerraScale rules

### Shipping code

- No PRs, reviews or merge queue. Push to `main`. Ignore outside contributors' PRs unless asked.
- Fetch `origin` first. Branch from fresh `origin/main` in your own worktree or clone, outside shared checkouts. Commit small changes; no AI attribution.
- Record non-obvious choices in the commit message as `DECISION: <choice>. Reason: <why>`.
- Before pushing, build or type-check only the project you changed. Don't run test suites locally: GitHub CI runs them on every push to `main`. Rebase and push again if the push races. Never force-push or delete `main`.
- Deploy only green main. If your push makes main red, fix or revert within about 30 minutes. For a red main you did not cause, wait for its owner's fix and rebase.
- Never run anything on or SSH to mariogk-desktop.
- StorageFormat.cs bumps and production resets need the user's approval; otherwise put `Needs format bump` in the commit message.

### Tests

- Keep tests few. Fast covers only the happy path and critical paths. Slow keeps only heavy critical tests; delete everything else.
- A new feature adds at most one happy-path and one critical-failure test. Refactors, renames, docs and config changes add none. Deleting low-value tests is welcome.
- Fast tests run fully parallel. Each single test finishes in under 10 s, each area's Fast suite in under 30 s, and every CI test job in under 5 minutes. Anything slower is a bug.
- Prove flaky fixes with 5 repeats; benchmarks with 3 measured repeats in `benchmark/bench/`.
- UI changes: screenshot every changed page before (`origin/main`) and after (your branch), same viewport, data and theme, in every supported theme. Save the PNGs outside the repo as `~/.local/state/shots/<branch>/<page>-before.png` and `-after.png`; never commit screenshots. Show them in your T3 thread: if you have T3 tools, `html_render` a before/after grid (absolute local image paths are inlined); otherwise embed each as a Markdown image with its absolute path, e.g. `![after: /settings](/home/<user>/.local/state/shots/<branch>/settings-after.png)`, in your final reply. List the pages in the PR body.

### Secrets and hard gates

- Runtime secrets: OCI Vault in gru via instance principal. CI secrets: GitHub Actions. Ask the user for dev secrets.
- Never expose secret values in chat, arguments, history, logs, git or commits. Use stdin or a 0600 file. Credential installs are one-command handoffs the user runs.
- Ask first before new paid capacity, security permissions or credentials.
- ts-auth and ts-dashboard store data in TerraBase through ts-dotnet; keep the SDK current with TerraBase's API.
- Outside the monorepo, never download or run TerraBase binaries in tests, CI or helpers; use HTTP fakes or contract fixtures. Live ts-status API monitoring is allowed. Never publish TerraBase binaries.
- Use GitHub and `gh`; no polling loops.

<!-- shared-rules:end -->

# ts-status


- TypeScript Cloudflare Worker with server-rendered HTML, Vite Plus and the Cloudflare Vite plugin; no frontend framework. Plain CSS uses dashboard design tokens and Lightning CSS.
- `src/` contains the application, probes, API, admin and database code; `schema.sql` defines D1 storage; `public/` holds static assets; `scripts/` holds canary recovery tools.
- Install: `npm ci`. Build: `npm run build`. Fast: `npm run check && npm test`; recovery tests: `python3 -m unittest discover -s scripts -p 'test_*.py'`. Lint: `npm run lint`. This area's Fast suite must finish in under 30 s wall time; each single test must finish in under 10 s.
- Status history, incidents, subscriptions and probe coordination stay on D1 (`STATUS_DB`) and Durable Objects. This deliberate Ouroboros exception must keep working during a TerraBase outage. Never move its data path to TerraBase.
- ts-orch is authoritative for regional origins. Discover all public regions and placement changes automatically; do not keep a static list, copy probe credentials or infer DNS names from region identities. The public `api.terrascale.tech` host supports regional readiness assertions.
- Every feature is always on. Do not add feature flags, activation settings, deploy toggles, kill switches or tuning knobs. Keep one correct value in code. CI allowances for unavoidable third-party switch names need a one-line reason. Fakes, demo stores and skip-auth belong only in tests and test binaries.
- Every region serves when ts-orch reports it ready, with no enable step. Discover public regional services automatically.
