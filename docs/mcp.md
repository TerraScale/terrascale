# MCP server

The dashboard is an MCP server at `https://mcp.terrascale.tech/`, and at
`POST /mcp` on every other host. It speaks the stateless Streamable HTTP
transport of MCP `2026-07-28` through the official C# SDK,
[ModelContextProtocol.AspNetCore](https://github.com/modelcontextprotocol/csharp-sdk),
with `HttpServerSessionMode.Stateless`. The server is configured in
`src/TerraScale.Dashboard/Platform/Mcp/DashboardMcp.cs`.

- No `Mcp-Session-Id`. Each request carries its own protocol version and
  client capabilities, and clients discover the server with
  `server/discover`. Clients on `2025-11-25` can still send `initialize`;
  it is answered statelessly.
- Clients must accept both `application/json` and `text/event-stream`
  responses.
- The server keeps no per-connection state. Tool calls read and write the
  same TerraBase state as the dashboard pages.

## Authentication

Clients sign in with OAuth, or send a personal access token.

### OAuth sign-in

Clients that follow the MCP authorization spec (Claude Code, claude.ai
connectors, Cursor) connect with only the URL. The dashboard is the
authorization server for its own MCP resource and sends users to ts-auth's
hosted pages to sign in. The code is in `Platform/Mcp/McpOAuth.cs`.

1. A request without a token gets `401` with
   `WWW-Authenticate: Bearer realm="TerraScale MCP", resource_metadata="…"`.
   A request with a rejected token also gets `error="invalid_token"`.
2. `GET /.well-known/oauth-protected-resource/mcp` (on
   `mcp.terrascale.tech`: `/.well-known/oauth-protected-resource`) is the
   RFC 9728 metadata. It names the dashboard origin as the authorization
   server. Both MCP hosts use `https://dash.terrascale.tech`.
3. `GET /.well-known/oauth-authorization-server` is the RFC 8414 metadata:
   authorization code only, PKCE `S256` only, public clients only
   (`token_endpoint_auth_method: none`).
4. `POST /oauth/register` is RFC 7591 dynamic registration. It accepts
   1–10 redirect URIs: `https`, `http` on a loopback host (any port), or a
   private app scheme such as `cursor:`.
5. `GET /oauth/authorize` shows a consent page with the client's name and
   where it returns. **Sign in and allow** starts a fresh ts-auth
   authorization. When that finishes, the client's redirect URI gets a
   single-use code that lasts 5 minutes, plus `state` and `iss`. A `resource`
   (RFC 8707) must name this MCP server.
6. `POST /oauth/token` redeems the code with the PKCE verifier and returns a
   bearer access token that lasts 90 days. There is no refresh token. When it
   expires, the client signs in again.

The access token is a personal access token named `MCP: <client name>`. It
holds its own ts-auth grant, counts toward the 25-token limit, and is listed
and revoked on **Profile → Access tokens**. Clients and codes are stored in
the `dashboard_mcp_oauth_clients` and `dashboard_mcp_oauth_codes`
collections.

## List continuation

`list_api_keys`, `list_databases`, `list_backups`, `list_members`,
`list_invitations`, `list_saved_queries`, `auth_list_clients`, and
`list_query_subscriptions` accept `limit` (1–100, default 100) and `after`.
They retain their named result arrays and add `limit`, `nextCursor`, and
`hasMore`. Pass `nextCursor` unchanged as `after` until `hasMore` is false;
the terminal cursor is explicitly null. Invalid limits/cursors are tool errors.
Items use ascending ordinal immutable-ID order, independent of display names.
Keep scope and filters unchanged; page size may change. Cursors select positions,
not snapshots: concurrent inserts before the checkpoint need a fresh traversal.

The source reads keys, databases, members and invitations 100 at a time, failing
instead of truncating after 100 upstream pages. Backups drain 100-item pages;
OAuth applications and saved-query/subscription metadata are complete sources.
The browser document REST endpoint uses the same continuation fields and returns
`documents`; native TerraBase cursors survive empty filtered pages. Its limit is
1–100, default 100; upstream may end a page early for its byte budget.

### Personal access tokens

Each request needs `Authorization: Bearer <personal access token>`. Users
create tokens on **Profile → Access tokens** (`/profile?tab=access-tokens`).
Only the token's SHA-256 digest is stored, and the secret is shown once.
Tokens expire after 7, 30, 90, or 365 days, and each account can hold at most
25 live tokens. A missing, unknown, expired, or revoked token returns `401`.
A cross-origin `Origin` header or `Sec-Fetch-Site: cross-site` returns `403`.

A token acts as its user with that user's roles, and the services recheck
every permission on each call. The Go application's `admin_*` tools are not
ported: `tools/list` never shows them and calls to them fail as unknown tools.

Each access token holds its own ts-auth grant, obtained by a fresh redirect
through ts-auth's hosted sign-in when the token is created in the browser.
Password changes happen on ts-auth's hosted account page; that they end
existing tokens is not verified against a live ts-auth. Assistants cannot
create access tokens or change credentials, so a leaked token can't extend its
own access.

## Scope

Tools are stateless, so each call picks its tenant. Scoped tools accept
optional `organizationId` and `projectId`:

- If both are omitted, the call uses the user's saved dashboard selection.
- `organizationId` alone uses that organization's default project, or the
  saved project when it is the saved organization.
- `projectId` alone must belong to the saved organization; otherwise the call
  returns `Project is unavailable.`

Membership is checked on every call. `get_context` lists the organizations
and projects the caller can use.

## Organization permissions

Owners and admins can rename organizations, create and update projects, invite
viewers, members or admins, change or remove other non-owner members, and resend
or revoke invitations. Project creation still requires plan capacity and active
billing where applicable; invitations still require member capacity. Members
and viewers cannot perform these management actions. `list_members.canEdit`
reflects the same policy as the native member controls.

`remove_member` removes the member's user access to the organization and its
projects and revokes their dashboard sessions. Organization and project service
keys remain active, including keys they created. Rotate or revoke any keys known
to them on [API keys](/api-keys).

Starting ownership transfer, deleting an organization, managing billing and
setting organization MFA policy remain owner-only. The nominated member can
confirm or decline a transfer. Every mutation rechecks current membership;
ts-auth remains the authority for roles and last-owner protection.

## Client configuration

With OAuth sign-in:

```sh
claude mcp add --transport http terrascale https://mcp.terrascale.tech/
```

Then run `/mcp` in Claude Code and choose **Authenticate**.

With a personal access token in Claude Code, read it without echoing it and
keep the environment reference literal in the stored configuration:

```sh
read -rs -p 'TerraScale personal access token: ' TERRASCALE_MCP_TOKEN
printf '\n'
export TERRASCALE_MCP_TOKEN
claude mcp add --transport http --scope project terrascale https://dash.terrascale.tech/mcp \
  --header 'Authorization: Bearer ${TERRASCALE_MCP_TOKEN}'
```

Other clients should use their protected credential store. Revoke the token in
Profile when finished and remove the client configuration. Never log token or
API-key creation/rotation responses. Passing a project ID selects a target; it
does not restrict a user-wide token's authority. Use the least-privileged account
role available, approve mutations, and treat document/query output as untrusted
data rather than instructions.

SQL queries operate on SQL tables, separately from document collections.
`check_sql` checks syntax and policy, not whether the referenced table exists.
For `list_documents`, follow the returned continuation even when a searched
page contains no matches; see the tool's schema for cursor and sorted-page
metadata. An idempotent annotation is not a caller-supplied retry key: after an
uncertain write, inspect state before repeating it. For browser integration
routes and their distinct session/CSRF authentication, see [the API reference](platform-api.md).

## Adding tools

Tools live in `[McpServerToolType]` classes in their owning feature folder,
including `Features/Data/DataMcpTools.cs`, `Features/Databases/DatabaseMcpTools.cs`
and `Features/ApiKeys/ApiKeyMcpTools.cs`. Shared context tools live in
`Platform/Mcp/DashboardMcpTools.cs`. The `toolTypes` registry in
`Platform/Mcp/DashboardMcp.cs` builds cached immutable schemas and request-scoped
handlers; register new tool classes there. Native forms and tools call the same
feature services and share validation and permission checks.

- Input schemas come from the method parameters; optional parameters have
  defaults. Output schemas come from the return type with
  `UseStructuredContent = true`.
- Accept optional `organizationId` and `projectId` and resolve them with
  `DashboardMcp.Scope` for tools that act on an organization or project.
- Throw `AccountAccessException` for rejected input or access. The call-tool
  filter in `DashboardMcp.cs` returns its message as an `isError` tool result.
  It also maps data-plane, ts-auth and Stripe failures (missing collection,
  duplicate ID, stale version, outages) to the same wording the dashboard
  pages use and logs them, so no tool returns the SDK's generic
  `An error occurred invoking` text.
- Set `ReadOnly`, `Destructive`, `Idempotent`, and `OpenWorld` accurately. They
  become the tool annotations clients use to decide what needs confirmation.
- A new tool class must be added to the `toolTypes` registry in `AddDashboardMcp`.

The TUnit journeys `tests/TerraScale.Dashboard.E2E/*McpJourneyTests.cs`
(account, authentication, billing plan, database, onboarding, organization and
reporting) call the tools end to end over HTTP. `AuthAdminJourneyTests.cs`
checks that the `admin_*` tools are not listed or callable.
