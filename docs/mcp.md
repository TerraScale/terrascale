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

Each request needs `Authorization: Bearer <personal access token>`. Users
create tokens on **Profile → Access tokens** (`/profile?tab=access-tokens`).
Only the token's SHA-256 digest is stored, and the secret is shown once.
Tokens expire after 7, 30, 90, or 365 days, and each account can hold at most
25 live tokens. A missing, unknown, expired, or revoked token returns `401`
with a `WWW-Authenticate: Bearer` challenge. A cross-origin `Origin` header or
`Sec-Fetch-Site: cross-site` returns `403`.

ts-auth tokens are not accepted until MCP OAuth (resource indicators and
client registration on ts-auth) ships; ts-auth's contract v1.1 leaves it as a
follow-up.

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

## Client configuration

```json
{
  "mcpServers": {
    "terrascale": {
      "type": "http",
      "url": "https://mcp.terrascale.tech/",
      "headers": { "Authorization": "Bearer tspat_..." }
    }
  }
}
```

## Adding tools

Tools live in `[McpServerToolType]` classes under `src/TerraScale.Dashboard`:
`Platform/Mcp/DashboardMcpTools.cs`
and `Platform/Mcp/DashboardDatabaseMcpTools.cs` for shared account, data and
credential tools, and a `*McpTools.cs` file in the owning feature slice (for
example `Features/Billing/BillingMcpTools.cs`). `AddDashboardMcp` in
`Platform/Mcp/DashboardMcp.cs` registers each class with `.WithTools<T>()`.
Native form handlers and tools call the same feature services, so both
surfaces share validation and permission checks.

```csharp
[McpServerTool(Name = "list_databases", Title = "List databases", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List the databases in a project with their status and region.")]
public Task<McpDatabaseList> ListDatabases(string? organizationId = null, string? projectId = null)
{
    var context = Scope(organizationId, projectId);
    DatabaseService.Scope(context);
    return Task.FromResult(new McpDatabaseList(context.Project!.Databases.Values.OrderBy(db => db.Name, StringComparer.Ordinal).Select(DatabaseOutput).ToArray()));
}
```

- Input schemas come from the method parameters; optional parameters have
  defaults. Output schemas come from the return type with
  `UseStructuredContent = true`.
- Accept optional `organizationId` and `projectId` and resolve them with
  `DashboardMcp.Scope` for tools that act on an organization or project.
- Throw `AccountAccessException` for rejected input or access. The call-tool
  filter in `DashboardMcp.cs` returns its message as an `isError` tool result.
- Set `ReadOnly`, `Destructive`, `Idempotent`, and `OpenWorld` accurately. They
  become the tool annotations clients use to decide what needs confirmation.
- A new tool class must be added to `AddDashboardMcp`.

The TUnit journeys `tests/TerraScale.Dashboard.E2E/*McpJourneyTests.cs`
(account, authentication, billing plan, database, onboarding, organization and
reporting) call the tools end to end over HTTP. `AuthAdminJourneyTests.cs`
checks that the `admin_*` tools are not listed or callable.
