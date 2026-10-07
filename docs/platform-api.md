# Platform API

`https://platform.terrascale.tech` serves the platform REST API: what
ts-dashboard owns under the service ownership in the umbrella `AGENTS.md`,
namely plans, subscriptions, Stripe billing and spending caps.
Organizations, projects, members and API keys are ts-auth's API, and
databases, documents and usage metering are TerraBase's.

The OpenAPI 3.1 document is `GET /openapi.json`, generated at startup from
the routes below.

## Authentication

The API is a resource server under ts-auth's `docs/platform-contract.md`
(v1.1, §6.4). Every operation is an organization route and needs
`Authorization: Bearer <token>` with an organization token of ts-auth's
`terrascale` tenant:

- an ES256 `at+jwt` from the issuer's JWKS, `iss` the issuer, `aud` including
  `https://platform.terrascale.tech`, unexpired;
- `org_id` equal to the path's organization and no `project_id`; anything
  else answers `404`, so a project token reaches no route here (a route under
  `…/projects/{projectId}` would need that project's token);
- the operation's scope in `scope` (`403 insufficient_scope` otherwise):
  `billing:read`, `billing:write` (owners) or `usage:read`.

Users and the CLI get one by exchanging their sign-in token (ts-go
`tsauth.NewExchangeSource`). The dashboard's services still act as a user and
check roles too, so API key tokens (no user) are refused until the identity
store moves to ts-auth, and until then `org_id` must be a dashboard
organization id.

## Operations

Each operation is an MCP tool with a `Route` (see [mcp.md](mcp.md#adding-tools)),
so both surfaces share validation, permissions and schemas. The JSON body,
query parameters and path wildcards fill the tool's input; unknown fields are
refused, and only the path names the organization and project.

Routes are under `/v1/organizations/{organizationId}`:

| Operation | Route | Scope |
| --- | --- | --- |
| `list_plans` | `GET …/plans` | `billing:read` |
| `get_billing` | `GET …/billing` | `billing:read` |
| `list_invoices` | `GET …/invoices` | `billing:read` |
| `preview_plan_change` | `GET …/plan/preview` | `billing:read` |
| `create_checkout` | `POST …/checkout` | `billing:write` |
| `cancel_checkout` | `DELETE …/checkout` | `billing:write` |
| `change_plan` | `PUT …/plan` | `billing:write` |
| `undo_plan_change` | `DELETE …/plan/scheduled-change` | `billing:write` |
| `get_billing_portal_url` | `POST …/billing-portal` | `billing:write` |
| `set_spending_controls` | `PUT …/spending-controls` | `billing:write` |
| `get_usage` | `GET …/usage` | `usage:read` |

Errors use ts-auth's envelope, `{"error":{"code","message"}}`: `400
invalid_request`, `401 unauthorized`, `403 insufficient_scope` or
`forbidden_role`, `404 not_found` (also for an organization or project that is
not the token's), `409 conflict` and `503 temporarily_unavailable`.

## Hosts

The dashboard's Fly app serves all three hosts; `internal/app` routes
`platform.terrascale.tech` to this API and `mcp.terrascale.tech` to the MCP
server. Their Cloudflare DNS records (TS-599, not proxied) point at the app's
Fly addresses, and Fly issues their certificates:

| Host | A record | AAAA record |
| --- | --- | --- |
| `platform.terrascale.tech` | `fcd121d998f2b3d3fbc15fd3ff132a75` | `cd581edf889b93aeaa13100066230c19` |
| `mcp.terrascale.tech` | `7c190d17c1fb2a08798d2741ad318389` | `f4399a7c13ed854b17e1dea6fbaad3e7` |
