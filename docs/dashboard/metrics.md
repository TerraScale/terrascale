# Dashboard metrics

Dashboard metrics display observations from TerraBase and the billing authorities. Keep request counts, SQL read fair use, billable write units and storage samples distinct. A missing sample is unknown, never zero. A partial regional result must not appear as a complete global total.

## Database activity

TerraBase owns SQL reads, committed writes, request/error counts, latency and sampled storage. Database statistics are node-local observations in five-second windows; replica status separately describes regional copies and lag. Physical file bytes differ from live SQL storage and from backup bytes. See [database stats](../terrabase/database-stats.md), [replica status](../terrabase/replica-status.md) and [metrics export](../terrabase/metrics-export.md).

Requests include dashboard browsing. They do not establish billable operation units. Requests, query text, row values, credential material and tenant IDs must not become unbounded telemetry labels. Logs expose authorized request IDs and bounded observations without SQL bodies or secrets.

## Allowances and billing

SQL reads are free with plan fair use. Shape reads are free and unmetered. Committed write units count once where served; replication does not add billable units. Monthly write caps and paid-plan overage follow the published pricing contract and the owning usage service. See [operation allowances](../terrabase/operation-allowance.md) and [Stripe billing](stripe.md).

The dashboard owns plans, subscriptions, spending caps and internal billing reports. An estimate or alert is not a Stripe charge or evidence of service suspension. See [internal billing](internal-billing.md).

## Display

Stats show a label and value. Preserve unavailable values explicitly. Fetch authorized observations for the selected organization, project and database and retain their time window and source. Do not derive global convergence, durability or erasure from a count or a lag sample.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
