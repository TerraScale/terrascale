# Dashboard integration API

The dashboard serves a session-authenticated integration API at
`https://dash.terrascale.tech`. Its [interactive reference](https://dash.terrascale.tech/reference/)
and [OpenAPI document](https://dash.terrascale.tech/openapi/integration.json)
cover the explicitly annotated integration routes. The committed snapshot is
`contracts/dashboard/integration.openapi.json`; it is checked against the running
application in `IntegrationContractJourneyTests`.

## Authentication and requests

Sign in through `/login`, which redirects to ts-auth. These browser integrations
use the HttpOnly `ts-dashboard-session` cookie. A personal access token authenticates
[MCP](mcp.md), not these routes. Never copy a browser cookie into source control,
chat or a shared request log.

Read operations return JSON. Mutations use the media type documented for the
operation, generally `application/x-www-form-urlencoded`, and require the current
page's `_csrf` token and same-origin browser context. Membership and role checks
run for each action. Follow documented `303` redirects after forms; do not interpret
the redirect itself as a JSON response. Missing sessions return `401`; rejected
CSRF and access checks return the operation's documented `400` or `403` response.
Do not assume every browser error has the same JSON envelope: consult each
operation's response/media-type contract.

## Covered operations

The current reference includes session identity, organization/project selection,
project API-key creation/rotation/revocation/CIDRs, conditional document writes,
and document-change streams. It is a partial integration contract, not an inventory
of every dashboard page or form endpoint. API-key creation and rotation return
secrets once: store them securely and never include their response bodies in logs.

Use each list operation's own continuation fields and limits; no universal page
parameter exists. Document-change streams use their documented SSE reconnect
contract. Responses carry `X-Request-Id` for troubleshooting; preserve it with the
HTTP status and a sanitized error. Do not infer an idempotency guarantee from an
internal Stripe request or MCP tool annotation. A caller-supplied `Idempotency-Key`
is not a published guarantee for these browser routes. After an uncertain mutation,
inspect current state before retrying, especially for credentials and billing.
No public rate-limit budget or rate-limit-header contract is established here;
respect `Retry-After` when supplied and never automatically replay an uncertain write.

## Public service APIs and removed routes

The former Go platform billing API at `platform.terrascale.tech`, including
`/openapi.json` and `/v1/organizations/{organizationId}` billing/plan/usage routes,
is not implemented by the current application. Do not use its historical bearer
JWT examples for the session integrations. Restoring a public, role-protected
platform contract and matching SDKs is tracked separately (TS-1104).

Organizations, projects, members and project credentials belong to ts-auth;
databases, documents and usage producers belong to TerraBase. Use their published
service contracts and official SDKs for programmatic access. Dashboard-owned
billing operations are available through the dashboard and its authenticated
[MCP tools](mcp.md). Public APIs retain version `v1`; their authentication and
schemas must be documented by the owning service.
