# Changelog

## Unreleased

- `create_api_key` on the client and the async client no longer takes
  `step_up_proof`: ts-auth creates an API key without a confirmation proof and
  issues no proof for key creation. A call that still passes it raises
  `TypeError` before any request.
- `execute(Statement(...), args)`, and `query` called that way, on the client,
  the async client and both SQL sessions raise `TypeError` before any request:
  a `Statement` carries its own arguments. Earlier versions dropped `args`.
- A client that holds an API key (`api_key=` or `TERRASCALE_API_KEY`) with a
  `base_url` other than `https://api.terrascale.tech` needs
  `issuer=`, the ts-auth issuer that exchanges the key, and raises `ValueError`
  before any request without it. Earlier versions sent the key to
  `https://auth.terrascale.tech` for every `base_url`, in the token exchange
  and `create_scoped_key`. An `issuer` with
  user info, a query or a fragment raises `ValueError` before the key is sent;
  earlier versions appended `/oauth/token` to it as given. The DB-API `connect`
  and the SQLAlchemy URL query take `issuer` too.
- `TerraScale` and `AsyncTerraScale` raise `ValueError` when both `api_key=` and
  `token=` are passed, like the other SDKs, and read `TERRASCALE_API_KEY` only
  when neither is passed. Earlier versions sent the token and kept the key for
  `create_scoped_key`.
- `rotate_api_key(organization, key, step_up_proof=, project=, grace_seconds=)`
  rotates an organization key, or a project key with `project`, through
  ts-auth's `POST /v1/terrascale/organizations/{organization}[/projects/{project}]/api-keys/{key}/rotate`
  with a `rotate_api_key` step-up proof and the platform user's token, and
  returns `ApiKeySecret`. The former `rotate_api_key(key_id, grace_seconds)`
  called `/v1/terrascale/api-keys/{key}/rotate`, which ts-auth does not serve,
  and always failed; `RotatedApiKey` is gone.
- `base_url`, `issuer` and the origin of the platform calls follow the origin
  rule every SDK shares (`sdk/SQL-API.md`): https, or http only for
  `localhost`, `127.0.0.0/8` and `[::1]`; no user info, query, fragment or path;
  an issuer is the ts-auth origin and one tenant segment, `terrascale` or a
  project id; a `terrascale.tech` host only for its own service, a cell's
  `<cell>.auth.terrascale.tech` included. The string is checked as written:
  whitespace, `%`, `\`, a non-ASCII character, a trailing dot, an IPv4 address
  in any form but four decimal octets, port `0` and a port with a leading zero
  are refused, and httpx, which sends the request, must read the same host and
  port. So are a string of more than 2,048 characters, a name of more than 253
  characters or with a label of more than 63, a label that begins or ends with
  a hyphen or begins with `xn--` (internationalised names), the name `loopback`
  and the unspecified addresses `0.0.0.0` and `[::]`; earlier versions accepted
  an `xn--` host or a 64-character label and then raised a raw `idna` error or
  `UnicodeEncodeError` on every call. The client raises `ValueError` when it is
  built. Earlier versions kept
  a `base_url` path prefix, query or user info, and the platform calls
  (`create_api_key`, `list_api_keys`, `revoke_api_key`, the project client,
  settings and user calls) sent an issuer's user info as HTTP Basic in place of
  the bearer token.
- Added the optional `sqlalchemy` extra (`pip install terrascale[sqlalchemy]`): a
  `terrascale://<project_id>/<database>` SQLAlchemy dialect and PEP 249 DBAPI
  over the SQL HTTP API, running in autocommit — each statement is one request
  and a multi-row insert is one batched request. The API key is read from
  `TERRASCALE_API_KEY` or supplied as the URL password, never a query
  parameter. The dialect now defaults to real transactions over the SQL
  WebSocket session (implicit begin, commit/rollback, `begin_nested()`
  savepoints), limited to 5 seconds each by the server;
  `isolation_level="AUTOCOMMIT"` keeps the previous one-request-per-statement
  HTTP path.
- Alembic migrations run through the dialect: the `terrascale` dialect name is
  registered with Alembic's SQLite implementation when Alembic is installed, so
  `render_as_batch=True` table-copy migrations and autogenerate reflection work
  over the SQL API. Documented in the Use SQLAlchemy and Alembic guide.
