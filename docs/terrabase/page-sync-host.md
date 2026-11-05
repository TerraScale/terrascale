# In-process page sync host

TerraBase serves native page pull for its own databases, inside the TerraBase process (PIVOT R2). Region replicas use it to copy a database from its home (D5). Only page pull is served. The engine's push and its server-side apply are never used (D14).

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

- **Client change log collision.** Native sync metadata must use a different capture table from TerraBase's replicated capture log. The pinned client isolates its local log so the next incremental pull cannot replay home changes as local writes.

## Client and native artifact

Clients use the pinned upstream sync implementation. The integration client sends HTTP/2 and the peer credential. Both supported native architectures must export the required sync and WAL revision APIs. Version and symbol qualification belongs to the native build boundary; a successful HTTP route alone does not prove client compatibility.
