---
---
<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Limits

These are the limits a request or a plan can hit. All SQL surfaces (HTTP, WebSocket, Postgres wire) share them and answer with the same error codes and SQLSTATEs. See [SQL](/es/concepts/sql) for the SQL model and [errors](/es/guides/errors) for retry behavior.

## SQL statements

| Limit | Value | Refusal |
| --- | --- | --- |
| Statements per request or `execute` frame | 1 to 256 | `invalid_request` (400) before anything runs |
| Statement text | 16 KiB (16,384 UTF-8 bytes) | `too_large` (413) / SQLSTATE 54000 |
| HTTP request body | 1 MiB | 413 `payload_too_large` |
| WebSocket frame | 1 MiB (the first auth frame 16 KiB) | close 1009 |
| Result size | 10,000 rows and 8 MiB | `too_large` (413) / 54000 |
| Rows changed per transaction | 1,024 | `too_large` (413) / 54000 |
| Database file growth per transaction | 8 MiB | `too_large` (413) / 54000 |

A query that stays inside result limits but scans or works past the plan's scan budget answers `query_canceled` (400) / 57014. Add indexes or a `LIMIT`, or narrow the query.

## Transactions

| Surface | Transaction limit |
| --- | --- |
| HTTP | One request is one atomic batch, all or nothing. `BEGIN`, `COMMIT` and `ROLLBACK` are refused. |
| WebSocket | `begin`/`commit`/`rollback` frames. At most 5 seconds, then it rolls back. |
| Postgres wire | `BEGIN`/`COMMIT`/`ROLLBACK` and savepoints. At most 5 seconds, then it rolls back. |

A transaction left open past the limit rolls back and answers `transaction_rolled_back` (400) / 40000.

## Postgres wire sessions

| Limit | Value | On exceed |
| --- | --- | --- |
| Startup | 10 seconds to finish the handshake, packets at most 10,000 bytes | connection closed (`08P01` on a bad packet) |
| Message size | 1 MiB per protocol message | connection closed (`08P01`) |
| Prepared statements | 256 per session | `54000` |
| Portals | 256 per session | `54000` |
| Catalog queries and `LISTEN`/`NOTIFY`, extensions | not supported | `0A000` |

## Requests per organization

| Limit | Free | Starter | Pro | Team | Enterprise |
| --- | --- | --- | --- | --- | --- |
| Requests per second | 50 | 200 | 500 | 500 | No limit |
| Burst requests | 100 | 400 | 1,000 | 1,000 | No limit |
| Concurrent requests | 4 | 8 | 16 | 16 | No limit |
| Query timeout | 1 s | 2 s | 5 s | 5 s | No limit |
| Scan budget, candidate rows | 10,000 | 50,000 | 100,000 | 100,000 | No limit |
| Scan budget, bytes scanned | 16 MiB | 64 MiB | 128 MiB | 128 MiB | No limit |
| Live databases | 1 | 5 | 25 | 100 | No limit |
| Storage ceiling | 0.1 GB | 5 GB | 100 GB | 100 GB | No limit |
| Point-in-time retention | 1 day | 3 days | 7 days | 14 days | 30 days |

Beyond the request, burst or concurrency limit, HTTP answers `429 tenant_busy` (SQLSTATE `53300`) with a `Retry-After` header; the Postgres wire answers SQLSTATE `53300`. Past the storage ceiling a write answers `429 quota_exceeded` (53100); past the database allowance a create answers `409 database_limit`. A database's point-in-time restore window reaches back at most its plan's retention; see [backups and restore](/es/guides/backups-and-restore).

## Monthly write units and SQL read fair use

| Plan | Write units included | Write cap | SQL read fair use |
| --- | --- | --- | --- |
| Free | 50,000 | 50,000 | 500,000 |
| Starter | 250,000 | 2,500,000 | 2,500,000 |
| Pro | 2,000,000 | 20,000,000 | 20,000,000 |
| Team | 5,000,000 | 50,000,000 | 50,000,000 |
| Enterprise | 10,000,000 | No limit | No limit |

One unit is each started 4 KiB. At the cap (the allowance on Free) new writes, and at fair use new SQL reads, answer `429 quota_exceeded` (SQLSTATE `53100`) until the next UTC month or an upgrade. Shape reads stay available. See [pricing](/es/pricing) for billing and overage.

</div>
