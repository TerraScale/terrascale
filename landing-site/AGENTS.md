<!-- shared-rules:start -->

## Shared TerraScale rules

### Shipping code

- Ship every change as a pull request to `main`. Fetch `origin` first and branch from fresh `origin/main` in your own worktree or clone, outside shared checkouts. Commit small changes; no AI attribution. Ignore outside contributors' PRs unless asked.
- Record non-obvious choices in the commit message as `DECISION: <choice>. Reason: <why>`.
- Before pushing, build or type-check only the project you changed. GitHub CI on the PR is the gate. A PR squash-merges as soon as its checks are green and its review passed; rebase it when it conflicts. Never push directly to, force-push or delete `main`.
- Deploy only green main. If your merge makes main red, fix or revert within about 30 minutes. For a red main you did not cause, wait for its owner's fix and rebase.
- Never run anything on or SSH to mariogk-desktop.
- StorageFormat.cs bumps and production resets need the user's approval; otherwise put `Needs format bump` in the commit message.

### Code

- No comments: no `//`, `#`, `/* */`, `///` doc comments or commented-out code in code, scripts or build files, including code you move or rewrite. Names and types carry the meaning; non-obvious choices go in the commit message as `DECISION:`.
- Fix the root cause. No workarounds: no retries, sleeps, longer timeouts, swallowed errors, special cases, flags or skipped or weakened journey steps that hide a symptom. If the cause is outside your task or outside our code, report it instead of patching around it.
- No legacy: no deprecated paths, compatibility shims, fallbacks, old-format readers, dead code or copies kept "for now". Replace the old path and delete it in the same change.
- No tests but the journey. The only test is the user-journey E2E suite in terrascale: it runs on every terrascale PR against a throwaway environment and after every release against production. Never add unit, integration, contract, Fast or Slow tests, test projects, fakes or fixtures in any repo; delete any you find. A behaviour that matters becomes a journey step. Production monitors (ts-orchestrator `multi-region`, `deploy-lag`, `app-canary`) are not tests; keep them. ts-orchestrator also keeps its single-host E2E (job `e2e-m1`) as its PR gate.

### CI and proof

- One PR workflow per repo (`ci.yml`); its last job is `all-green`, the one required check. Release, deploy and monitor workflows stay separate.
- CI must be as fast as it can be: no test jobs, no duplicate builds, cached toolchains, jobs in parallel.
- The shared CI-time check (`ci-time.yml` in `TerraScale/.github`) runs after every CI run. It compares working time, the longest chain of time a runner spent on the jobs, not time spent waiting for a runner. A run is slow when that time is more than 30 s over the median of the last 5 successful runs with the same jobs, or a job is more than 30 s over the slowest of its own earlier runs. An issue opens when that slowness repeats on the previous run with the same jobs, and never blocks a merge; investigate it and fix the cause like a bug.
- UI changes: screenshot every changed page before (`origin/main`) and after (your branch), same viewport, data and theme, in every supported theme. Save the PNGs outside the repo as `~/.local/state/shots/<branch>/<page>-before.png` and `-after.png`; never commit screenshots. Show them in your T3 thread: if you have T3 tools, `html_render` a before/after grid (absolute local image paths are inlined); otherwise embed each as a Markdown image with its absolute path, e.g. `![after: /settings](/home/<user>/.local/state/shots/<branch>/settings-after.png)`, in your final reply. List the pages in the PR body.

### Secrets and hard gates

- Runtime secrets: OCI Vault in gru via instance principal. CI secrets: GitHub Actions. Ask the user for dev secrets.
- Never expose secret values in chat, arguments, history, logs, git or commits. Use stdin or a 0600 file. Credential installs are one-command handoffs the user runs.
- Ask first before new paid capacity, security permissions or credentials.
- ts-auth and ts-dashboard store data in TerraBase through ts-dotnet; keep the SDK current with TerraBase's API.
- Outside the monorepo, never download or run TerraBase binaries in CI or helpers. Live ts-status API monitoring is allowed. Never publish TerraBase binaries.
- Use GitHub and `gh`; no polling loops.

<!-- shared-rules:end -->

# ts-landing


- Public site: `https://terrascale.tech`. Use SvelteKit, Svelte, TypeScript, Vite and the shared Tailwind `@theme` tokens in `src/index.css`.
- Routes live in `src/routes/`; reusable components in `src/components/`; static assets in `static/`; build and validation helpers in `scripts/`.
- Install: `npm ci`. Build and static validation: `npm run build`. Asset refresh: `npm run icons:sync` and `npm run world-map:sync`.
- Ship static HTML, CSS, JavaScript and assets from `build/` using `@sveltejs/adapter-static`. Prerender every public page from the root layout. Keep SSR enabled at build time, strict prerender checks and no SPA fallback.
- No runtime SSR, application server, Workers, Pages Functions, server actions or request-time API handlers. Fully generated build-time resources are fine. A runtime server requires human approval and an updated deployment plan.
- Keep browser-only code out of build-time execution. Never include private credentials in bundles or generated files.
- Use current stable compatible dependencies; check installed-version docs and migration requirements before upgrades. Keep manifests and lockfile in sync; use existing scripts and avoid competing build pipelines or frameworks.
- Reuse small Svelte components, shared theme tokens and committed brand assets. Preserve branding, typography, spacing and interactions unless assigned to change them.
- Support mobile and desktop without horizontal overflow; respect reduced motion. Use semantic HTML, keyboard-accessible controls, visible focus and image alternatives.
- Keep primary content and navigation in prerendered HTML. Preserve titles, descriptions, social metadata and canonical URLs under `https://terrascale.tech`.
- Optimize assets, defer nonessential browser code and avoid unnecessary dependencies or blocking third-party scripts.
- Cover navigation, calls to action, forms and important interactive states. Check direct navigation and refresh, responsive layouts, links, assets, hydration and console errors. Validate generated output, not just the dev server.
- Every feature is always on. Do not add feature flags, activation settings, deploy toggles, kill switches or tuning knobs. Keep one correct value in code. CI allowances for unavoidable third-party switch names need a one-line reason. No fakes, demo stores or skip-auth.
- Do not invent product claims, APIs or URLs, or bypass authorization, tenant scope, registered callbacks, payment consent or safety guards. The contract requires automatic public endpoint discovery; README records SDK and qualification gaps and existing switches remain owning implementation work.
- Changes limited to `AGENTS.md`/`CLAUDE.md` run no build in CI.
