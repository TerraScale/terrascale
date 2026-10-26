---
title: Expire documents with TTL
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# Expire documents with TTL

A collection can name one member that holds each document's expiry time. Once that time passes, TerraScale deletes the document for you, so sessions, carts, one-time codes and caches need no cleanup job.

| Method | Route | Scope | Behavior |
| --- | --- | --- | --- |
| `PUT` | `…/collections/{collection}/ttl` | `documents:write` | Set or replace the expiry member; body `{"path":"/expiresAt"}` |
| `GET` | Same route | `documents:read` | `200 {"path":"/expiresAt"}`, or `{"path":null}` when documents never expire |
| `DELETE` | Same route | `documents:write` | Clear it; documents stop expiring |

## Set up the shell

```bash
export TERRASCALE_API_URL='https://api.terrascale.tech'
export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ID'
export TERRASCALE_DATABASE_NAME='YOUR_DATABASE_NAME'
# TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
DB="$TERRASCALE_API_URL/v1/projects/$TERRASCALE_PROJECT_ID/databases/$TERRASCALE_DATABASE_NAME"
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"; }
```

The collection must exist. Create one with `api -H 'Content-Type: application/json' -d '{"name":"sessions"}' "$DB/collections"`.

## Set, read and clear the TTL

```bash
api -X PUT -H 'Content-Type: application/json' -d '{"path":"/expiresAt"}' "$DB/collections/sessions/ttl" | jq
api "$DB/collections/sessions/ttl" | jq
api -X DELETE "$DB/collections/sessions/ttl" | jq
```

`path` is an RFC 6901 JSON Pointer of 1 to 16 object members, such as `/expiresAt` or `/session/expiresAt`. Anything else answers `400 invalid_ttl`. An unknown collection answers `404 unknown_collection`. A second `PUT` replaces the path.

## Write documents that expire

Store the expiry as an RFC 3339 time in UTC, ending in `Z`:

```bash
# Restore TTL after the clear example above.
api -X PUT -H 'Content-Type: application/json' -d '{"path":"/expiresAt"}' "$DB/collections/sessions/ttl" | jq
api -H 'Content-Type: application/json' \
  -d '{"user":"u_123","expiresAt":"2026-10-24T12:00:00Z"}' \
  "$DB/collections/sessions/documents" | jq
```

- A document expires when its member holds a UTC time earlier than now, such as `"2026-10-24T12:00:00Z"` or `"2026-10-24T12:00:00.250Z"`.
- Documents without the member, or with a number, `null` or any other value, never expire. Remove or rewrite the member to keep a document.
- Always write a complete UTC timestamp ending in `Z`. Date-only values, timestamps without a timezone and non-UTC offsets such as `+02:00` are unsupported TTL inputs. Validate expiry values in your application or declared collection schema; do not rely on unsupported timestamps being retained or deleted at a particular time.

## Lag: when expired documents disappear

Deletion is not instant. Each database's home region checks for expired documents **once an hour**, so a document disappears **up to about one hour after its expiry time**, and later while the home region is unavailable.

- Until then, an expired document is still readable and still matches queries. To hide it at once, filter on the member, for example `v1Filter.gt('/expiresAt', new Date().toISOString())`.
- Each expiry is an ordinary delete. It replicates to every region of the database, appears in the [changes feed](/ru/guides/realtime) as `op: "delete"`, and counts as a write in [usage](/ru/pricing/). The checks themselves are not billed as reads.
- A document rewritten after the check read it keeps its new version and is checked again next hour.
- In a large collection, [index](/ru/guides/indexes) the expiry member so each hourly pass stays within the query budget. A pass that fails is retried the next hour.

## From an SDK

JavaScript (`@terrascale/sdk` 0.0.5 or newer; 0.0.3 has no TTL methods), where `sessions` is a collection handle such as `client.database('app').collection('sessions')`:

```js
await sessions.putTtl('/expiresAt')   // { path: '/expiresAt' }
await sessions.getTtl()               // { path: '/expiresAt' } or { path: null }
await sessions.deleteTtl()            // documents stop expiring
```

Go (`terrascale.tech/sdk` v0.0.2 or newer), where `sessions` is `db.Collection("sessions")`:

```go
err := sessions.SetTTL(ctx, "/expiresAt")
path, err := sessions.TTL(ctx) // "" when documents never expire
err = sessions.DeleteTTL(ctx)
```

C# (`TerraScale.DotNet`):

```csharp
await database.SetCollectionTtlAsync("sessions", "/expiresAt", cancellationToken);
TerraBaseCollectionTtl ttl = await database.GetCollectionTtlAsync("sessions", cancellationToken); // ttl.Path is null when none
await database.ClearCollectionTtlAsync("sessions", cancellationToken);
```

</div>
