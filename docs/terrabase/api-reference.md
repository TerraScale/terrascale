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
  Management and the health probes, with the paths and operation ids in [the route table](api.md). Administrative
  operations and hidden legacy routes are left out. Scalar organizes tags under Management, Data and System.
- One security scheme: `TsAuth` (`Authorization: Bearer <ts-auth project access token>`), naming `auth-issuer`
  when set. As an HTTP bearer scheme, each `security` requirement is `{"TsAuth":[]}`. Required JWT scopes
  appear in the operation description and the `x-required-scopes` array; every listed scope is required in
  the token's `scope` claim. Each operation documents `401` and `403 forbidden_scope`. Existing
  `tb_` keys and membership credentials remain supported through hidden compatibility paths only.
- Operations have request and response examples; error descriptions list their machine codes. Every error
  response is the `ErrorBody` envelope (`{"error":{"code","message"}}`, see
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
