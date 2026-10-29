# Cross-project authorization errors

The approved error contract when a credential touches data outside its reach, on every data
protocol. One rule: **a credential learns nothing about resources it cannot reach.** Another
organization's or project's database, a database outside the key's `database` restriction and a
database that does not exist all answer the same way.

## Decision

Wrong organization, wrong project and outside-restriction requests answer **`404
unknown_database`**, never `403`. A `403` would confirm that the resource exists and belongs to
someone else, which lets a key enumerate other tenants' project and database identifiers. `403` is
reserved for a resource the credential does reach but an operation it may not perform. This is
the contract; a QA expectation of "wrong project → 403" is not met by relabelling and must not be.

## HTTP data protocols (documents, HTTP SQL)

| Case | Status | `code` | Message |
| --- | --- | --- | --- |
| No credential, malformed, expired or revoked credential | `401` | `invalid_key` or the key status code | per [key-revocation.md](key-revocation.md) |
| Database of another organization or project, outside the key's `database` restriction, or absent; unknown or unbound project | `404` | `unknown_database` | `the database does not exist` |
| Database reached, scope missing (for example a write with a `documents:read` key) | `403` | `forbidden_scope` | names the missing scope |
| The key's IP allowlist refuses the client | `403` | `ip_not_allowed` | names the client address |

- The `404` holds for every route under `/v1/projects/{project}/databases/{database}`: documents,
  collections, export, changes and `sql/execute`. Whether
  the route names another project's database by name or by ULID, or names another project's ULID
  in the caller's own project route, nothing in the answer differs from a missing database, and
  nothing is read or changed there.
- Body shape: REST and HTTP SQL answer `{"error":{"code","message"}}`.
- `ip_not_allowed` and missing-scope answers come from the key's own restrictions and are decided
  before or after the tenant lookup in a way that never depends on another tenant's resources.

## PostgreSQL wire (`pg.terrascale.tech:5432`)

PostgreSQL has no HTTP status; the startup error is the contract.

| Case | SQLSTATE | Message |
| --- | --- | --- |
| Wrong or unknown key; database of another organization or project, outside the key's restriction, or absent; key without `documents:read` | `28P01` (fatal) | `password authentication failed` |
| Revoked or expired ts-auth key | `28000` (fatal) | `<status code>: …` per [key-revocation.md](key-revocation.md) |
| The key's IP allowlist refuses the client | `28000` | `ip_not_allowed: …` |
| Database reached, write statement with a read-only key | `42501` (`insufficient_privilege`) | names the missing scope |

The `28P01` answer is byte-identical for a wrong password and a foreign database, so a valid key
cannot tell another tenant's database from a missing one.

## Customer message

> `404 unknown_database` means the database does not exist **or your credential cannot reach
> it**: check the project ID and database name in the URL, and the key's project and database
> restriction. TerraScale never tells you which, so another project's resources stay invisible.
> `403` means the database is yours but the key lacks a scope or the request comes from an
> address outside the key's IP allowlist.

## Proof

- `tests/TerraBase.Fast/Management/CrossProjectErrorTests.cs`: foreign database by name and ULID,
  through the foreign and the caller's project route, against absent databases and projects, on
  collections, collection delete, export and HTTP SQL; the foreign database is
  unchanged afterwards.
- `tests/TerraBase.Fast/Postgres/PostgresWireTests.cs`
  `Wrong_keys_unknown_databases_and_unreached_databases_fail_alike`: `28P01` for all three.
- `tests/TerraBase.Fast/Management/ProjectTokenTests.cs`: `database`-restricted tokens and
  hidden management routes.
