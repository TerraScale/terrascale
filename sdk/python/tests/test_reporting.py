"""HTTP fixtures derived from the advertised usage/workload response schemas."""

import inspect

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import AsyncTerraScale, NotFoundError, TerraScale
from terrascale.reporting import AsyncReporting, DatabaseUsage, ProjectUsage, Reporting, UsageQuery

DATABASE = "01ARZ3NDEKTSV4RRFFQ69G5FA1"
START, END = "2026-10-07T00:00:00+00:00", "2026-10-08T00:00:00Z"
LARGE = 2**60 + 1
PERIOD = {"from": START, "to": END, "granularity": "day", "reads": "3", "writes": 2}
POINT = {"start": START, "reads": "3", "writes": 2}
DB = {"databaseId": DATABASE, "storageBytes": str(LARGE), "logicalBytes": 13, "collections": "1", "documents": "2",
      "reads": "9", "writes": 7, "period": PERIOD, "series": [POINT]}
PROJECT_USAGE = {"projectId": PROJECT, "databases": "1", "storageBytes": str(LARGE), "logicalBytes": 13,
                 "documents": "2", "reads": "9", "writes": 7, "period": PERIOD, "series": [POINT],
                 "items": [DB | {"series": []}], "fileBytes": "0", "future": "retained",
                 "month": {"month": "2026-10", "readUnits": str(LARGE), "writeUnits": "2",
                           "units": str(LARGE + 2), "organizationUnits": str(LARGE + 10)}}
SHAPE = {"id": "a" * 64, "collection": "orders", "shape": '{"filter":{"eq":["/status","?"]}}',
         "queries": str(LARGE), "totalLatencyMs": "10.5", "maxLatencyMs": 4.0, "rowsScanned": "17", "lastSeenAt": END}


@pytest.mark.parametrize("asynchronous", [False, True])
async def test_reporting_contract(asynchronous: bool) -> None:
    expected = [
        (f"/v1/projects/{PROJECT}/usage", {"granularity": "day", "from": START, "to": END}, PROJECT_USAGE),
        (f"/v1/projects/{PROJECT}/databases/main/usage", {}, DB),
        (f"/v1/projects/{PROJECT}/databases/main/usage", {"granularity": "hour", "from": START}, DB),
        (f"/v1/projects/{PROJECT}/databases/main/query-shapes", {},
         {"items": [SHAPE | {"latencyBuckets": ["1", 2], "p95LatencyMs": "4.0"}, SHAPE,
                    SHAPE | {"latencyBuckets": None, "p95LatencyMs": None}]}),
        (f"/v1/projects/{PROJECT}/databases/main/query-shapes", {}, {"items": []}),
        (f"/v1/projects/{PROJECT}/usage", {}, {k: v for k, v in PROJECT_USAGE.items() if k != "fileBytes"}),
    ]

    def respond(r: httpx.Request) -> httpx.Response:
        path, params, result = expected.pop(0)
        assert r.method == "GET" and r.url.path == path and _route_ok(r.method, r.url.path)
        assert dict(r.url.params) == params and not r.content
        assert r.headers["authorization"] == "Bearer fixture-only"
        return httpx.Response(200, json=result)

    transport = httpx.MockTransport(respond)
    http = httpx.AsyncClient(transport=transport) if asynchronous else httpx.Client(transport=transport)
    client = (AsyncTerraScale if asynchronous else TerraScale)(token="fixture-only", project=PROJECT, http_client=http)
    reports = (AsyncReporting if asynchronous else Reporting)(client)

    async def call(method: str, *args: object) -> object:
        result = getattr(reports, method)(*args)
        return await result if inspect.isawaitable(result) else result

    try:
        project = await call("get_project_usage", UsageQuery("day", START, END))
        assert isinstance(project, ProjectUsage) and project.project_id == PROJECT and project.databases == 1
        assert project.storage_bytes == LARGE and project.logical_bytes == 13 and project.documents == 2
        assert project.reads == 9 and project.writes == 7 and project.file_bytes == 0
        assert project.period.from_time == START and project.period.to_time == END and project.period.granularity == "day"
        assert project.period.reads == 3 and project.period.writes == 2 and project.series[0].start == START
        assert project.series[0].reads == 3 and project.series[0].writes == 2 and project.items[0].series == []
        assert project.month.month == "2026-10" and project.month.read_units == LARGE and project.month.write_units == 2
        assert project.month.units == LARGE + 2 and project.month.organization_units == LARGE + 10
        assert project.raw["future"] == "retained"
        database = await call("get_database_usage", "main")
        assert isinstance(database, DatabaseUsage) and database.database_id == DATABASE
        assert database.storage_bytes == LARGE and database.logical_bytes == 13 and database.collections == 1
        assert database.documents == 2 and database.reads == 9 and database.writes == 7
        assert database.series == project.series and database.period == project.period
        assert await call("get_database_usage", "main", UsageQuery(from_time=START)) == database
        shapes = await call("list_query_shapes", "main")
        assert shapes[0].id == "a" * 64 and shapes[0].collection == "orders" and shapes[0].shape == SHAPE["shape"]
        assert shapes[0].queries == LARGE and shapes[0].total_latency_ms == 10.5 and shapes[0].max_latency_ms == 4.0
        assert shapes[0].rows_scanned == 17 and shapes[0].last_seen_at == END
        assert shapes[0].latency_buckets == [1, 2] and shapes[0].p95_latency_ms == 4.0
        assert shapes[1].latency_buckets is None and shapes[1].p95_latency_ms is None and shapes[1] == shapes[2]
        assert await call("list_query_shapes", "main") == []
        assert (await call("get_project_usage")).file_bytes is None
        assert expected == []
    finally:
        if asynchronous:
            await http.aclose()
        else:
            http.close()


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("method,args,body", [("get_project_usage", (), PROJECT_USAGE),
                                            ("get_database_usage", ("main",), DB),
                                            ("list_query_shapes", ("main",), {"items": []})])
async def test_reporting_reads_recover_from_lost_response(asynchronous: bool, method: str, args: tuple,
                                                        body: dict, monkeypatch: pytest.MonkeyPatch) -> None:
    requests = []
    monkeypatch.setattr("terrascale._spec.backoff", lambda *_: 0)

    def respond(r: httpx.Request) -> httpx.Response:
        requests.append(r)
        if len(requests) == 1:
            raise httpx.ReadError("fixture: response lost", request=r)
        return httpx.Response(200, json=body)

    transport = httpx.MockTransport(respond)
    if asynchronous:
        async with httpx.AsyncClient(transport=transport) as http:
            client = AsyncTerraScale(token="fixture-only", project=PROJECT, http_client=http)
            await getattr(AsyncReporting(client), method)(*args)
    else:
        with httpx.Client(transport=transport) as http:
            client = TerraScale(token="fixture-only", project=PROJECT, http_client=http)
            getattr(Reporting(client), method)(*args)
    assert len(requests) == 2 and requests[0].url == requests[1].url


def test_reporting_signatures_match() -> None:
    methods = {n for n in dir(Reporting) if not n.startswith("_")}
    assert methods == {n for n in dir(AsyncReporting) if not n.startswith("_")}
    for method in methods:
        assert inspect.signature(getattr(Reporting, method)) == inspect.signature(getattr(AsyncReporting, method))


@pytest.mark.parametrize("asynchronous", [False, True])
async def test_reporting_errors_and_validation(asynchronous: bool) -> None:
    requests = []

    def deny(r: httpx.Request) -> httpx.Response:
        requests.append(r)
        return httpx.Response(404, json={"error": {"code": "not_found", "message": "not found"}})

    transport = httpx.MockTransport(deny)
    http = httpx.AsyncClient(transport=transport) if asynchronous else httpx.Client(transport=transport)
    client = (AsyncTerraScale if asynchronous else TerraScale)(token="fixture-only", project=PROJECT, http_client=http)
    reports = (AsyncReporting if asynchronous else Reporting)(client)

    async def call(method: str, *args: object) -> object:
        result = getattr(reports, method)(*args)
        return await result if inspect.isawaitable(result) else result

    try:
        for method, args in [("get_database_usage", ("../other",)), ("list_query_shapes", ("",)),
                             ("get_project_usage", (UsageQuery("invalid"),))]:
            with pytest.raises(ValueError):
                await call(method, *args)
        assert requests == []
        for method, args in [("get_project_usage", ()), ("get_database_usage", ("main",)), ("list_query_shapes", ("main",))]:
            with pytest.raises(NotFoundError) as error:
                await call(method, *args)
            assert error.value.status == 404 and error.value.code == "not_found"
        assert len(requests) == 3
    finally:
        if asynchronous:
            await http.aclose()
        else:
            http.close()
