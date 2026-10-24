---
title: Metrics, log drains and alerts
description: Scrape per-database metrics with Prometheus, ship request logs to an OTLP endpoint such as Grafana Cloud or Datadog, and get alert emails and webhooks.
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# Metrics, log drains and alerts

TerraScale measures every database: requests, errors, latency, document operations and storage. The dashboard shows them on the **Analytics** and **Logs** tabs. This page covers taking them into your own tools.

## Scrape metrics with Prometheus

Each project serves its metrics in the Prometheus text format:

```http
GET https://api.terrascale.tech/v1/projects/{project}/metrics
Authorization: Bearer <project access token>
```

The token needs the `databases:read` scope. A token narrowed to one database only sees that database. Prometheus can obtain and renew the token itself with the OAuth 2.0 client credentials grant. Create an [API key](/ru/guides/authentication) with `databases:read` in the dashboard; its client ID is the part before the first `_`:

```yaml
scrape_configs:
  - job_name: terrascale
    scheme: https
    metrics_path: /v1/projects/<project id>/metrics
    scrape_interval: 60s
    static_configs:
      - targets: ['api.terrascale.tech']
    oauth2:
      client_id: <API key prefix before the first _>
      client_secret: <API key>
      token_url: https://auth.terrascale.tech/terrascale/oauth/token
```

Every series has the labels `database` (name), `database_id` and `region` (the region that observed the requests):

| Metric | Meaning |
| --- | --- |
| `terrascale_database_requests_per_second` | Requests per second over the last 5 complete minutes |
| `terrascale_database_errors_per_second` | Error responses per second over the last 5 complete minutes |
| `terrascale_database_operations_per_second{kind}` | Committed document operations per second, by kind |
| `terrascale_database_latency_milliseconds{quantile}` | Latency at quantile `0.5`, `0.95` and `0.99`, from the newest complete minute with requests |
| `terrascale_database_storage_bytes` | Latest sampled storage of the database in that region |

Values are averaged per minute, so scraping more often than every 60 seconds returns the same numbers. They are observations for monitoring, not billing figures; your invoice uses the metered usage on the **Usage** page.

Graph the request rate of one database in Grafana or the Prometheus UI with:

```promql
sum by (database) (terrascale_database_requests_per_second{database="orders"})
```

## Ship request logs to your log platform

A log drain sends each project's request logs to an [OpenTelemetry](https://opentelemetry.io/docs/specs/otlp/#otlphttp) OTLP/HTTP logs endpoint, as JSON. Owners set it up in the dashboard under the database **Logs** tab:

1. Enter the full HTTPS URL of the logs endpoint, for example `https://otlp-gateway-<zone>.grafana.net/otlp/v1/logs` for Grafana Cloud.
2. Add the header your platform authenticates with, for example `Authorization` with `Basic <base64 of instance id:token>` for Grafana Cloud, or `DD-API-KEY` with your API key for a Datadog OTLP logs intake. The dashboard stores the value and never shows it again.

New requests arrive within about a minute. Each log record has:

| Field | Value |
| --- | --- |
| Resource `service.name` | `terrascale` |
| Resource `terrascale.project_id`, `terrascale.database_id`, `terrascale.database` | The database that served the request |
| `body` | `<METHOD> <route> <status> <latency>ms` |
| `severityText` | `INFO`, `WARN` for 4xx, `ERROR` for 5xx |
| Attributes | `http.request.method`, `http.route`, `http.response.status_code`, `terrascale.latency_ms`, `terrascale.operations`, `terrascale.region`, `terrascale.is_query` |

Routes are templates: they never contain document keys, query text or credentials. Delivery is best effort. A database that serves more than 100 requests between two deliveries ships only the newest 100 of them, and the drain does not backfill requests made before it was set up.

## Alerts

Owners create alert rules in the dashboard under **Notifications → Alerts**. A rule watches one database or every database of a project:

| Metric | Fires when |
| --- | --- |
| p95 latency (ms) | The highest per-minute p95 latency of the last 5 complete minutes is above the threshold |
| Error rate (%) | Errors as a share of requests over the last 5 complete minutes are above the threshold |
| Storage (GB) | The database's latest sampled storage, summed over its regions, is above the threshold |
| Operation units (%) | The organization's operation units this month are above that share of the plan allowance |

Rules are checked every minute. When a rule fires, organization owners get an email and the rule's webhook, if it has one, receives a `POST`. When the value is back at or below the threshold, the alert resolves and both are sent again with the resolved state.

The webhook body is JSON:

```json
{"type":"alert.firing","alertId":"01JB...","projectId":"01JA...","rule":{"id":"01JB...","name":"Slow orders","metric":"p95_latency_ms","threshold":250},"database":"orders","value":412.5,"at":"2026-10-24T12:00:00Z"}
```

`type` is `alert.firing` or `alert.resolved`; both carry the same `alertId`. Requests are signed exactly like [change webhooks](/ru/guides/change-webhooks#verify-the-exact-request-bytes): `X-TerraScale-Delivery`, `X-TerraScale-Timestamp` and `X-TerraScale-Signature` with the rule's secret, which the dashboard shows once when you create the rule. The webhook URL must be public HTTPS. A failed delivery is retried on the next checks, up to 3 attempts.

</div>
