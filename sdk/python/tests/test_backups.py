"""Contract-shaped HTTP fixtures; no service process or customer credentials."""

import inspect
import json

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import AsyncTerraScale, CommitUncertainError, TerraScale
from terrascale.backups import AsyncDatabaseBackups, DatabaseBackups

BACKUP_ID = "01ARZ3NDEKTSV4RRFFQ69G5FA4"
DATABASE_ID = "01ARZ3NDEKTSV4RRFFQ69G5FA1"
TIME = "2026-10-07T12:00:13Z"
BACKUP = {"id": BACKUP_ID, "databaseId": DATABASE_ID, "status": "ready", "trigger": "manual",
          "sizeBytes": "40960", "createdAt": "2026-10-07T12:00:00Z", "future": "retained"}
WINDOW = {"earliest": "2026-10-07T12:00:00Z", "latest": "2026-10-07T12:00:16Z"}
PLAN = {"window": WINDOW, "pointInTime": TIME, "snapshotAt": "2026-10-07T12:00:10Z",
        "latestSegmentAt": None, "segmentCount": "0", "historyCheck": "metadata"}
RESTORED = {"id": "01ARZ3NDEKTSV4RRFFQ69G5FA6", "projectId": PROJECT, "name": "restored",
            "branchedFrom": DATABASE_ID, "createdAt": TIME, "homeRegion": "gru-1", "replicaRegions": []}


@pytest.mark.parametrize("asynchronous", [False, True])
async def test_backup_contract(asynchronous: bool) -> None:
    expected = [
        ("POST", "/backups", {}, None, BACKUP),
        ("GET", "/backups", {"limit": "1"}, None, {"items": [BACKUP], "next": "opaque+/="}),
        ("GET", "/backups", {"after": "opaque+/="}, None, {"items": [], "next": None}),
        ("POST", f"/backups/{BACKUP_ID}/restore", {}, {"name": "restored"}, RESTORED),
        ("GET", "/point-in-time", {"destination": "customer"}, None, WINDOW),
        ("POST", "/point-in-time/plan", {}, {"pointInTime": TIME, "destination": "platform"}, PLAN),
        ("POST", "/point-in-time/restore", {},
         {"pointInTime": TIME, "destination": "customer", "name": "restored"}, RESTORED),
        ("POST", f"/backups/{BACKUP_ID}/restore", {}, {}, RESTORED),
    ]

    def respond(r: httpx.Request) -> httpx.Response:
        method, suffix, params, body, result = expected.pop(0)
        assert r.method == method
        assert r.url.path == f"/v1/projects/{PROJECT}/databases/main" + suffix
        assert _route_ok(r.method, r.url.path)
        assert dict(r.url.params) == params
        assert (json.loads(r.content) if r.content else None) == body
        assert r.headers["authorization"] == "Bearer fixture-only"
        return httpx.Response(200 if method == "GET" or suffix.endswith("/plan") else 201, json=result)

    transport = httpx.MockTransport(respond)
    http = httpx.AsyncClient(transport=transport) if asynchronous else httpx.Client(transport=transport)
    client = (AsyncTerraScale if asynchronous else TerraScale)(token="fixture-only", project=PROJECT, http_client=http)
    backups = (AsyncDatabaseBackups if asynchronous else DatabaseBackups)(client.database("main"))

    async def call(method: str, *args: object, **kwargs: object) -> object:
        result = getattr(backups, method)(*args, **kwargs)
        return await result if inspect.isawaitable(result) else result

    try:
        backup = await call("create_backup")
        assert backup.id == BACKUP_ID and backup.size_bytes == 40960 and backup.raw["future"] == "retained"
        page = await call("list_backups", limit=1)
        assert page.items == [backup] and page.next_cursor == "opaque+/="
        page = await call("list_backups", cursor=page.next_cursor)
        assert page.items == [] and page.next_cursor is None
        restored = await call("restore_backup", BACKUP_ID, name="restored")
        assert restored.id == RESTORED["id"] and restored.project_id == PROJECT
        assert restored.branched_from == DATABASE_ID and restored.raw["homeRegion"] == "gru-1"
        window = await call("get_restore_window", destination="customer")
        assert window.earliest == WINDOW["earliest"] and window.latest == WINDOW["latest"]
        plan = await call("plan_point_in_time", TIME)
        assert plan.window == window and plan.point_in_time == TIME and plan.snapshot_at == PLAN["snapshotAt"]
        assert plan.segment_count == 0 and plan.latest_segment_at is None and plan.history_check == "metadata"
        assert await call("restore_point_in_time", TIME, name="restored", destination="customer") == restored
        assert await call("restore_backup", BACKUP_ID) == restored
        assert expected == []
    finally:
        if asynchronous:
            await http.aclose()
        else:
            http.close()


def test_backup_surface_signatures_match() -> None:
    methods = {n for n in dir(DatabaseBackups) if not n.startswith("_")}
    assert methods == {n for n in dir(AsyncDatabaseBackups) if not n.startswith("_")}
    for method in methods:
        assert inspect.signature(getattr(DatabaseBackups, method)) == inspect.signature(getattr(AsyncDatabaseBackups, method))


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("method,args", [("create_backup", ()), ("restore_backup", (BACKUP_ID,)),
                                        ("restore_point_in_time", (TIME,))])
async def test_backup_mutations_are_not_replayed(asynchronous: bool, method: str, args: tuple) -> None:
    requests = []

    def fail(r: httpx.Request) -> httpx.Response:
        requests.append(r)
        raise httpx.ReadError("fixture: response lost after commit", request=r)

    transport = httpx.MockTransport(fail)
    if asynchronous:
        async with httpx.AsyncClient(transport=transport) as http:
            client = AsyncTerraScale(token="fixture-only", project=PROJECT, http_client=http)
            with pytest.raises(CommitUncertainError):
                await getattr(AsyncDatabaseBackups(client.database("main")), method)(*args)
    else:
        with httpx.Client(transport=transport) as http:
            client = TerraScale(token="fixture-only", project=PROJECT, http_client=http)
            with pytest.raises(CommitUncertainError):
                getattr(DatabaseBackups(client.database("main")), method)(*args)
    assert len(requests) == 1


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("method,args,kwargs", [
    ("list_backups", (), {"limit": 0}), ("list_backups", (), {"limit": 101}),
    ("list_backups", (), {"limit": True}), ("list_backups", (), {"limit": 1.5}),
    ("restore_backup", ("invalid",), {}), ("restore_backup", (BACKUP_ID,), {"name": "../other"}),
    ("get_restore_window", (), {"destination": "other"}),
    ("plan_point_in_time", (TIME,), {"destination": "other"}),
    ("restore_point_in_time", (TIME,), {"name": ""}),
])
async def test_invalid_backup_arguments_send_no_requests(asynchronous: bool, method: str, args: tuple, kwargs: dict) -> None:
    def unexpected(r: httpx.Request) -> httpx.Response:
        pytest.fail(f"invalid arguments sent an HTTP request: {r.method}")

    transport = httpx.MockTransport(unexpected)
    if asynchronous:
        async with httpx.AsyncClient(transport=transport) as http:
            client = AsyncTerraScale(token="fixture-only", project=PROJECT, http_client=http)
            with pytest.raises(ValueError):
                await getattr(AsyncDatabaseBackups(client.database("main")), method)(*args, **kwargs)
    else:
        with httpx.Client(transport=transport) as http:
            client = TerraScale(token="fixture-only", project=PROJECT, http_client=http)
            with pytest.raises(ValueError):
                getattr(DatabaseBackups(client.database("main")), method)(*args, **kwargs)
