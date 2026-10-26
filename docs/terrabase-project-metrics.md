# Project Prometheus metrics

`documents.Project(projectId).GetPrometheusMetricsAsync(cancellationToken)` reads
`GET /v1/projects/{project}/metrics` using the existing project access-token provider.
The token needs `databases:read`; the project binding and database narrowing remain
server-enforced. A raw dashboard API key is not the project bearer token.

```csharp
TerraBasePrometheusMetrics scrape = await documents.Project(projectId)
    .GetPrometheusMetricsAsync(cancellationToken);
await destination.WriteAsync(scrape.Body, cancellationToken);
// When serving the bytes, preserve scrape.ContentType on the response.
```

The response preserves bytes and `text/plain; version=0.0.4; charset=utf-8`.
No JSON decoding, line normalization, metric parsing, pagination or background work
occurs. Each call sends once, caps the body at 8 MiB and covers token acquisition,
HTTP and body reads with a fixed 30-second deadline. Earlier existing client limits
still apply. Cancellation disposes the response; the caller owns its HttpClient.
Non-200 success or unexpected media fails with `invalid_response`; JSON failures
keep the usual status/code, request ID, regional hints and Retry-After without retry.

Rates average five complete UTC minutes; latency uses the newest complete minute
with observations, and storage the newest sample in the last hour. Gauges are
best-effort observations, not billing counters. The producer and public-contract
digests are pinned in `contracts/terrabase/project-metrics.json`; its sample is synthetic.

Live qualification remains pending: with one authorized account and exact SDK
commit/local pack, produce low-rate owned requests, scrape the same project/region,
preserve status/media and safely graph `terrascale_database_requests_per_second`.
Check read-only/database-narrowed credentials and cancellation, without interpreting
empty output or 401 as successful measurement. Reuse only the parent's fixture;
no signup, extra credentials or package publication is required by this API.
