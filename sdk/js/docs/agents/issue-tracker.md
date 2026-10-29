# Issue tracker: GitHub

Issues for this repository live in GitHub Issues at `TerraScale/ts-js`.
TerraScale product tickets live in Ku (`ku` MCP tools, ku.mariogk.com).
Use `gh` for repository issues and pull requests. Never use Forgejo, `fj`, or `tea`.

## Conventions

- Infer the GitHub repository from the current repository's `origin` remote.
- Before any write, confirm that the selected repository is `TerraScale/ts-js`.
- Preserve existing issue content and labels unless the task calls for changing them.
- Use `gh -R TerraScale/ts-js` when the command runs outside the repository.
- Never put secrets, tokens, or private service details in an issue or comment.

## Repository issues (`gh`)

- Create: `gh issue create --title "..." --body-file <file>`
- Read: `gh issue view <number>` and `gh issue view <number> --comments`
- Search: `gh issue list --search "<query>" --state open`
- Comment: `gh issue comment <number> --body-file <file>`
- Add or remove labels: `gh issue edit <number> --add-label "..."` or `--remove-label "..."`
- Assign: `gh issue edit <number> --add-assignee <login>` (use `@me` for yourself)
- Close with an explanation: `gh issue close <number> --comment "..."`

Use `--body-file` for long issue bodies or comments.

## TerraScale tickets (Ku)

- Create: `create_issue`; read: `get_issue`; search: `list_issues`; update: `update_issue`
- Comment: `create_comment`; read comments: `list_comments`
- Blocking relations: `create_relation`, `list_relations`
- Labels: `list_labels`, `create_label`, `update_label`

Keep the same wayfinding conventions below for Ku tickets. Do not mirror Ku tickets into GitHub Issues.

## Request surface

Pull requests for this repository are on GitHub. Open them ready for review, never as drafts.
Run CI on GitHub as the gate. Keep at most one open pull request per repository per thread.
The `terrascale` and `ts-dashboard` repositories merge through the merge queue
(`gh pr merge --auto --squash`); this repository merges directly once CI is green on the exact head.
Ready-for-review pull requests need no reviewer approval; fix or answer every review comment.

## When a skill says "publish to the issue tracker"

Create a GitHub issue with `gh issue create` in the repository inferred from `origin`.
For a TerraScale product ticket, create it in Ku instead.

## When a skill says "fetch the relevant ticket"

Read the issue body, comments, labels, state, and assignees with `gh issue view --comments`.
For a Ku ticket, use `get_issue` and `list_comments`.

## Wayfinding operations

The map is one issue. Its child tickets are separate issues.

- Label the map `wayfinder:map`.
- Put `Part of #<map>` at the top of each child issue.
- Label each child `wayfinder:<type>`, where the type is `research`, `prototype`, `grilling`, or `task`.
- Record dependencies as `Blocked by: #<number>, #<number>` at the top of the child body (for Ku, also create a blocking relation).
- A ticket is available when it is open, has no open blocker, and has no assignee.
- Claim a ticket by assigning the current GitHub user (`--add-assignee @me`) before starting work.
- Resolve a ticket by posting the answer, closing it, and adding its result to the map's Decisions-so-far section.
