# Analytics and replication (TS-762)

The Analytics page reads the selected project's time series, optionally scoped
to a database and region. All four ranges (`1h`, `24h`, `7d`, `30d`) are passed
to the provider; automatic bucketing belongs to TerraBase. Request and operation
types remain separate series. The current bucket is partial, so Storage sums the
database-region gauges in the latest bucket where every database region was
sampled, and the p95 cards report the worst measured p95 in the latest bucket
with requests; percentiles are never summed or averaged. Items for a region no
longer in the database placement are kept as history, as TerraBase documents.
Read operations are Usage's reads; put, patch and delete are its writes. Latency lines stay separate per database and region, with gaps for
missing observations. Latency chart values are microseconds; headline latency is displayed in milliseconds.

Database filters restrict the offered regions to that database. Changing the
database clears a region carried over from another database in the same project;
unknown regions and unauthorized database IDs are rejected before provider reads.
The legacy reads/writes usage API exposes hourly or daily buckets: its `1h`
selection is the current UTC hour and is labeled accordingly in Usage and the
measured Overview activity chart. Analytics and replication pass `1h` to their
own provider APIs for automatic bucketing. Illustrative demo charts retain their
explicitly labeled rolling sample window.

Usage and database detail share the replication view. Each ordered region pair
keeps sent and received byte counts. The chart groups sent bytes by direction
across databases. Total replication bandwidth counts sent bytes once, rather
than adding the destination's received bytes to the same transfer. This is the
provider's `replication_bytes` meter, separate from client bandwidth. Region
health, lag, divergence, convergence time and last applied are provider values.

Overview shows analytics, replication for the selected project, and every region
used by the selected organization. Database counts include all its authorized
projects, counting each database once per region. Region health and latency
come from ts-status's public `https://status.terrascale.tech/api/latency`
(`src/TerraScale.Dashboard/Features/Home/HomeMeasurements.cs`); missing
latency is shown as unknown, not zero. Reads have five-second deadlines and
Overview reads its three measurement sources concurrently. Native GET filters use the existing
navigation progress and an accessible loading status. Provider failures preserve
the page and filters, with a retry message instead of fabricated measurements.

## SDK integration gate

Paths are relative to `src/TerraScale.Dashboard`. `IAnalyticsBackend`
(`Features/Analytics/AnalyticsModels.cs`) is the read-only contract used by
the UI. Tests keep the real adapter and serve a test-only producer protocol
from `tests/TerraScale.Dashboard.E2E/Demo/TestReporting.cs`. Production never
synthesizes metrics.

SDK 0.0.3 source (`ff1d1126087d40d112ca27383fbb3111e320cb4a`) exposes usage,
but does not publish analytics/replication methods. TerraBase main
`e30ffdc287530551b161b0130220be53d3c0475e` serves project analytics in
`Features/Management/Usage/GetProjectAnalyticsEndpoint.cs`, requiring
`databases:read`. Its query contract is `range=1h|24h|7d|30d`, optional
`database` (catalog ID) and `region` (observing region). The response is
`ProjectAnalytics` with an unpaged `items` array; it is distinct from the
hour/day reads-and-writes usage response.

`Features/Analytics/AnalyticsBackend.cs`
therefore calls the producer's `/v1/projects/{projectId}/analytics` route over
HTTP with a fresh, explicitly requested `databases:read` project token from
the five-argument RFC 8693 exchange on every read; see
[runtime integration gates](csharp-migration.md#runtime-integration-gates).
It keeps provider IDs unchanged. When the SDK publishes typed methods, move
this adapter to them. Live verification is required before calling this slice
complete.

This analytics read alone accepts a response up to 64 MiB, matching the SDK's
default body bound. The bound applies to both the declared content length and
bytes actually streamed, under the existing five-second deadline. Authentication
and discovery envelopes retain their shared 64 KiB limit. This permits full
database-region series without treating normal dense responses as unavailable.

Parallel-safe HTTP contract coverage lives in
`tests/TerraScale.Dashboard.E2E/Features/Analytics/AnalyticsBackendContractTests.cs`.
It checks all four ranges, database-ID/region filters, current session/project
authority, fresh read tokens and exchange refusals that send no analytics
request. Execution against the real SDK and I3/I5 composition is still required;
the tests do not establish live producer qualification.

Historical: the Go application left its `core.MetricsReader` unset because the
ts-go pin lacked these methods, and read region latency through ts-go
`sdk.Status.Latency`.

The public status origin is fixed in code; there is no override or
measurement-disable setting. The public status read never receives the
signed-in user's token.
