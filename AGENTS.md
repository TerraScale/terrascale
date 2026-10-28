# Landing Page Agent Rules

> [!IMPORTANT]
> **AI agents MUST NOT edit this file. Only humans may modify it.**

TerraScale public landing site: https://terrascale.tech

## Local CPU Priority

- Prefix local builds, tests, lints, benchmarks, dev servers, and watchers with `nice -n 19` (for example `nice -n 19 npm run build`).
- In CI (`CI=true`), use full priority. Never add `nice` or `renice` to CI workflows, Makefiles, scripts, or Containerfiles.

## Model Routing

- Short, bounded work (quick subagents, lookups, small edits) uses Claude Haiku 5.5, `claude-haiku-5-5`. If unavailable, use GPT-6.1-Sol through Codex, `gpt-6.1-sol`, after confirming the ID in the live model catalog.
- Never use Claude Haiku 4.5 under any ID (`claude-haiku-4-5`, dated variants included), and never fall back silently to an older model. If neither model is available, stop and report it.
- Claude Opus 5.5 (`claude-opus-5-5`) is the advisor and runs any task expected to take more than 10 minutes. The Haiku default never overrides this.
- Every T3 Code thread runs in full-access mode, subject to tool safety constraints.

## General

- Keep changes focused on the assigned task. Follow repository conventions and reuse established components and dependencies.
- Never add AI attribution, generated-by notices, or co-author trailers to commits, PR titles, or PR descriptions.
- Never invent URLs, commands, test results, or product claims.
- Preserve unrelated local changes. Never discard, overwrite, or commit another contributor's work.

## Stack

- SvelteKit + Svelte + TypeScript, built with Vite. Tailwind CSS uses the shared design tokens in the `@theme` block in `src/index.css`. Static prerendering uses `@sveltejs/adapter-static`. Deployment is Cloudflare Pages.
- Use the latest stable, mutually compatible releases; this file mandates no version numbers. Verify compatibility and migrations, and read the official docs for the installed version, before upgrading.
- Keep manifests and the committed lockfile in sync. Use the existing package manager and scripts. Do not add competing frameworks, package managers, or build pipelines.

## Rendering and Deployment

- The whole site MUST deploy as static HTML, CSS, JavaScript, and assets from `build/` via `@sveltejs/adapter-static`. Never swap in a server or platform-runtime adapter.
- Prerender in the root layout and generate every public page. Keep SvelteKit rendering on at build time; never set `ssr = false`.
- **Runtime SSR, application servers, Cloudflare Workers, and Pages Functions are prohibited.** No server actions, request-time API handlers, or features that need a deployed app runtime. Build-time data and generated static resources are fine when fully included in `build/`.
- Never disable strict prerender checks or add an SPA fallback to hide missing pages or build failures.
- Keep browser-only code out of build-time execution. Never put secrets or private credentials in client bundles or generated files.
- Changes that need a runtime server MUST get explicit human approval and an updated deployment plan first.

## Cloudflare: use the `cf` CLI

Use an authenticated `cf` CLI for the authorized account. Keep operator hostnames, account identities, and private topology out of this public repository.

- Use `cf` for DNS, zones, origin CA and client certificates, SSL/TLS, cache, rules, analytics, and API tokens. Workers deploys keep using `wrangler`.
- Before starting, run `cf auth whoami`; it must show `"authenticated": true`.
- Never print tokens or the config file. Show the plan before any change. Deleting zones, production DNS records, or Workers needs the user's confirmation. Record changed Cloudflare object IDs (never secrets) in the PR or runbook.

## Code and Design

- Organize routes, components, styles, and utilities by responsibility. Prefer small reusable Svelte components over duplicated markup or needless abstractions.
- Use the shared Tailwind theme, not copied tokens. Reuse committed brand assets; never create inconsistent logo variants.
- Preserve branding, typography, spacing, and interaction patterns unless the task changes them.
- Support mobile and desktop without unintended horizontal overflow. Respect reduced motion.
- Use semantic HTML, accessible controls, keyboard navigation, visible focus, and image alternatives.
- Keep primary content and navigation in the prerendered HTML.
- Keep page titles, descriptions, canonical URLs, and social metadata. Production canonical URLs MUST use `https://terrascale.tech`.
- Optimize assets and defer nonessential browser code. Avoid unnecessary dependencies and render-blocking third-party scripts.

## Development and Previews

- Every dev server MUST be reachable over Tailscale, with Vite HMR enabled and verified through the Tailscale URL.
- Give each active worktree its own dev and HMR ports. Never stop another worktree's processes or reuse its occupied ports.
- Preview URLs MUST identify the correct worktree or checkout. A shared Tailscale hostname with different ports is fine.
- Limit exposure to the intended Tailscale path. Allow the required hostname explicitly; never disable host validation globally.

Preview requests, in order:

1. Start the dev environment for the correct branch and worktree.
2. Get the real Tailscale DNS URL, with the port if needed.
3. Open it and verify the changed functionality, assets, and HMR.
4. Fix failures before sharing.
5. Send the verified URL.

Never send a localhost URL as a remote preview, invent a hostname, or present an unverified preview as working. Report unresolved access or verification blockers.

## Testing and Validation

### Required Checks

CI is the gate: no local test/check run is required before pushing or opening a PR.

### Coverage

- Add or update tests for changed behavior: navigation, calls to action, forms when present, and important interactive states.
- Check representative mobile and desktop layouts for visual changes.
- Verify changed routes by direct navigation and refresh, not only client-side navigation.
- Check for broken links, missing assets, hydration errors, and browser console errors.
- Validate the generated site with the repository's static-preview workflow. A working dev server alone is insufficient.

## Git

- Prefer a dedicated worktree per task (default, not mandatory). Use the existing checkout only when explicitly requested or when a worktree is impractical. Before switching branches or creating a worktree, inspect the working directory and preserve existing changes.
- Base every task branch on freshly fetched `origin/main`, never a stale local `main`. Never implement or push task changes directly on `main`. Keep each task's changes, commits, and PR isolated.
- Branch prefixes: `feat/{name}`, `fix/{name}`, `hotfix/{name}`, `refactor/{name}`.
- Remove a task worktree only when it is no longer needed and holds no uncommitted changes, unpushed commits, or files to keep.
- Use `git` for repository operations and the **`gh` CLI** for GitHub operations, including PR creation, reviewer requests, and status checks. Check CLI help when unsure; do not invent commands.

## Pull Requests

- Open a PR into **`main`** when the work is ready for review. Keep it focused, with a concise change summary and the validation performed.
- **Start from latest main.** Base every change on the most recent `main`: before starting, `git fetch origin` and branch from `origin/main`, never from a stale local `main`, another feature branch or an old worktree. Before every push, `git fetch origin && git rebase origin/main` again; right before merging, rebase once more and merge only when CI is green on that exact head.
- Never merge while required checks are pending or failing.
- Before finishing, verify the target branch, title, reviewers, previews, and current checks. Send the PR URL with the checks that passed and any unresolved blockers.

### PR previews (required for every visible change)

- Every PR that changes anything a user sees (pages, components, styles, layout, copy, emails, generated pages, maps, charts) MUST embed previews in the PR body. A visible change without previews is not ready to merge.
- Before and after screenshots of each changed page and state, desktop (1440 px wide) and mobile (390 px wide). Add a short video or GIF for interactions (toggles, menus, flows, animations).
- Capture from the real running build of the exact PR head (dev server, preview deploy or live preview), never mockups. Use synthetic or demo accounts; no secrets, tokens or real customer data on screen.
- Upload: commit the images to this repo's orphan branch `pr-assets` under `<pr-number>/` (first time: `git switch --orphan pr-assets`); never put them on the PR branch or `main`, never merge `pr-assets`. Embed each as `![<page> <state> after, desktop](https://raw.githubusercontent.com/TerraScale/ts-landing/pr-assets/<pr-number>/<file>.png)` and check that every URL returns 200 before posting.
- When a later push changes the visuals, replace the previews. PRs with no visible change say "No visual change" in the body.

## Zero configuration (user decision 2026-10-09, do not revert)

- Every feature is always on. No feature flags, on/off variables, opt-in settings, boolean enabled configuration keys, deploy toggles, or kill switches in code, config, or CI. Delete switches and their guarded paths.
- Every region always serves. A region starts serving as soon as its machines boot, with no per-region enable step.
- Production secrets live in the platform secret store and are never copied by hand. Vendor-issued keys (such as Stripe and Cloudflare) are set the same way.
- Values have one correct setting in code. Expose no tuning knobs. Fakes, demo stores, and skip-auth belong only in test code and test binaries.
- CI rejects new switch-style configuration. Only unavoidable third-party names get a narrow allowance, with a one-line reason.

This is the operating contract, not evidence that every runtime flow has shipped; see [README.md](README.md) for SDK and qualification gaps. It requires automatic public endpoint discovery; production publication and Cloudflare credentials come from the platform secret store. Existing production switches are inventoried for their owning implementation work. Documentation must not invent APIs or bypass authorization, tenant scope, registered callbacks, payment consent, or safety guards.
