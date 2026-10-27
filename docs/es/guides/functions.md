---
title: Functions
description: Deploy JavaScript functions next to a database, call them over HTTPS, run them on collection changes or a cron schedule, and read their logs.
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Functions

A function is one JavaScript file deployed next to a database. It runs when you call it over HTTPS, when documents in a collection change, or on a cron schedule, and it can write documents to its own database.

## Write a handler

A function is one ES2023 source file that defines `handler(event)` at the top level. It runs as a classic script, not as a module:

- Declare `function handler(event)`, `async function handler(event)` or `const handler = (event) => …`. An async handler's result is awaited.
- `import` and `export` (including `export default`) are refused at deploy with `400 invalid_request: source is not valid JavaScript`.
- CommonJS `module.exports` deploys but fails at invoke time with `500 function_error: module is not defined`.
- There is no module loader. To use ES module sources or packages, bundle them into one script and bind `handler` at the top level:

```sh
npx esbuild src/index.js --bundle --format=iife --global-name=fn --outfile=bundle.js
echo 'const handler = fn.handler' >> bundle.js
ts functions deploy example bundle.js -name hello
```

Here `src/index.js` uses `export function handler(event)`.

Each invocation starts with fresh state. The handler cannot do I/O: it has no network, file, environment or database read access. It returns a response and records writes with three calls:

| Call | Effect |
| --- | --- |
| `db.insert(collection, doc)` | Insert a document |
| `db.put(collection, id, doc)` | Write the document with this ID |
| `db.delete(collection, id)` | Delete the document with this ID |

Writes apply atomically after the handler returns successfully, and only to the function's own database. If the handler throws, nothing is written.

The `event` depends on what started the run:

| Trigger | `event` |
| --- | --- |
| HTTPS | `{trigger: "http", body, query}` |
| Collection change | `{trigger: "change", collection, changes: [{id, deleted, document}]}` |
| Cron | `{trigger: "cron", scheduledAt}` |

Return `{status, body}` to choose the HTTP status, or return any JSON value to send it with status 200. `console.log` lines are kept in the function's [logs](#read-logs-and-deliveries).

Function names are lowercase and at most 63 characters. Deploying again with the same name replaces the function.

## Deploy and call over HTTPS

Save this as `hello.js`:

```js
function handler(event) {
  console.log('hello called')
  return { status: 200, body: { message: 'Hello from TerraScale', received: event.body } }
}
```

Deploy it to the `example` database with the [CLI](/es/cli), then run it:

```sh
ts functions deploy example hello.js -name hello
ts functions invoke example hello -data '{"name":"Ada"}'
```

From your server, call the function over HTTPS with an [access token](/es/guides/authentication) exchanged from a project API key. Replace `{project}` with the project ID:

```sh
curl -X POST \
  "https://api.terrascale.tech/v1/projects/{project}/databases/example/functions/hello/invoke" \
  -H "Authorization: Bearer $ACCESS_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"Ada"}'
```

Invoking needs `documents:read`. Keep API keys and access tokens on your server, as the [authentication guide](/es/guides/authentication) describes.

## Run on collection changes

Deploy with a trigger collection to run the function whenever documents in that collection change. This function writes an audit entry to `audit` for every written or deleted document in `orders`:

```js
function handler(event) {
  for (const change of event.changes) {
    db.insert('audit', {
      collection: event.collection,
      documentId: change.id,
      action: change.deleted ? 'delete' : 'upsert',
    })
  }
  return { audited: event.changes.length }
}
```

```sh
ts functions deploy example audit.js -name audit-orders \
  -trigger-collection orders -events upsert,delete
```

`-events` accepts `upsert` (insert or update) and `delete`. v1 cannot tell inserts apart from updates, so an audit of inserts records updates too. A change-triggered function cannot write to its own trigger collection, which prevents loops.

Delivery is at least once, with exponential retries. A retried delivery runs the handler again, so the example above can record the same change twice; make your writes idempotent where duplicates matter, for example with `db.put` keyed by `change.id` when only the latest entry per document matters.

## Run on a schedule

Deploy with `-cron` and a five-field cron expression, evaluated in UTC. A schedule runs at most once per minute. This function writes a marker document every day at 03:00 UTC:

```js
function handler(event) {
  db.put('heartbeats', 'daily', { lastRunAt: event.scheduledAt })
  return { ok: true }
}
```

```sh
ts functions deploy example heartbeat.js -name heartbeat -cron "0 3 * * *"
```

The schedule runs on the server that accepted the deploy. A run missed while that server is unavailable is skipped, not replayed.

## Read logs and deliveries

Each invocation keeps its `console.log` lines, status, duration and error. The last 100 invocations are kept per function:

```sh
ts functions logs example hello -limit 20
```

For a change-triggered function, the deliveries view shows each delivery's state and retries, including deliveries that keep failing. The dashboard Functions page shows the same logs.

## Manage functions

```sh
ts functions list example
ts functions delete example hello
```

Deleting a function removes its trigger, schedule and logs. Deleting the database deletes its functions.

All paths are under `/v1/projects/{project}/databases/{database}`:

| Request | Purpose |
| --- | --- |
| `PUT /functions/{name}` with `{source, trigger?: {collection, events?}, cron?}` | Deploy or replace |
| `GET /functions` | List functions |
| `GET /functions/{name}` | Read one function, with its source |
| `DELETE /functions/{name}` | Delete the function, trigger, schedule and logs |
| `POST /functions/{name}/invoke` | Run with `event = {trigger: "http", body, query}` |
| `GET /functions/{name}/logs` | Invocation logs |
| `GET /functions/{name}/deliveries` | Change-trigger deliveries |

Deploy, list, read, logs and delete need an access token with both `documents:read` and `documents:write`; invoke needs `documents:read`. A function's writes run with the function's own authority, like a backend endpoint.

## Billing

Every invocation counts as one `invoke` operation in the database's usage, next to reads and writes. Documents a function writes count as normal document writes. See [pricing](/es/pricing/).

## Limits

| Bound | Value |
| --- | --- |
| Source | 256 KiB, one file |
| Functions | 32 per database |
| Run time | 2 s per invocation; 10,000,000 statements; recursion depth 256 |
| Memory | 32 MiB allocated per invocation |
| Input and output | 1 MiB request body; 1 MiB response |
| Writes | 25 document writes per invocation, 256 KiB per document |
| Logs | 50 lines of 1 KiB per invocation; last 100 invocations kept per function |
| Cron | At most once per minute |

## Current v1 limitations

- **JavaScript only.** One ES2023 source file per function.
- **No secrets yet.** Do not put credentials, API keys or tokens in function source.
- **No outbound requests.** Functions cannot call `fetch` or any other network, file or environment API, and cannot read documents.
- **Change events are `upsert` and `delete` only.** Inserts and updates are not separated.
- **Cron runs on the deploying server.** A run missed while that server is unavailable is skipped, not replayed.

</div>
