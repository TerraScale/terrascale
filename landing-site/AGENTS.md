<!-- shared-rules:start -->

## Shared TerraScale rules

### Shipping code

- No PRs, reviews or merge queue. Push to `main`. Ignore outside contributors' PRs unless asked.
- Fetch `origin` first. Branch from fresh `origin/main` in your own worktree or clone, outside shared checkouts. Commit small changes; no AI attribution.
- Ship with `ts-ship` (fetch, rebase, affected Fast suites on a dev host, push `main`, retry on races). Docs-only, `.cos`-only and `AGENTS.md`-only changes skip tests. Never force-push or delete `main`.
- CI runs on every main push; deploy only green main. If your push makes main red, fix or revert within about 30 minutes. For a red main you did not cause, wait for its owner's fix and rebase.
- Heavy builds, tests, benchmarks and stress repeats use `ts-heavy-any`, never GitHub Actions. Use mariogk-dev or beta; never run anything on or SSH to mariogk-desktop.
- Fast tests run fully parallel, finish in under 5 minutes and cover only the main features. Everything else goes to Slow; a Fast suite over 5 minutes is a bug.
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
- ts-auth uses sdk/dotnet to reach TerraBase. The dashboard reaches TerraBase over MagicOnion through the shared contracts library. The SDKs track TerraBase's public API.
- Outside the monorepo, never download or run TerraBase binaries in tests, CI or helpers; use HTTP fakes or contract fixtures. Live ts-status API monitoring is allowed. Never publish TerraBase binaries.
- Use GitHub and `gh`; no polling loops. Never revive ts-reviewer or ts-cos.

<!-- shared-rules:end -->

# ts-landing


- Public site: `https://terrascale.tech`. Use SvelteKit, Svelte, TypeScript, Vite and the shared Tailwind `@theme` tokens in `src/index.css`.
- Routes live in `src/routes/`; reusable components in `src/components/`; static assets in `static/`; build and validation helpers in `scripts/`.
- Install: `npm ci`. Build and static validation: `npm run build`. Fast: `node --test scripts/*.test.mjs`. Asset refresh: `npm run icons:sync` and `npm run world-map:sync`.
- Ship static HTML, CSS, JavaScript and assets from `build/` using `@sveltejs/adapter-static`. Prerender every public page from the root layout. Keep SSR enabled at build time, strict prerender checks and no SPA fallback.
- No runtime SSR, application server, Workers, Pages Functions, server actions or request-time API handlers. Fully generated build-time resources are fine. A runtime server requires human approval and an updated deployment plan.
- Keep browser-only code out of build-time execution. Never include private credentials in bundles or generated files.
- Use current stable compatible dependencies; check installed-version docs and migration requirements before upgrades. Keep manifests and lockfile in sync; use existing scripts and avoid competing build pipelines or frameworks.
- Reuse small Svelte components, shared theme tokens and committed brand assets. Preserve branding, typography, spacing and interactions unless assigned to change them.
- Support mobile and desktop without horizontal overflow; respect reduced motion. Use semantic HTML, keyboard-accessible controls, visible focus and image alternatives.
- Keep primary content and navigation in prerendered HTML. Preserve titles, descriptions, social metadata and canonical URLs under `https://terrascale.tech`.
- Optimize assets, defer nonessential browser code and avoid unnecessary dependencies or blocking third-party scripts.
- Cover navigation, calls to action, forms and important interactive states. Check direct navigation and refresh, responsive layouts, links, assets, hydration and console errors. Validate generated output, not just the dev server.
- Every feature is always on. Do not add feature flags, activation settings, deploy toggles, kill switches or tuning knobs. Keep one correct value in code. CI allowances for unavoidable third-party switch names need a one-line reason. Fakes, demo stores and skip-auth belong only in tests and test binaries.
- Do not invent product claims, APIs or URLs, or bypass authorization, tenant scope, registered callbacks, payment consent or safety guards. The contract requires automatic public endpoint discovery; README records SDK and qualification gaps and existing switches remain owning implementation work.
