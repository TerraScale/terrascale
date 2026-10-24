# Alert emails

`POST /v1/{slug}/email/alert` uses the dashboard's existing ClientBasic email authority, as
`POST /v1/{slug}/email/allowance` does ([requests and allowances](requests-and-allowances.md)). The JSON body is:

```json
{ "organizationId": "<ULID>", "projectId": "<ULID>", "alertId": "<ULID>", "state": "firing",
  "ruleName": "Slow reads", "metric": "p95_latency_ms", "databaseName": "main", "threshold": 250, "value": 412.5 }
```

`state` is `firing` or `resolved`; `ruleName` has 1 to 100 characters without control characters; `metric` is
`p95_latency_ms`, `error_rate_percent`, `storage_gb` or `operation_units_percent`; `databaseName` is a database
name or null for the whole project; `threshold` and `value` are finite numbers. Anything else answers 400
`invalid_request`. An unknown organization, or a project outside it, answers 404.

ts-auth emails the organization's current owners ("[TerraScale] Alert firing: <rule>" or "Alert resolved: <rule>")
with a link to `https://dashboard.terrascale.tech/notifications/alerts`, and returns only
`{ "status": "accepted" | "retryable" | "unknown" | "rejected" | "no_recipients" }`. Durable per-recipient claims in
`email_alert_deliveries`, keyed by organization, alert and state, send one message per transition with the
allowance email's retry semantics.
