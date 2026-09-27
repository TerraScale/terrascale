# MCP server

The dashboard is an MCP server at `POST /mcp`. It speaks the stateless
Streamable HTTP transport of MCP `2026-07-28` through the official
[Go SDK](https://github.com/modelcontextprotocol/go-sdk) with
`StreamableHTTPOptions{Stateless: true, JSONResponse: true}`:

- No `initialize` handshake or `Mcp-Session-Id`. Each request carries its own
  protocol version and client capabilities, and clients discover the server
  with `server/discover`. Clients on `2025-11-25` still connect through the
  SDK's legacy handshake, answered statelessly.
- Every response is `application/json`. `GET` and `DELETE` return `405`.
- The server keeps no per-connection state. Tool calls read and write the
  same store as the dashboard pages.

## Authentication

Each request needs `Authorization: Bearer <personal access token>`. Users
create tokens on **Profile → Access tokens** (`/profile?tab=access-tokens`).
Only the token's SHA-256 digest is stored, and the secret is shown once.
Tokens expire after 7, 30, 90, or 365 days, and each account can hold at most
25. A missing, unknown, expired, or revoked token returns `401` with a
`WWW-Authenticate: Bearer` challenge.

A token acts as its user with that user's roles. Platform administrators also
see the `admin_*` tools, and the services recheck every permission on each
call. `tools/list` depends on the caller, so list results use
`cacheScope: "private"`.

Changing the password revokes every access token along with every session.
Assistants cannot create access tokens or change credentials. Password,
email, MFA, and passkey changes stay in the browser, so a leaked token can't
extend its own access.

## Scope

Tools are stateless, so each call picks its tenant. Scoped tools accept
optional `organizationId` and `projectId`:

- If both are omitted, the call uses the user's saved dashboard selection.
- `organizationId` alone uses that organization's default project.
- `projectId` alone finds the organization that owns the project.

Membership is checked on every call. `get_context` lists the organizations
and projects the caller can use.

## Client configuration

```json
{
  "mcpServers": {
    "terrascale": {
      "type": "http",
      "url": "https://<dashboard-host>/mcp",
      "headers": { "Authorization": "Bearer tspat_..." }
    }
  }
}
```

## Adding tools

Each feature slice registers its tools in its own `tools.go` from `Mount`,
next to its HTTP routes. HTTP handlers and tools call the same `Service`
methods, so both surfaces share validation and permission checks.

```go
core.AddTool(deps.Tools, core.ToolSpec{
	Name: "list_databases", Title: "List databases", ReadOnly: true,
	Description: "List the databases in a project with their status and region.",
}, func(_ context.Context, p core.Principal, _ core.Scope) (databaseList, error) {
	list, err := service.List(p)
	return databaseList{Databases: list}, err
})
```

- Input and output schemas are inferred from the Go types. Use `json` tags
  for names, `jsonschema` tags for descriptions, and `omitempty` for optional
  fields.
- Embed `core.Scope` in inputs that act on an organization or project.
- Outputs must be structs.
- Return `core.Invalid("reason")` for rejected input. The reason reaches the
  assistant as an `isError` tool result.
- Set `ReadOnly`, `Destructive`, `Idempotent`, and `OpenWorld` accurately. They
  become the tool annotations clients use to decide what needs confirmation.
- Set `Admin` to list a tool only for platform administrators.

`tests/e2e/mcp_*_test.go` cover the tools end to end with the SDK client.
