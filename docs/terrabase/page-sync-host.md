# In-process page sync host

TerraBase serves Turso Sync page pull for its own databases, inside the TerraBase process (PIVOT R2). Region replicas use it to copy a database from its home (D5). Only page pull is served. Turso's push and its server-side apply are never used (D14).

## Endpoint

`POST /v1/sync/organizations/{organization}/projects/{project}/databases/{database}/pull-updates`

- **Private network only.** The route exists only on the internal listener. That listener binds this node's private mesh address and speaks HTTP/2 with prior knowledge. The public listener answers 404.
- **Internal auth.** Every internal-listener request must carry the peer credential (`X-TerraBase-Peer-Credential`). Anything else gets 401 before routing.
- **Per-database scope.** The organization, project and database in the path must resolve in this node's catalog, otherwise the answer is 404. A pull streams that one database's pages and nothing else.
- **Protocol.** The request is the official client's protobuf pull-updates request, in page mode with raw encoding. The answer is a length-delimited protobuf header (`server_revision`, `db_size`, `protocol = pages`) followed by one message per page. Errors before the first byte are TerraBase JSON errors. A failure after the header aborts the HTTP/2 stream, so a client never applies a truncated answer.
- **Refused requests (400 `unsupported_pull`).** Zstd encoding, MVCC logical pulls, a pinned `server_revision`, and page or query selectors (partial sync). Long polls are answered immediately: replicas learn about new revisions from NATS (D25, R3), and small answers arrive with the notice ([region replicas](region-replicas.md#how-a-replica-follows-its-home)).

## Revisions

`server_revision` is the durable revision `ts1:<incarnation>:<generation>:<frame>` from the revision ledger (R1). A client revision that the retained history cannot serve incrementally gets every page: bootstrap, an unrecognized revision, another incarnation, a revision ahead of history, an expired one, or a rewritten database. The client applies a full answer as a rebootstrap.

## Streaming

An answer streams in bounded batches. The pull plans against one read snapshot, writes the header, then reads at most 256 KiB of pages on a storage thread and writes and flushes them to the HTTP/2 stream before it reads the next batch. HTTP/2 flow control pushes back on the reads, so a pull holds about one batch in memory, whatever the database size. The batch is reserved from the memory budget with the plan. The revision index returns reservation it no longer needs after checkpoints trim it, so a tenant whose index once peaked can still pull. The read snapshot, its pooled connection and its storage admission are held until the last page is sent, so a slow replica holds them longer. A failure after the header, including a copy fault noticed after the last page, resets the stream.

## Status (work in progress, landed when the pivot halted)

- **Client change log collision.** Turso's sync client keeps its own local change log in a table named `turso_cdc`. TerraBase captures changes in a table of the same name on every database, and page pull copies it to the replica. The client then replays the home's captured changes as its own local writes. The first incremental pull fails with "failed to replay local change after remote apply: Corrupt database". `src/vendor/Turso.Native/patches/0006-sync-client-own-cdc-table.patch` moves the client's log to `turso_sync_cdc`. It is in the pinned combined release `turso-native-v0.8.2-fts-contract1-aegis-wal1-interrupt-synccdc1-20261029` (maintenance run 40945318877).

## Client and native artifact

Clients are Turso's official sync clients. The tests use the vendored upstream .NET client (`TursoSyncDatabase`). Its `HttpClient` must send HTTP/2 and the peer credential. The pinned native libraries for linux-x64 and linux-arm64 both export the sync client (`turso_sync_*`) and the WAL revision contract (`turso_terrascale_wal_*`).

## Proof

- Fast: `PageSyncTests`. The official client bootstraps, pulls inserts, an update and a delete, and pulls across a checkpoint. It then gets an empty pull. A bad peer credential, the public listener, a crossed or unknown database, and a partial pull are each refused. A copy fault after the header resets the stream, so the reader gets an I/O error instead of a clean end.
- Slow: `BoundedPullMemoryTests`. In its own process, a 706 MiB database (180,774 pages) is fully pulled through a server with a 32 MiB memory budget. Peak RSS growth during the pull stays under 64 MiB (measured 23–30 MiB on beta, 2026-11-04).
- Slow: `ThousandDatabaseSyncTests`. 1,000 databases are bootstrapped and pulled through the host by the official client, with zero wrong-tenant rows. A bad credential is refused.
