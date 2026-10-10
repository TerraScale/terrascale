# Analytics and replication (TS-762)

The Analytics page reads the selected project's time series, optionally scoped
to a database and region. All four ranges (`1h`, `24h`, `7d`, `30d`) are passed
to the provider; automatic bucketing belongs to TerraBase. Request and operation
types remain separate series. Storage sums the database-region gauges in the
latest bucket. The Overview p95
card reports the worst measured p95 in that bucket; percentiles are never summed
or averaged. Latency lines stay separate per database and region, with gaps for
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
come from ts-status through the TS-752 ts-go contract; missing latency is shown
as unknown, not zero. Reads have five-second deadlines and Overview reads its
three measurement sources concurrently. Native GET filters use the existing
navigation progress and an accessible loading status. Provider failures preserve
the page and filters, with a retry message instead of fabricated measurements.

## SDK integration gate

`core.MetricsReader` is the injectable read-only contract used by the UI and the
Playwright fake client. The current ts-go pin does not publish the TS-762
analytics/replication methods. `app.New` therefore leaves this reader unset and
displays explicit unavailable states. TS-752 is merged: region measurements are
wired through `sdk.Status.Latency` on the public status origin. Test fixtures
are only in test files; production and the in-memory demo never synthesize metrics.

When slice 2 merges, repin ts-go and implement this reader under
`internal/adapters/tsgo`, using those typed methods and the signed-in user's
access token. Wire it in `app.New`. Keep provider IDs unchanged; do not generate
UUIDs, add versioned types/routes, use direct HTTP fallbacks, or read TerraBase's
internal data. The SDK adapter and live verification are required before calling
this slice complete.

The public status source has one correct setting. The current `TS_STATUS_URL`
override and empty-value measurement-disable path are runtime removal gaps;
regional measurement is always available under the operating contract.
The public status read never receives the signed-in user's token.
