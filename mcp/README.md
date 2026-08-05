# TerraScale MCP

`@terrascale/mcp` is the first, deliberately narrow TerraScale MCP server. It calls only the public typed `@terrascale/sdk`; it does not forward arbitrary requests or use private transports.

## Scope

The v1 tools are read-only: caller context, organizations/projects, databases, region placement and latency, usage, backups, and restore points. Project data always needs an explicit `projectId`. The public TerraScale API remains the authorization and tenant-isolation boundary.

Not exposed: every mutation; API-key or M2M credential operations; billing/payment operations; access-role changes; audit/notification administration; and platform administration. No response, resource, prompt, or log includes an access token.

## Configuration and run

Node 22+ is required. `TERRASCALE_API_BASE_URL` must be HTTPS (HTTP is accepted only for localhost tests). Requests time out after 10 seconds by default; set `TERRASCALE_MCP_REQUEST_TIMEOUT_MS` to an integer from 100 through 30000.

For stdio, the launching caller supplies its own public credential:

```sh
TERRASCALE_API_BASE_URL=https://api.example TERRASCALE_ACCESS_TOKEN=... node dist/main.js
```

For Streamable HTTP, set `TERRASCALE_MCP_TRANSPORT=http` and optionally `TERRASCALE_MCP_PORT` (default `3000`). It binds to `127.0.0.1` by default. A public `TERRASCALE_MCP_HOST` is rejected unless both `TERRASCALE_MCP_ALLOWED_HOSTS` (comma-separated exact Host values) and `TERRASCALE_MCP_ALLOWED_ORIGINS` (comma-separated origins) are configured; the official MCP DNS-rebinding protection is enabled for that allowlist. Terminate TLS and enforce network access at the proxy/ingress, forwarding only the approved Host and Origin values.

Each MCP session must send its caller's public `Authorization` header; the service has no configured service identity. `/mcp` is the MCP endpoint and `/healthz` and `/readyz` return plain, non-sensitive health responses.

The HTTP listener limits headers to 10 seconds, complete inbound requests and pending initialization to 15 seconds, request bodies to 10 seconds and 1 MiB, and idle keep-alive connections to 5 seconds. It does not use a socket inactivity timeout, so active SSE streams remain open. At most 100 initialized-or-pending sessions are retained; initialized sessions have a 15-minute idle limit and one-hour absolute limit. Override these three limits with `TERRASCALE_MCP_MAX_SESSIONS`, `TERRASCALE_MCP_SESSION_IDLE_TIMEOUT_MS`, and `TERRASCALE_MCP_SESSION_TTL_MS`. They retain only a credential fingerprint, are promoted/removed through the MCP SDK lifecycle callbacks, and are closed on expiration, DELETE, or transport close.

Structured stderr logs use OpenTelemetry-compatible timestamp/severity/body/attributes fields and never include credentials or API response bodies.

## Verify

```sh
npm run build:mcp
npm run test:mcp
```

The test suite checks MCP discovery, tool schemas, strict error/log redaction, public-boundary identity propagation across two project IDs, and the absence of SDK generic-transport imports. Live TerraScale API evidence still needs DevHost credentials and is intentionally environment-gated; no fake persistence is used.
