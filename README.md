# TerraScale Python SDK

The Python client for [TerraScale](https://terrascale.tech) TerraBase: documents,
queries, vector search, TTL, SQL, transactions and database regions. Sync and
asyncio, built on [httpx](https://www.python-httpx.org/). Python 3.10+.

```sh
pip install terrascale
# until the first PyPI release:
pip install "git+https://github.com/TerraScale/ts-python@v0.0.2"
```

## Quickstart

Create a database and an API key in the [dashboard](https://dash.terrascale.tech/)
(**Full access** if you use SQL or create collections), then:

```sh
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
```

```python
from terrascale import ConflictError, TerraScale, query as q

with TerraScale() as ts:                       # reads TERRASCALE_API_KEY / TERRASCALE_PROJECT_ID
    db = ts.database("main")
    try:
        db.create_collection("todos")
    except ConflictError as exc:
        if exc.code != "name_taken":
            raise
    todos = db.collection("todos")

    res = todos.create({"title": "Ship the Python SDK", "done": False})
    doc = todos.get(res.id)
    todos.patch(doc.id, merge={"done": True}, if_match=doc.version)

    for d in todos.items(q.eq("/done", True), sort=[q.desc("/title")]):
        print(d.id, d.data)
```

The API key is exchanged with ts-auth (`https://auth.terrascale.tech`) for a
short-lived project access token, cached and refreshed automatically. Keep keys
on your server and out of git. See [examples/quickstart.py](examples/quickstart.py).

### Async

This snippet reuses the `todos` collection created above.

```python
from terrascale import AsyncTerraScale

async with AsyncTerraScale() as ts:
    todos = ts.database("main").collection("todos")
    res = await todos.create({"title": "async"})
    async for d in todos.items(limit=100):
        ...
```

`AsyncTerraScale` has the same methods as `TerraScale`, awaited; `pages()` and
`items()` are async iterators.

## API

| Surface | Methods |
| --- | --- |
| Client | `TerraScale(api_key=, project=, token=, base_url=)`, `database(name)`, `list_databases()`, `create_database(name, home_region=, replica_regions=, all_regions=)` |
| Database | `info()`, `update(home_region=, replica_regions=, all_regions=)`, `delete()`, `branch(name)`, `create_collection(name)`, `list_collections()`, `collection(name)` |
| Documents | `get`, `create`, `put`, `patch(merge=\|ops=)`, `delete`, `list` |
| Queries | `query(filter, sort=, project=, limit=, cursor=, count=, aggregate=)`, `pages(...)`, `items(...)` |
| Vector | `search(path, vector, limit=, metric="cosine"\|"l2")` or `sort=[q.near(...)]` |
| Geo | `q.within_radius(path, lat, lon, meters)`, `q.within_box(path, south, west, north, east)`, `sort=[q.geo_near(path, lat, lon)]` |
| Aggregates | `query(filter, aggregate=q.aggregate(group_by, sum=, avg=, min=, max=), limit=)` answers `page.groups` |
| TTL | `get_ttl()`, `set_ttl("/expiresAt")`, `delete_ttl()` |
| SQL | `sql(select)`, `check_sql`, `explain_sql`, `execute(sql, args)`, `batch([...])` |
| Transactions | `with db.transaction() as tx: tx.create/put/patch/delete/assert_/assert_absent/assert_count` |

**IDs** are canonical ULIDs (`terrascale.new_id()`); the document key is the ID.
**Versions** are opaque: pass `doc.version` back as `if_match`. Use
`if_none_match_any=True` to create only when absent.

**Filters** (`terrascale.query`): `and_`, `or_`, `not_`, `eq`, `ne`, `lt`, `lte`,
`gt`, `gte`, `in_`, `nin`, `exists`, `prefix`, `contains`; paths are JSON Pointers
(`"/title"`) or `q.ID_PATH`. Sort with `q.asc`/`q.desc`. Patch ops: `q.set_`,
`q.unset`, `q.append`, `q.inc`, `q.add`, `q.remove`.

**Geo**: points are `{"lat": ..., "lon": ...}` objects (WGS 84 degrees).
`q.within_radius`/`q.within_box` are ordinary filters (a box with `west > east`
crosses the antimeridian). `q.geo_near` must be the only sort key: one page of
at most 100 documents, nearest first, each with `distance` in meters.

**Aggregates**: `q.aggregate(["/status"], sum=["/amount"])` groups by 0-4 paths;
each `terrascale.Group` has `key` (one value per path), `count`, and `sum`/`avg`/
`min`/`max` dicts keyed by pointer. `limit` bounds the groups; aggregates take no
`sort`, `project`, `cursor` or `count`.

**Regions**: `create_database("eu", home_region="fra", replica_regions=["gru"])`,
`db.update(all_regions=True)`. Writable databases require at least two distinct serving
regions: the home region plus at least one replica region. On creation, omitting
`replica_regions` uses all current serving regions, even if `home_region` is set.
`all_regions=True` also follows future serving regions. Creating with an explicit
`replica_regions=[]` returns HTTP 400.

For deletion, call `db.delete()` directly in the database's home region (await
for async clients), without a preceding placement update. Deletion covers every
replica, including all-regions placement. HTTP 204 confirms logical deletion and
local cleanup; remote erasure is retried durably and may complete later.
Updating to `replica_regions=[]` can temporarily disable document writes with
HTTP 409 `placement_needs_two_regions`; it does not provide supported
single-region placement.

**TTL** deletes documents whose member holds a past RFC 3339 UTC time, in an
hourly sweep; expired documents stay readable until then.

## Errors

Every server error is a `terrascale.APIError` with `status`, `code`, `message`,
`op` (failing transaction op), `retry_after` and `etag`. Common cases have
subclasses: `InvalidRequestError` (400), `AuthenticationError` (401),
`PermissionDeniedError` (403), `NotFoundError` (404), `ConflictError` /
`UniqueViolationError` (409), `PreconditionFailedError` (412), `RateLimitError`
(429), `ServiceUnavailableError` (503). A refused API key raises `TokenError`.

`APIError` (including its subclasses) and `TokenError` expose
`request_id: str | None` for support correlation in both sync and async clients.
It is the responding request's `X-Request-ID`, accepted only as one canonical
26-character uppercase ULID (first character `0`–`7`). Missing, malformed or
repeated IDs give `None`; IDs in response bodies are ignored. The ID is
diagnostic metadata, never proof of identity, ownership or authorization.
Existing error strings are unchanged; report `exc.request_id` separately and
keep credentials, URLs with query values and response bodies out of logs.

An invalid acknowledgement raises an `APIError` subclass with code
`invalid_response`, its HTTP status and request ID, rather than a raw parser
exception. Its fixed message omits response contents; the SDK does not retry it.
Malformed token success responses retain `TokenError` / `invalid_token_response`
and now carry the request ID too. Transport failures with no response have no ID.

Reads and read-only queries retry transport failures, 429 and transient 503s,
honouring `Retry-After`. Writes, including `if_match` writes, are never replayed
after an ambiguous failure. A transport failure, 5xx or malformed acknowledgement
raises `CommitUncertainError`; reconcile state before retrying. Transactions
cannot be changed or committed again after submission; context exit sends once.
Malformed read responses raise `InvalidResponseError`.

SQL `execute`/`batch` columns are `SQLColumn(name, decltype)` objects. SQL errors
retain `statement` and `sqlite_code` alongside the standard `APIError` fields.

## Development

```sh
uv venv && uv pip install -e '.[dev]'
pytest && ruff check src tests && mypy
```

Tests run against an in-memory HTTP fake (`tests/fake.py`) and check every
request against the published route list in `contracts/openapi-routes.json`
(`python scripts/refresh-contract.py` updates it). They never start a TerraBase
server. Releases: bump `src/terrascale/_version.py`, tag `vX.Y.Z` and publish a
GitHub release for it; the publish workflow uploads to PyPI with trusted publishing.

License: Apache-2.0.

`401 key_revoked` and `401 key_expired` raise `KeyRevokedError` and
`KeyExpiredError`, subclasses of `AuthenticationError` retaining `code` and
`message`. They never retry or refresh. Built-in sync and async token sources
drop cached tokens and remain refused, including during in-flight exchanges;
create a new source/client with a replacement key. Token endpoint
`invalid_client` is a terminal `TokenError`. Caller-owned providers must discard
refused tokens themselves. `503 auth_unavailable` follows the existing safe-read
backoff and `Retry-After` policy without discarding the cached token.
