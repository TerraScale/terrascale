---
title: Realtime changes
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# Realtime changes

TerraScale offers two read paths for live data. The changes feed streams every committed change in a database over Server-Sent Events (SSE). A shape syncs one collection, optionally filtered, as snapshot pages followed by long-polled changes. Both need a token with the `documents:read` scope.

## Stream the changes feed

```bash
# TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
D="https://api.terrascale.tech/v1/projects/$PROJECT/databases/$DATABASE"
headers() { printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN"; }
curl -N -sS -H @<(headers) -H 'Accept: text/event-stream' "$D/changes?cursor=now&collections=posts"
```

Query parameters:

- `cursor`: a cursor from a previous event, or `now` to start at the current head. Without it, the stream starts at the oldest retained change.
- `collections`: comma-separated collection names, at most 64. A cursor is only valid with the same filter.

Any other query parameter returns `400`.

## Event format

The stream sends three event types:

```text
event: cursor
id: <opaque cursor>
data: {"cursor":"<opaque cursor>"}

event: change
id: <opaque cursor>
data: {"collection":"posts","id":"01JB7Q3V0Z8K4M2N6P9R5T1W6A","op":"upsert","version":"1729587600000123","origin":"3f9a0c2d4b1e8a77","document":{"title":"Hello"}}
```

- `cursor` is sent when the stream starts and every 15 seconds. It is also the keepalive.
- `change` carries `collection`, `id`, `op` (`upsert` or `delete`), `version` (a decimal string), `origin` (16 hex digits) and `document`.
- `document` is the document's current value, or `null` when it no longer exists. Several changes to one document can show the same current value.
- `error` is sent once before the server ends a stream it already started. Its data is the usual error body, such as `{"error":{"code":"cursor_expired","message":"..."}}`.

Every `id` is an opaque cursor. Store the latest one and do not parse it. When several events belong to one commit, only the last one carries an `id`.

Delivery is at least once. Deduplicate by `(origin, version, collection, id)`, or apply changes idempotently: store `document` under `id`, and remove the document when it is `null`. Changes from one origin arrive in commit order. Changes from different origins can interleave.

Collection and index changes are not in the feed. Neither are the documents of a dropped collection.

## Resume with Last-Event-ID

Reconnect with the latest event id in the `Last-Event-ID` header. It takes precedence over the `cursor` query parameter. Keep the same `collections` filter.

```bash
curl -N -sS -H @<(headers) -H 'Accept: text/event-stream' \
  -H "Last-Event-ID: $LAST_EVENT_ID" "$D/changes?collections=posts"
```

A stream ends after 30 minutes, or when the access token expires. Reconnect with a fresh token and `Last-Event-ID`.

## Errors and retention

| Status | Code | What to do |
| --- | --- | --- |
| `400` | `invalid_cursor` | Send exactly one `Last-Event-ID` header. |
| `410` | `cursor_expired` | Re-read your data, then start again with `cursor=now`. |
| `429` | `changes_busy` | Wait for `Retry-After` (1 second), then reconnect with backoff. |

The feed keeps changes for 7 days. A cursor older than 7 days, or one that cannot be decoded, returns `410 cursor_expired`. The same code can arrive as an `error` event during a stream. A database allows at most 128 open streams per server, so close streams you no longer read.

## Follow the feed in JavaScript

The SDK iterator parses SSE and reconnects normal EOF with its last cursor. Supply an SDK database handle from the [quickstart](/hi/getting-started/javascript), configured with a current-token provider. Other errors reach the caller; respect `error.retryAfter` (seconds) before retrying a safe read.

```js
// db is an SDK database handle configured with a current-token provider.
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

async function followChanges({ db, onChange, onReset, signal }) {
  let lastEventId = null
  let delay = 1000
  while (!signal.aborted) {
    let expired = false
    try {
      for await (const event of db.changes({ cursor: 'now', collections: ['posts'], lastEventId: lastEventId ?? undefined }, { signal })) {
        if (event.type === 'change') await onChange(event)
        if (event.cursor !== undefined) lastEventId = event.cursor
      }
    } catch (error) {
      throw error // Includes terminal auth, expired history and application failures.
    }
    if (expired) {
      lastEventId = null // The next request starts again at cursor=now.
      await onReset()
      continue
    }
    await sleep(delay)
    delay = Math.min(delay * 2, 30_000)
  }
}
```

The client token provider supplies a current access token on each reconnect. Apply changes idempotently before advancing the checkpoint; application failures stop consumption. Expired history stops here for an explicit rebuild.

Use the maintained [SDK follower](/examples/terrabase-js/follow-changes.mjs) for checkpoint persistence, cancellable overload backoff and expired-history rebuilding. It captures a fresh position before the full re-read and then replays from it, covering writes during that read. `onChange` must be idempotent; `onCheckpoint` saves only successfully applied positions. Use a deadline signal; 401/403, terminal SSE errors, malformed data and lost connections stop. Only safe 429/503 reads retry, waiting at least `retryAfter` seconds (numeric or HTTP-date Retry-After); application failures stop. A fresh authorized token comes from the client provider.

## Sync a filtered shape

A shape is a collection plus an optional `where` filter in the [query filter grammar](/hi/guides/queries). Start with `offset=-1`. The server answers `307` to the first snapshot page and sets the shape's handle.

```bash
S="$D/collections/posts/shape"
curl -sS -L -H @<(headers) -G "$S" \
  --data-urlencode 'where={"eq":["/status","active"]}' --data-urlencode 'offset=-1'
```

```json
{"shape_id":"a07ef7c2873b7b4c4fcbb88c9e682ca5","handle":"h1-5c1e0a9b7d2f4e36","offset":"2","next_offset":"4","covered_offset":"4","values_at":"4","up_to_date":false,"cursor_resume":"<opaque>","changes":[{"offset":"4","ops":[{"op":"upsert","id":"01JB7Q3V0Z8K4M2N6P9R5T1W6A","version":"<opaque>","document":{"status":"active"}},{"op":"delete","id":"01JB7Q3V0Z8K4M2N6P9R5T1W6B"}]}]}
```

Then repeat with the same `where`, `handle=<handle>` and `offset=<next_offset>`. Offsets move from snapshot pages (`snap.<S>.<P>`) to decimal commit boundaries. A `delete` op means the document was deleted or no longer matches the filter. Treat your copy as consistent only when `up_to_date` is `true`.

For live updates at a decimal offset, add `live=true`. The request waits up to 20 seconds for a commit. Send the `TB-Shape-Cursor` response header of the previous live response as `cursor`. The `TB-Shape-Handle`, `TB-Shape-Next-Offset` and `TB-Shape-Up-To-Date` headers mirror the body. `If-None-Match` with the `ETag` returns `304`.

To resume on another server without a handle, send `offset=-1` and the last `cursor_resume` as `cursor`. Use the `TB-Shape-Resume` header instead when the URL would be too long.

| Status | Code | What to do |
| --- | --- | --- |
| `409` | `must_refetch` | Discard the shape and restart at `offset=-1`. The body and `TB-Shape-Handle` name the current handle. |
| `414` | `uri_too_long` | Use a shorter filter. |
| `429` | `shape_busy` | Keep your state and retry with backoff. |
| `503` | `shape_too_large`, `unavailable` | Keep your state and retry with backoff. |

`GET …/shape/manifest` and `GET …/shape/keys` serve encrypted, presigned copies of the same shape bodies. They are optional: reading every offset from `GET …/shape` gives the same result.

## Live queries in JavaScript

`collection.subscribeShape()` in `@terrascale/sdk` 0.0.7 or newer runs the shape protocol for you. `state` maps each matching document id to `{ version, document }`. `upToDate` is `true` once the server confirms the latest commit.

```js
import { createTerraBaseClient, indexedDbShapeReplica, v1Filter } from '@terrascale/sdk'

const client = createTerraBaseClient({ project, baseUrl: 'https://api.terrascale.tech', token: () => getAccessToken() })
const posts = client.database('app').collection('posts')
const live = posts.subscribeShape({ where: v1Filter.eq('/status', 'active') }, {
  replica: indexedDbShapeReplica(`${userId}/posts/active`), // optional
  onUpdate: (s) => render([...s.state.values()].map((d) => d.document), s.upToDate),
})
// live.close() stops it. live.done rejects on an error that stops it.
```

- **Network drop:** the subscription keeps its documents and retries the same position with backoff, up to 30 seconds apart.
- **Token renewal:** pass `token` as a function. Each long-poll asks it for the current token, so renew the token before it expires. A `401` or `403` stops the subscription and rejects `done`.
- **`must_refetch`:** the subscription discards its documents, loads a fresh snapshot and calls `onUpdate`.
- **Read replica:** with `replica`, the last saved documents appear at once on the next start, with `upToDate` `false`, and stay readable while offline. `indexedDbShapeReplica(key)` stores them in browser IndexedDB. For React Native, pass your own `{ load, save }` that stores a string. Use one key per signed-in user and shape, and clear it on sign-out.

Writes always go to the server. Write with `collection.put()` or `create()` while online; the subscription then delivers the change. There is no offline write queue.

In browsers, the subscription follows the `307` bootstrap redirect itself and uses `fetch`, so it needs no server proxy.

</div>
