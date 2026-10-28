AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# Rules

Production uses persistent state and ts-auth authorization. In-memory demos, fakes, demo stores, and skip-auth belong only in test code and test binaries. Existing runtime demo paths are implementation gaps, not deployment requirements.

- No AI attribution in commits, PR titles, or PR descriptions: no `Co-Authored-By:` trailers, no "Generated with Claude Code" or similar notices.

## Local CPU Priority

- Locally, prefix every build, test, lint, benchmark, dev server, and watcher with `nice -n 19`, e.g. `nice -n 19 make test-fast`.
- In CI (`CI=true`), use full priority. Never add `nice` or `renice` to CI workflows, Makefiles, scripts, or Containerfiles.

## Stack

- Use Go and datastar. Deploy to OCI gru under ts-orch. Use the official Stripe library for payments.
- The dashboard must not depend on terrabase (project); depend on ts-go instead.

## Preview

- Preview request: start the dev env, confirm it works over Tailscale, fix anything broken, then send the user the Tailscale DNS name.
- Dev servers stay on Tailscale with HMR. Worktrees need distinct URLs and ports.

## Ownership

- Owns: ts-auth authentication; MCP endpoints and logic, so users can work with the dashboard through AI; Organizations, Projects, Users, Payments, Plans; Stripe management.
- Does not own: usage metrics; regions (availability, latency, etc.), which come from terrabase.

## State and Tests

- Stateless. All state lives in the database, accessed through ts-go.
- Keep a test covering the whole flow: create an account, use the ts-typescript SDK to interact with the database, verify the result.

## Structure and Design

- Each product area owns its components and abstractions in its own folder.
- Bundle with lightningcss into a single shipped CSS file.

## Brand Assets

- Serve TerraScale logos, brand marks, wordmarks, and favicons from SVG files under `public/brand` or another committed asset file.
- Never inline logo or wordmark SVG in React components, route files, CSS, or generated templates. Shared components may wrap the files in `<img>`; path and polygon data stays in the asset files.
- Use the same file-backed assets on auth pages, the app shell, dashboard surfaces, and metadata/icon declarations.

## Dotted Map

- Use `dotted-map` only for map visualization. Lazy-load map code and map data on routes that need them.
- Accessible non-map controls are the canonical input. Put in the SVG the data needed to use the map from code.

## Zero Configuration (user decision 2026-10-09, do not revert)

- Every feature is always on. No feature flags, on/off variables, opt-in settings, boolean enabled-config keys, deploy toggles, or kill switches in code, config, or CI. Delete switches and their guarded paths.
- Every region always serves. NetBird membership admits a node automatically; it discovers all regions, infers its own from where it runs, and serves with no per-region enable step.
- Discover authentication, database, and orchestrator endpoints automatically. Production runtime secrets come from OCI Vault through ts-orch using instance principal; CI secrets are GitHub Actions secrets. The complete secret inventory lives in the terrascale repository under `ops/secrets/`. Never put secret values in code or config.
- Values have one correct setting in code. Expose no tuning knobs.
- CI rejects new switch-style configuration. Only unavoidable third-party names may have a narrow allowance, with a one-line reason.

## Agent Model Routing

- Short, bounded delegated agent work: Claude Haiku 5.5, `claude-haiku-5-5`. If unavailable, use Codex GPT-6.1-Sol, `gpt-6.1-sol`, after confirming that ID in the live model catalog.
- Advisor work and tasks expected to take more than 10 minutes: Claude Opus 5.5, `claude-opus-5-5`. The Haiku rule covers only short, bounded work.
- Run every T3 Code thread in full-access mode, within the existing tool safety rules.
- Never use Haiku 4.5 (`claude-haiku-4-5`, or a dated ID such as `claude-haiku-4-5-20251001`), and never fall back silently to an older Haiku. If no allowed model is available, report it instead of substituting another.

## Git

### Branches

- Branch from `origin/main` in a new worktree: `feat/…`, `fix/…`, `hotfix/…`, or `refactor/…`.
- Never base work on local `main`, which is often stale. Run `git fetch origin` first, then branch, rebase, diff, and compare against `origin/main`.
- Before pushing, fetch and rebase onto the latest `origin/main` so the PR never carries outdated code.

### Conflicts

Work has been lost while resolving conflicts. On any merge, rebase, cherry-pick, or stash apply that conflicts:

- Stop. Do not use `--ours`/`--theirs`, `git checkout -- <file>`, `git reset --hard`, `git clean`, or force-push to make a conflict go away.
- For each conflicted file, tell the user what our side and `origin/main` each change (intent, commits, PRs), what each resolution would lose, and your recommendation with reasons. Default: keep `origin/main` and reapply our changes on top.
- Wait for the user's confirmation. Combine both sides when possible; state explicitly anything that must be dropped.
- Before resolving, save a backup ref, e.g. `git branch backup/<branch>-<date>`.
- After resolving, show the final diff of the conflicted files and confirm build, lint, and tests pass.

### GitHub Workflow

- Use `git` over SSH to `github.com` for source control and `gh` for GitHub PRs, repository issues, Actions and releases. All repositories live under `github.com/TerraScale`.
- Use Ku (`ku.mariogk.com`, via Ku MCP) for TerraScale tickets and `gh issue` for repository issues.
- Open ready-for-review PRs targeting `main`, never drafts. Keep at most one open PR per repository per thread.
- CI is the gate: no local test/check run is required before pushing or opening a PR. GitHub Actions uses GitHub-hosted runners only.
- **Start from latest main.** Base every change on the most recent `main`: before starting, `git fetch origin` and branch from `origin/main`, never from a stale local `main`, another feature branch or an old worktree. Before every push, `git fetch origin && git rebase origin/main` again; right before merging, rebase once more and merge only when CI is green on that exact head.
- Merge only when checks pass on the exact latest PR head.
- Use the merge queue: `gh pr merge --auto --squash`. Never bypass the queue or required checks.
- Use the T3 PR watcher for check and review updates; never run `gh` polling loops.
- Send the PR link to the user when done.

### PR previews (required for every visible change)

- Every PR that changes anything a user sees (pages, components, styles, layout, copy, emails, generated pages, maps, charts) MUST embed previews in the PR body. A visible change without previews is not ready to merge.
- Before and after screenshots of each changed page and state, desktop (1440 px wide) and mobile (390 px wide). Add a short video or GIF for interactions (toggles, menus, flows, animations).
- Capture from the real running build of the exact PR head (dev server, preview deploy or live preview), never mockups. Use synthetic or demo accounts; no secrets, tokens or real customer data on screen.
- Upload PNGs with `node scripts/upload-pr-images.mjs <PR-number> <file.png> [more.png ...]`; paste its Markdown into the PR body. The helper creates the orphan `pr-assets` branch on first use and preserves existing images. Use descriptive alt text and distinct filenames for each page/revision/viewport/theme.
- When a later push changes the visuals, replace the previews. PRs with no visible change say "No visual change" in the body.
