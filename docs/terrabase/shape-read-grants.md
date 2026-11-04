# Shape read grants and end-user tokens (F4)

App developers let their end users read table shapes straight from the browser. The database owner grants read on a
table; the developer's backend mints a short-lived end-user token; the browser reads the shape with it. TerraBase
checks the token offline and applies the grant on every read. Shape reads stay free.

## 1. Grants

A grant belongs to one table of one database. It is stored in the database's internal shape metadata,
replicated with its pages, and deleted with its database. Regional shape reads check the local replicated grant.
Grant changes and revocations are captured in CDC so backup replay preserves them.

| Route | Scope | Answer |
|---|---|---|
| `PUT /v1/projects/{project}/databases/{database}/tables/{table}/read-grant` | `databases:write` | `200` grant |
| `GET …/tables/{table}/read-grant` | `databases:read` | `200` grant, `404 no_read_grant` |
| `DELETE …/tables/{table}/read-grant` | `databases:write` | `204`, `404 no_read_grant` |
| `GET /v1/projects/{project}/databases/{database}/read-grants` | `databases:read` | `200 {"grants":[…]}` |

Body and grant: `{"table":"notes","where":"owner_id = :sub","columns":["id","owner_id","body"],"updatedAt":"…"}`.

- `where` is optional. It takes the same expressions as the shape `where` parameter (this table's columns, literals,
  operators, `CASE`, `CAST`, `COLLATE` and deterministic functions; no subqueries), plus one parameter, `:sub`, the
  end user's id from the token. Any other parameter is refused (`unsafe_where`).
- `columns` is optional: 1 to 256 column names. End users receive only these columns.
- `PUT` checks the grant against the table before it saves: an unknown table answers `404 unknown_table`, an unknown
  column `400 unknown_column`, an invalid condition `400 unsafe_where`.
- API keys and project tokens are not limited by grants.

## 2. End-user tokens

The developer's backend calls ts-auth with a project API key
([scoped end-user tokens](../auth/scoped-end-user-tokens.md)):

```http
POST /v1/terrascale/projects/{project}/end-user-tokens
Authorization: Bearer <project API key>

{"user_id":"01J…","database":"01J…","tables":["notes"]}
```

The JWT carries `sub`, `token_kind: end_user`, `project_id`, `database` and `tables`, and lives at most 15 minutes.
TerraBase validates it offline against ts-auth's published signing keys; no read calls ts-auth.

## 3. Enforcement

On `GET …/tables/{table}/shape`, `…/shape/keys` and `…/shape/manifest` with an end-user token:

1. The token must name the database (`database`, a ULID) or any request for another database answers `404`.
2. `table` must be in the token's `tables`, and the table must have a grant; otherwise `403 not_granted`.
3. The grant's `where`, with `:sub` bound to the token's subject as a string literal, is joined with the request's
   `where` as `(grant) AND (request)`. Each is validated on its own first, so a request cannot escape the grant.
4. Requested columns outside the grant's `columns` are dropped; with no `columns`, the shape carries every granted
   column. If none remain, `403 column_not_granted`.

Each end user's shape has its own handle, so cached bodies and epoch keys never cross users. A changed grant gives a new
handle; clients holding the old one get `409 must_refetch`. A grant whose condition no longer fits the table answers
`403 grant_invalid`.

End-user tokens cannot run SQL or reach management routes (`403`). An expired or tampered token answers `401`.

## 4. Tests

`tests/TerraBase.Fast/Shapes/ShapeGrantTests.cs`: an end user reads only their own rows and the granted columns, also
after live changes; an ungranted table, a table outside the token and an expired token are refused.
