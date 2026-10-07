# API reference

TerraBase documents its public HTTP API with the built-in ASP.NET Core OpenAPI generator
(`Microsoft.AspNetCore.OpenApi`) and renders it with Scalar (`Scalar.AspNetCore`). The wiring lives in
`src/TerraBase/Features/ApiReference`.

| URL (client listener) | Answer |
| --- | --- |
| `GET /` | `302` with the relative `Location: scalar/` |
| `GET /scalar/` | Scalar page; its scripts are embedded in the binary and served below `/scalar/` |
| `GET /openapi/v1.json` | OpenAPI document |

For example, `https://gru.api.terrascale.tech/` opens the reference of the `gru` region. All three are anonymous
and served on the client listener only; the loopback admin listener answers `404`, like any route it does not serve.

## Document contents

- Only operations served on the client listener: data plane (Documents v1, queries, transactions, shape reads),
  Management and the health probes. Admin-only operations (`/admin/api-keys`, `/v1/node/pressure`) are left out.
- Security schemes: `ApiKey` (`Authorization: Bearer <tb_ API key>`, with the operation's scope in the
  requirement) and `TsAuth` (`Authorization: Bearer <ts-auth OIDC access token>`, naming `auth-issuer` when set).
  Authenticated operations document `401`, and API-key operations `403 forbidden_scope`.
- Every error response is the `ErrorBody` envelope (`{"error":{"code","message"}}`, see
  [error handling](error-handling.md)), and every client operation documents the region pin and `421`
  ([region identity](region-identity.md)).
- `info.version` is the build version; `servers` is the relative `/` (described with the node's region), so
  "Try it" targets whichever regional host or proxy served the document. No hostname is configured or hardcoded.

`ApiReferenceTests` in the Fast suite pins the operation list and security requirements: a new public endpoint
updates its snapshot, and an admin operation appearing there is a leak.

## Security headers

TerraBase sets no `Content-Security-Policy`. The Scalar page loads only same-origin resources: default fonts from
`fonts.scalar.com`, Scalar telemetry and its AI agent are off. A policy added at the edge needs
`script-src 'self' 'unsafe-inline'` (Scalar initializes from an inline module script),
`style-src 'self' 'unsafe-inline'` (Scalar injects styles at runtime), `img-src 'self' data:` and
`connect-src 'self'` for the document and "Try it" requests. This list comes from Scalar 2.17's page and
scripts, not from a browser run under an enforced policy: try such a policy in report-only mode first.
