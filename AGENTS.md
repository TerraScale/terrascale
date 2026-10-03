AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# Rules

FOR NOW WE MUST USE: 
TS_DASHBOARD_PRODUCTION_DEMO=1
Everything must work in memory account creation and etc

- Never add "Generated", "Co-Author" and similar to the PR or descript or commits

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
