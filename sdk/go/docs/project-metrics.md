# Project Prometheus metrics

`Client.ProjectPrometheusMetrics(ctx, projectID)` reads the project's raw
Prometheus 0.0.4 exposition using a project access token with `databases:read`.
The token's project binding and database narrowing remain enforced by TerraBase.
This is separate from JSON analytics and the operator's administrative metrics.

The result owns `Body` bytes and preserves the response `ContentType`. Do not
mutate shared result bytes. The operation sends once, without token refresh,
retry, redirect following, region selection or background scraping. It has a
fixed 30-second deadline including resolution/auth/read and an 8 MiB body cap;
earlier caller deadlines and existing smaller response bounds still apply.
API errors preserve status/code/headers and a bounded body; invalid success
responses return `PrometheusProtocolError`. Caller cancellation takes precedence.

Rates average the last five complete UTC minutes; latency uses the newest
complete minute with samples, storage the newest sample in the last hour.
Observations are best effort, flushed every ten seconds, and are not billing.
There is no pagination, query/body or per-operation configuration.

`contracts/project-metrics.json` pins the producer and public OpenAPI digests,
route, scope and synthetic exposition shared with .NET. Its region-pin note
describes existing producer authority; this Go method adds no region header/API.
Fixtures establish source/HTTP behavior; deployed scrape/graph proof remains
parent-owned and pending ingress restoration and an authorized own-project token.
