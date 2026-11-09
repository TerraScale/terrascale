---
title: Monitoring
description: Usage counters, request analytics, Prometheus metrics, request logs and the status page.
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# Monitoring

TerraScale exposes what a project does through four read-only surfaces — usage counters, request analytics, Prometheus metrics and request logs — plus the dashboard's Usage page and the public status page. Every API route below needs the `databases:read` scope; exchange a project API key for an access token first, as in [getting started](/ar/getting-started/#exchange-the-key-for-an-access-token).

## Usage counters

```
GET https://api.terrascale.tech/v1/projects/{project}/usage
GET https://api.terrascale.tech/v1/projects/{project}/databases/{database}/usage
```

Query parameters: `granularity` (`hour`, the default, or `day`: UTC buckets), `from` (inclusive, rounded down to its bucket) and `to` (exclusive, rounded up; default now), as RFC 3339 instants. Without `from`, the period is the 24 hours or 30 days that end with `to`.

The project route sums every database and lists each one in creation order; the database route adds a series with one point per bucket. Each entry carries `storageBytes`, totals of `reads` and `writes` since the database was created, and the period's sums. The project answer also carries `month`: operation units of the current UTC month for the project and the organization.

Each SQL read statement is one read, however many rows it returns; each committed write statement is one write. Counters are best-effort dashboard usage, not billing data: they are saved every 10 seconds, and a crash loses the operations counted since the last successful save.

## Request analytics

```
GET https://api.terrascale.tech/v1/projects/{project}/analytics
GET https://api.terrascale.tech/v1/projects/{project}/databases/{database}/analytics
```

Query parameters: `range` (`1h`, `24h` the default, `7d`, `30d`), `database` (catalog id, on project routes) and `region` (observing region). Buckets are 1 minute, 1 hour, 6 hours and 1 day respectively; the current bucket is partial and empty buckets are included. The answer holds client requests by HTTP method, committed operations by type, errors, latency percentiles, sampled on-disk storage and client bandwidth. A response contains one regional catalog's observations; unobserved remote placement regions are omitted. Requests are bounded to 100 databases, 16,384 database-region buckets and 5 seconds.

## Prometheus

```
GET https://api.terrascale.tech/v1/projects/{project}/metrics
```

The same analytics as a Prometheus scrape target (text exposition version 0.0.4), covering every database the token reaches, per database and observing region: `requests_per_second`, `errors_per_second`, `operations_per_second` by operation kind and `latency_milliseconds` at quantiles 0.5, 0.95 and 0.99 — rates averaged over the last 5 complete UTC minutes — plus `storage_bytes`, the newest sampled on-disk storage of the last hour. Scrape with an oauth2 `client_credentials` API key holding `databases:read`.

## Request logs

```
GET https://api.terrascale.tech/v1/projects/{project}/databases/{database}/logs
```

The retained requests of the last 24 hours, oldest first, with an `after` cursor (a canonical uppercase ULID, recorded at least 30 seconds ago) and `nextCursor` to page; `gap` is true when requests after the cursor may have been discarded unread. Filter by status class with `status=2xx|3xx|4xx|5xx`. Each entry carries the route template, HTTP status, committed operations and latency; templates contain no row keys, SQL text or credentials. Query requests with a latency of 100 ms or more are marked as slow queries. Recording is best effort: pending observations can be lost on crash, and capacity omissions are reported since startup.

## Dashboard Usage page

The dashboard's **Usage** page shows the same usage per project and database: storage, reads and writes over a selected range, the current month's operation units, and the organization's allowance. Its **Spending alert** tab sets a monthly alert amount in USD for organization owners and admins; writes stop at the plan's monthly write cap.

## Status page

Current service health, per-region uptime and incidents live at [status.terrascale.tech](https://status.terrascale.tech). Check it before reporting an outage. There are no log drains; the request logs above are the record of what a database served.

</div>
