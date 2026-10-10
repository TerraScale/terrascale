AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# Rules

Production uses persistent state and ts-auth authorization. In-memory demos
and fakes belong only in test code and test binaries. Existing runtime demo
paths are implementation gaps, not deployment requirements.

- Never add AI attribution to commits, PR titles, or PR descriptions: no `Co-Authored-By:` trailers, no `🤖 Generated with [Claude Code]` or similar generated-by notices.

## Local CPU Priority

- When building or testing locally, run every build, test, lint, and benchmark
  command at low CPU priority by prefixing it with `nice -n 19` (for example
  `nice -n 19 make test-fast`). Local dev servers and watchers count too.
- In CI (`CI=true`) use full CPU priority: do nothing. Never add `nice` or
  `renice` to CI workflows, Makefiles, scripts, or Containerfiles; the low
  priority is applied by whoever runs the command locally.

## Stack
- We need to use GO and datastar
- The Dashboard must not depend on terrabase(project) it should depend on ts-go instead
- We deploy to fly.io
- We need to use the official stripe lib for payment


## Preview

- If the user requests a preview url to check the work that is current made you need to start the dev env and send the user a tailscale dns to access it and test if it works before sending to the user if it doesnt work fix it
- When running a dev server it must always be accesible via the tailscale net and it must have hmr turned for really fast development
- Make sure that the urls are different as we are using worktrees so the ports needs to be diff

# Ownership

## Owner of
- Authentication via ts-auth
- MCP endpoints and MCP logic so the user can interact with the dashboard with an AI
- Organizations, Projects, Users, Payments, Plans
- Stripe management

## NOT AN OWNER OF
- Usage metrics
- Regions like available regions their latency and etc (This comes from terrabase)

# State 
- Stateless all state must live in the database via ts-go

# Test
- We need to have a test that tests the whole flow from creating the account and then using the ts-typescript sdk to interact with the database and make sure it is working correctly


## Feature Slices

- Each product area owns its components and abstraction on different folders

## Design System
- We must use lightningcss to bundle to ship a single CSS file

## Brand Assets

- TerraScale product logos, brand marks, wordmarks, and favicons must be served
  from SVG files under `public/brand` or another committed asset file.
- Do not inline TerraScale logo or wordmark SVG markup in React components,
  route files, CSS, or generated templates.
- Shared brand components may wrap those files with `<img>` for sizing,
  accessibility, and reuse, but the SVG path/polygon data must stay in the asset
  files.
- Use the same file-backed brand assets consistently on auth pages, the app
  shell, dashboard surfaces, and metadata/icon declarations.

## Dotted Map

- Use `dotted-map` only for map visualization
- Lazy-load map code and generated map data on routes that need it.
- Provide accessible non-map controls as the canonical input.
- Add to SVG the information needed so to make it easier to use the map on the code

## Git

### Issue (required before any PR)
- Issues are tracked in ku.mariogk.com and not the forgejo issues
- Every PR title is `{ISSUE_ID}: {title}`, e.g. `DSH-167: Revamp usage page`.
- The user may give the issue in any of these forms; all resolve to an ID:
  - a ku link: `https://ku.mariogk.com/terrascale/issue/DSH-167` → `DSH-167`
  - the ID alone: `DSH-167` or `dsh-167` → `DSH-167`
  - the issue title, e.g. "Usage revamp": search ku (MCP `list_issues`
    with `search`) and use the match. If there are several matches, ask
    the user which one.
- If you have no issue at the start of a task, ask for one right away.
  Never push or open a PR without it, and never make up an ID.
- When the AI creates the issue, it must assign it to the user who is
  running the AI and move it to In Progress right away.

### Branches
- Branch from `origin/main` in a new worktree:
  `feat/…`, `fix/…`, `hotfix/…`, `refactor/…`.
- Always base work on `origin/main`, never the local `main` (it is often
  stale in worktrees). Run `git fetch origin` first, then branch, rebase, diff
  and compare against `origin/main`.
- Before pushing, run `git fetch origin` and rebase onto the latest
  `origin/main` so the PR never carries outdated code.

### Conflicts
Work has been lost while resolving conflicts. When a merge, rebase, cherry-pick
or stash apply hits conflicts:

- Stop before resolving anything. Do not pick `--ours`/`--theirs`, run
  `git checkout -- <file>`, `git reset --hard`, `git clean`, or force-push to
  make a conflict go away.
- Tell the user, for each conflicted file:
  - what our side changes and what `origin/main` changes (summarize the
    intent of each, with the commits/PRs involved),
  - exactly what would be lost under each possible resolution,
  - which side you recommend keeping and why (by default, keep what is already
    on `origin/main` and reapply our changes on top of it).
- Wait for the user to confirm the resolution before applying it.
- Combine both sides whenever possible instead of discarding one. If something
  really must be dropped, say so explicitly.
- Before resolving, save a backup ref (for example
  `git branch backup/<branch>-<date>`) so nothing is unrecoverable.
- After resolving, show the user the final diff of the conflicted files and
  confirm that build, lint and tests still pass.

### Pull requests
- Use `fj` for git and Forgejo.
- Before pushing: lint passes and the `fast` and `slow` tests are green.
- Target `main` and enable auto-merge once checks pass.
- Reviewers: `mariogk` and `terrascale-bot` (Forgejo rejects the PR author,
  so skip whichever one opened the PR).
- Visual changes: upload several before/after screenshots as PR
  attachments. Never commit them.
- Send the PR link to the user when done.

## Zero configuration (user decision 2026-10-09, do not revert)

- Every feature is always on. No feature flags, on/off variables, opt-in settings, boolean enabled configuration keys, deploy toggles or kill switches in code, config or CI. Delete switches and their guarded paths.
- Every region always serves. NetBird network membership admits a node automatically; it discovers every region, infers its own region from where it runs and starts serving without a per-region enable step.
- OCI Vault automatically creates, delivers and configures every first-party production secret. Vendor-issued keys (such as Stripe and Cloudflare) are stored once in Vault and picked up automatically; nobody copies secrets by hand.
- Values have one correct setting in code. Expose no tuning knobs. Fakes, demo stores and skip-auth belong only in test code and test binaries.
- CI rejects new switch-style configuration; ts-reviewer requests changes. Only unavoidable third-party names may have a narrow allowance with a one-line reason.

Discover authentication, database and orchestrator endpoints automatically; OCI Vault supplies service secrets and the vendor-issued Stripe keys.
