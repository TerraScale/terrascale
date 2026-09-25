```markdown
# Landing Page Agent Rules

> [!IMPORTANT]
> **AI agents MUST NOT edit this file. Only humans may modify it.**

## Project

This repository contains the TerraScale public landing site.

**Production URL:** https://terrascale.tech

## General

- Keep changes focused on the assigned issue; avoid unrelated rewrites.
- Follow repository conventions and reuse established components and dependencies.
- Never add AI attribution, generated-by notices, or co-author trailers to commits, PR titles, or PR descriptions.
- Never invent issue IDs, URLs, commands, test results, or product claims.
- Preserve unrelated local changes. Never discard, overwrite, or commit another contributor’s work.

## Stack

- **SvelteKit + Svelte + TypeScript**.
- **Vite** for development and builds.
- **Tailwind CSS**, with shared design tokens in the `@theme` block in `src/index.css`.
- **`@sveltejs/adapter-static`** for static prerendering.
- **Cloudflare Pages** for deployment.

### Dependencies

- Use the **latest stable, mutually compatible releases**. This file does not mandate specific version numbers.
- Verify compatibility and migration requirements before upgrading; do not assume the newest releases work together.
- Keep dependency manifests and the committed lockfile synchronized so installations and builds remain reproducible.
- Use the repository’s existing package manager and scripts.
- Do not introduce competing frameworks, package managers, or build pipelines.
- Consult official documentation matching the installed versions before changing integrations.

## Rendering and Deployment

- The entire site MUST deploy as static HTML, CSS, JavaScript, and assets from `build/`.
- Use `@sveltejs/adapter-static`; do not replace it with a server or platform-runtime adapter.
- Enable prerendering in the root layout and ensure every public page is generated.
- Keep SvelteKit rendering enabled during the build. Do NOT set `ssr = false` to enforce static hosting.
- **Runtime SSR, application servers, Cloudflare Workers, and Pages Functions are prohibited.**
- Do not introduce server actions, request-time API handlers, or features requiring a deployed application runtime.
- Build-time data loading and generated static resources are allowed when their output is fully included in `build/`.
- Do not disable strict prerender checks or introduce an SPA fallback to hide missing pages or build failures.
- Keep browser-only code isolated from build-time execution.
- Never include secrets or private credentials in client bundles or generated files.
- Changes requiring a runtime server MUST receive explicit human approval and an updated deployment plan before implementation.

## Code and Design

- Keep routes, components, styles, and utilities organized by responsibility.
- Prefer small, reusable Svelte components over duplicated markup or unnecessary abstractions.
- Use the shared Tailwind theme instead of duplicating design tokens.
- Preserve existing branding, typography, spacing, and interaction patterns unless the issue explicitly changes them.
- Reuse committed brand assets; do not create inconsistent logo variants.
- Support mobile and desktop layouts without unintended horizontal overflow.
- Use semantic HTML, accessible controls, keyboard navigation, visible focus states, and appropriate image alternatives.
- Respect reduced-motion preferences.
- Keep primary content and navigation present in prerendered HTML.
- Maintain page titles, descriptions, canonical URLs, and social-sharing metadata. Production canonical URLs MUST use `https://terrascale.tech`.
- Optimize assets and defer nonessential browser code. Avoid unnecessary dependencies and render-blocking third-party scripts.

## Development and Previews

- Every development server MUST be accessible through the Tailscale network.
- Enable Vite HMR and verify it works through the Tailscale-accessible URL.
- Assign each active worktree its own available development and HMR ports where separate ports are required.
- Never stop another worktree’s processes or reuse its occupied ports.
- Preview URLs MUST identify the correct worktree or checkout. A shared Tailscale hostname with different ports is acceptable.
- Limit development-server exposure to the intended Tailscale access path.
- Allow the required Tailscale hostname explicitly rather than disabling host validation globally.

When the user requests a preview:

1. Start the development environment for the correct branch and worktree.
2. Obtain the actual Tailscale DNS URL, including the port when required.
3. Open that URL and verify the changed functionality, assets, and HMR.
4. Fix failures before sharing the preview.
5. Send the verified URL to the user.

Never send a localhost URL as a remote preview, invent a hostname, or present an unverified preview as working. Report unresolved access or verification blockers.

## Testing and Validation

### Required Checks

Before pushing, opening or updating a PR, or merging:

- Formatting, lint, and type checks MUST pass.
- The production build and prerendering MUST succeed.
- All configured test suites MUST pass.
- **The `slow` suite is mandatory and blocking in this repository.** Run `fast` as well when configured.
- Results MUST apply to the current changes or exact commit being advanced.
- Rerun affected checks after subsequent changes.
- Never skip, disable, or weaken checks to bypass a failure.
- Never claim a check passed unless it actually ran successfully.

### Coverage

- Add or update tests for changed behavior.
- Validate navigation, calls to action, forms when present, and important interactive states.
- For visual changes, check representative mobile and desktop layouts.
- Verify changed routes through direct navigation and page refresh, not only client-side navigation.
- Check for broken links, missing assets, hydration errors, and browser console errors.
- Validate the generated production site using the repository’s static-preview workflow; a working development server alone is insufficient.

## Issues and Git

### Issue Required

- Track work in **Ku**, not Forgejo Issues.
- A valid Ku issue URL is required at the start of a development task.
- If none is supplied, ask for it before implementation.
- Extract and normalize the issue ID from the supplied URL.

Example:

`https://ku.mariogk.com/terrascale/issue/DSH-96` → `DSH-96`

Never invent an issue ID, push task changes, or open a PR without the corresponding issue.

### Branches and Worktrees

- **Prefer a dedicated Git worktree for each task.** Worktrees are the default approach, not a mandatory requirement.
- Fetch the latest `origin/main` and use it as the base for every new task branch.
- Do not branch from a stale local `main`.
- Use the existing checkout when explicitly requested or when a worktree is impractical.
- Inspect the working directory before switching branches or creating a worktree; preserve existing changes.
- Never implement task changes directly on or push directly to `main`.
- Keep each task’s changes, commits, and PR isolated.

Allowed branch prefixes:

- `feat/{feature-name}`
- `fix/{fix-name}`
- `hotfix/{fix-name}`
- `refactor/{description}`

Remove task worktrees only when no longer needed and after confirming they contain no uncommitted changes, unpushed commits, or other files that must be preserved.

### Tooling

- Use `git` for repository operations, including fetching, branching, worktree management, committing, and pushing.
- Use the **`fj` CLI** for Forgejo operations, including PR creation, reviewer requests, and status checks.
- Use Ku tooling for issue lookup and management.
- Inspect available CLI help when syntax is uncertain; do not invent commands.

## Pull Requests

### Requirements

- Open a PR into **`main`** when the work is ready for review and all required local checks pass.
- Use the title format **`{ISSUE_ID}: {title}`**.

Example:

`DSH-96: Improve landing page navigation`

- Include the Ku issue link, a concise change summary, and the validation performed.
- Request reviews from **`mariogk`** and **`terrascale-bot`**, excluding whichever account authored the PR.
- Keep the PR focused on its issue.
- Do not merge while required checks are pending or failing.

### Visual Changes

- Capture multiple useful before/after screenshots covering the affected layouts and states.
- Include mobile and desktop examples when the change affects both.
- Upload screenshots as PR attachments; do not commit images solely for PR documentation.
- Capture screenshots from the actual implementation, not mockups presented as completed work.

### Completion

- Verify the target branch, issue-prefixed title, reviewers, attachments, and current checks.
- Send the PR URL to the user.
- State which checks passed and identify any unresolved blockers.
```
