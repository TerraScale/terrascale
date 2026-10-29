# Project collaborators

A project collaborator has an explicit ts-auth grant to one project
(`viewer`, `member` or `admin`) and no organization role. ts-auth stays the
authority: the dashboard re-reads the grant on every request, so a demotion or
removal applies to existing browser sessions, personal access tokens and MCP
OAuth grants on the next call.

## What a collaborator can use

| Area | viewer | member | admin |
|---|---|---|---|
| Discover and select the granted project (pages, `get_context`, `select_organization`, `select_project`) | yes | yes | yes |
| Databases, collections and documents of that project (read) | yes | yes | yes |
| Document and SQL writes, files, functions, webhooks, imports | no | yes | yes |
| Project settings and rename | no | no | yes |
| Explicit project grants (list) | yes | yes | yes |
| Explicit project grants (set or remove) | no | no | yes |
| Project overview and project-scoped notifications | yes | yes | yes |
| Project authentication: overview, users, sessions, OAuth applications (read) | yes | yes | yes |
| Project authentication: settings, branding, social providers, user disable/enable/delete, session revoke, OAuth applications, directory export | no | no | yes |

Every row applies only to the exact granted project. Sibling projects in the
same organization return `404`, as do inactive, archived or MFA-gated
organizations. Platform workspaces never accept a project grant.

## What stays organization-only

Collaborators never see or change organization metadata or aggregates:
billing, plan, usage, members, invitations, audit, ownership, deletion and
organization-wide notifications. MCP returns these organization fields as
`null` for a collaborator (`kind`, `status`, `role`, `planId`, `planName`,
`billingStatus`, `deletionRequested`), and the overview shows organization
member counts as unavailable. Allowance notices belong to one organization and
only its owner sees them.

Collaborator reads never write organization state and never publish a plan.

## MCP

- `get_context` lists each accessible project with `role` (the effective role
  for that exact project) and `canWriteProjectData`.
- `get_profile` reports `projectRole` and `canWriteProjectData` for the scoped
  project; `role` is `null` without organization membership.
- `organizationId` alone picks the saved project, then the default accessible
  project, then the first accessible project by ID.
- `list_project_members`, `set_project_member_role` and
  `remove_project_member` manage explicit grants of the exact project.
  `set_project_member_role` only grants to existing organization members or
  changes an existing collaborator grant; it never invites. Removing a grant
  leaves organization-implied access and service keys as they are.
- `auth_*` tools use the same project role as the pages: reads need any role,
  changes need `admin`. `auth_create_client` validates its input and returns
  `step_up_required`: OAuth applications are created on the dashboard's OAuth
  applications page after hosted confirmation, which shows a confidential
  client's secret once.

See [MCP server](mcp.md) for transport, authentication and scope rules.
