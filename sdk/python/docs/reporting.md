# Usage reports

This source candidate covers project and database usage through
`terrascale.reporting`. Root-client integration awaits the
concurrent safety changes. Contract tests use HTTP fakes; live qualification
and package publication remain separate work.

```python
from terrascale.reporting import Reporting, UsageQuery

reports = Reporting(client)
query = UsageQuery(granularity="day",
                   from_time="2026-10-07T00:00:00Z", to_time="2026-10-08T00:00:00Z")
project = reports.get_project_usage(query)
database = reports.get_database_usage("main", query)
billed_units = project.month.units
```

For asyncio, use `AsyncReporting(async_client)` from the same module and await
each method. The arguments and result DTOs match. The existing client's
project authorization and token handling apply to every request.

Omit `UsageQuery` to retain the server's defaults. Reporting periods use UTC
hourly or daily buckets, with an inclusive `from` and exclusive `to`; the server
rounds boundaries to buckets. Counts and decimal-string integers become Python
integers without floating-point loss. Project database items have empty series;
the database report includes its own series. `file_bytes` is `None` if omitted.

Usage reads/writes are best-effort dashboard observations, not billed units.
`month` contains billed project units and organization units for the current
UTC month, including databases deleted since. `AnalyticsQuery` adds project
and database analytics over a fixed 1h/24h/7d/30d range, and
`get_project_metrics()` returns the Prometheus text exposition of the same
observations. `client.list_regions()` reads the anonymous serving-region
catalog without a bearer token.
