---
---
<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# Limits

These are the limits a request or a plan can hit. All SQL surfaces (HTTP, WebSocket, Postgres wire) share them and answer with the same error codes and SQLSTATEs. See [SQL](/hi/concepts/sql) for the SQL model and [errors](/hi/guides/errors) for retry behavior.

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

A query that stays inside result limits but scans or works past the plan's scan budget answers `query_canceled` (400) / 57014 with `query too expensive: it exceeds the work budget`; add indexes or a `LIMIT`, or narrow the query. A request whose statements together spend more than 5 s executing answers `query_canceled` (400) / 57014 with `the request exceeded its 5 s time limit; split the batch or narrow the query`. Only statement and commit execution counts toward the 5 s; time spent admitted, queued or waiting on the organization's limits does not.

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

Beyond the request, burst or concurrency limit, HTTP answers `429 tenant_busy` (SQLSTATE `53300`) with a `Retry-After` header; the Postgres wire answers SQLSTATE `53300`. Past the storage ceiling a write answers `429 quota_exceeded` (53100) over SQL and `429 storage_quota` over file storage; neither sends `Retry-After`, and the file-storage message names the used and allowed gigabytes and the next step. Past the database allowance a create answers `409 database_limit`. A database's point-in-time restore window reaches back at most its plan's retention; see [backups and restore](/hi/guides/backups-and-restore).

## Monthly operation units and SQL read fair use

| Plan | Operation units included | Write cap | SQL read fair use |
| --- | --- | --- | --- |
| Free | 50,000 | 50,000 | 500,000 |
| Starter | 250,000 | 2,500,000 | 2,500,000 |
| Pro | 2,000,000 | 20,000,000 | 20,000,000 |
| Team | 5,000,000 | 50,000,000 | 50,000,000 |
| Enterprise | 10,000,000 | No limit | No limit |

One unit is each started 4 KiB. At the cap (the allowance on Free) new writes, and at fair use new SQL reads, answer `429 quota_exceeded` (SQLSTATE `53100`) until the next UTC month or an upgrade. Shape reads stay available. See [pricing](/hi/pricing) for billing and overage.

</div>
