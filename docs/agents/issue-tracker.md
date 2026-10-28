# Issue tracker: GitHub Issues

Issues and specs for this repository live in GitHub Issues at `github.com/TerraScale/ts-landing`.

Use the `gh` CLI for all issue operations. Forgejo (`git.terrascale.tech`) is retired; do not use `fj`, `tea`, the Forgejo MCP server, or the Forgejo API.

TerraScale product tickets are tracked in Ku (`ku` MCP). Use Ku for those; use GitHub Issues for issues about this repository.

## Conventions

- Create, read, search, comment on, label, assign, and close issues with `gh issue`.
- Target the repository explicitly with `-R TerraScale/ts-landing` whenever the command may run outside this repository.
- Before any write, confirm that `-R` (or the current `origin` remote) points at `TerraScale/ts-landing`.
- Preserve existing issue content and labels unless the task calls for changing them.
- Use `--body-file` (or `-F`) for long issue bodies or comments. Use `-F -` to read from standard input.

## `gh` commands

- Create: `gh issue create -t "..." -F body.md`
- Read: `gh issue view <number>` and `gh issue view <number> --comments`
- Search: `gh issue list -S "<query>" --state open`
- Comment: `gh issue comment <number> -b "..."` or `-F comment.md`
- Add or remove labels: `gh issue edit <number> --add-label "..."` or `--remove-label "..."`
- Assign: `gh issue edit <number> --add-assignee <login>` (`@me` assigns yourself)
- Close with an explanation: `gh issue close <number> -c "..."` (add `-r "not planned"` when the issue will not be done)

## Pull requests as a triage surface

PRs as a request surface: no. Pull requests carry code changes; route requests through issues.

## When a skill says "publish to the issue tracker"

Create a GitHub issue in `TerraScale/ts-landing` with `gh issue create`.

## When a skill says "fetch the relevant ticket"

Read the issue body, comments, labels, state, and assignees with `gh issue view <number> --comments`.

## Wayfinding operations

The map is one GitHub issue. Its child tickets are separate issues.

- Label the map `wayfinder:map`.
- Put `Part of #<map>` at the top of each child issue.
- Label each child `wayfinder:<type>`, where the type is `research`, `prototype`, `grilling`, or `task`.
- Record dependencies as `Blocked by: #<number>, #<number>` at the top of the child body. `gh issue create --blocked-by <number>` and `gh issue edit --add-blocked-by <number>` also record the native relationship.
- A ticket is available when it is open, has no open blocker, and has no assignee.
- Claim a ticket by assigning the current GitHub user (`gh issue edit <number> --add-assignee @me`) before starting work.
- Resolve a ticket by posting the answer, closing it (`gh issue close <number> -c "..."`), and adding its result to the map's Decisions-so-far section.
