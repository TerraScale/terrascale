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
- Prove flaky fixes with 5 repeats; benchmarks with 3 measured repeats in `benchmark/bench/`. For page changes, report before/after screenshots.

### Secrets and hard gates

- Runtime secrets: OCI Vault in gru via instance principal. CI secrets: GitHub Actions. Ask the user for dev secrets.
- Never expose secret values in chat, arguments, history, logs, git or commits. Use stdin or a 0600 file. Credential installs are one-command handoffs the user runs.
- Ask first before new paid capacity, security permissions or credentials.
- ts-auth and ts-dashboard store data in TerraBase through ts-dotnet; keep the SDK current with TerraBase's API.
- Outside the monorepo, never download or run TerraBase binaries in tests, CI or helpers; use HTTP fakes or contract fixtures. Live ts-status API monitoring is allowed. Never publish TerraBase binaries.
- Use GitHub and `gh`; no polling loops.

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
- Changes limited to `AGENTS.md`/`CLAUDE.md` skip area Fast tests in CI. Mixed commits keep the gates for the changed code.
