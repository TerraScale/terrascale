"""Region catalog and analytics/usage reporting over the HTTP fake."""

from __future__ import annotations

from fake import Fake

from terrascale import AsyncTerraScale, TerraScale
from terrascale.reporting import AnalyticsQuery, AsyncReporting, Reporting, UsageQuery


def test_regions_catalog_and_analytics(ts: TerraScale, fake: Fake) -> None:
    regions = ts.list_regions()
    assert regions.regions == ["gru", "sjc"]
    assert all("authorization" not in r.headers for r in fake.requests if r.url.path == "/v1/regions")
    reports = Reporting(ts)
    analytics = reports.get_project_analytics(AnalyticsQuery(range="24h", region="gru"))
    assert analytics.bucket_seconds == 3600
    assert analytics.items[0].database_name == "main"
    assert analytics.items[0].series[0].requests == {"GET": 2}
    assert analytics.items[0].series[0].latency.p95_ms is None
    assert analytics.items[0].series[0].client_bandwidth.bytes_received == 20
    assert reports.get_database_analytics("main").items[0].series[0].operations == {"insert": 1}
    assert reports.get_project_metrics().startswith("terrabase_requests_total")
    analytics_request = next(r for r in fake.requests if r.url.path.endswith("/analytics")
                             and "/databases/" not in r.url.path)
    assert dict(analytics_request.url.params) == {"range": "24h", "region": "gru"}


async def test_async_regions_and_database_usage(ats: AsyncTerraScale, fake: Fake) -> None:
    assert (await ats.list_regions()).regions == ["gru", "sjc"]
    reports = AsyncReporting(ats)
    usage = await reports.get_database_usage("main", UsageQuery(granularity="day"))
    assert usage.reads == 3
    assert usage.period.granularity == "hour"
    assert (await reports.get_database_analytics("main")).range == "24h"
