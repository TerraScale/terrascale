"""HTTP fixtures verify the SDK contract, not server-side rename invariants."""

import inspect
import json

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, CommitUncertainError, TerraScale

DATABASE_ID = "01ARZ3NDEKTSV4RRFFQ69G5FAW"
RENAMED = {"id": DATABASE_ID, "projectId": PROJECT, "name": "orders_new", "region": "gru", "homeRegion": "gru",
           "branchedFrom": None,
           "replicaRegions": ["fra"], "allRegions": False, "createdAt": "2026-10-26T00:00:00Z"}


@pytest.mark.parametrize("selector", [DATABASE_ID, "orders"])
@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("status", [200, 400, 401, 403, 404, 409, 421, 503, 0])
async def test_rename_contract_and_errors(asynchronous: bool, status: int, selector: str) -> None:
    requests = []
    code = {400: "invalid_request", 404: "unknown_database", 409: "name_taken", 421: "wrong_region"}.get(status, "storage_unavailable")

    def respond(request: httpx.Request) -> httpx.Response:
        requests.append(request)
        if request.method == "GET":
            assert status == 200
            assert request.url.path.startswith(f"/v1/projects/{PROJECT}/databases/{DATABASE_ID}")
            if request.url.path.endswith("/documents/by-key"):
                assert request.url.params["id"] == DATABASE_ID
                return httpx.Response(200, json={"id": DATABASE_ID, "version": "tbv1-0", "document": {"kept": True}})
            assert request.url.path == f"/v1/projects/{PROJECT}/databases/{DATABASE_ID}"
            return httpx.Response(200, json=RENAMED)
        assert request.method == "PATCH"
        assert request.url.path == f"/v1/projects/{PROJECT}/databases/{selector}"
        assert _route_ok(request.method, request.url.path)
        assert json.loads(request.content) == {"name": "orders_new"}
        assert request.headers["authorization"] == "Bearer fixture-only"
        assert request.headers["content-type"] == "application/json"
        assert "idempotency-key" not in request.headers
        if status == 0:
            raise httpx.ReadError("fixture response lost", request=request)
        return httpx.Response(status, json=RENAMED if status == 200 else
                              {"error": {"code": code, "message": "fixture refusal"}})

    transport = httpx.MockTransport(respond)
    http = httpx.AsyncClient(transport=transport) if asynchronous else httpx.Client(transport=transport)
    client = (AsyncTerraScale if asynchronous else TerraScale)(token="fixture-only", project=PROJECT, http_client=http)

    async def rename(database_id: str, name: str):
        result = client.rename_database(database_id, name)
        return await result if inspect.isawaitable(result) else result

    try:
        if status == 200:
            result = await rename(selector, "orders_new")
            assert result.id == DATABASE_ID and result.project_id == PROJECT and result.name == "orders_new"
            assert result.raw["homeRegion"] == "gru" and result.raw["replicaRegions"] == ["fra"]
        elif status == 0:
            with pytest.raises(CommitUncertainError):
                await rename(selector, "orders_new")
        else:
            with pytest.raises(APIError) as error:
                await rename(selector, "orders_new")
            assert error.value.status == status
            assert error.value.code == code
        assert len(requests) == 1
        for database_id, name in [("../escape", "orders_new"), (DATABASE_ID, ""), (DATABASE_ID, "../escape")]:
            with pytest.raises(ValueError):
                await rename(database_id, name)
        if status == 200:
            metadata = client.database(DATABASE_ID).info()
            metadata = await metadata if inspect.isawaitable(metadata) else metadata
            assert metadata.name == "orders_new" and metadata.id == DATABASE_ID
            document = client.database(DATABASE_ID).collection("orders").get(DATABASE_ID)
            document = await document if inspect.isawaitable(document) else document
            assert document.data == {"kept": True}
        for selector in ["", "../escape", "Bad Name", "8" + DATABASE_ID[1:], DATABASE_ID.lower()]:
            with pytest.raises(ValueError):
                client.database(selector)
        for name in [DATABASE_ID, "Bad Name", "../escape"]:
            with pytest.raises(ValueError):
                await rename(DATABASE_ID, name)
            with pytest.raises(ValueError):
                created = client.create_database(name)
                if inspect.isawaitable(created):
                    await created
            with pytest.raises(ValueError):
                branched = client.database(DATABASE_ID).branch(name)
                if inspect.isawaitable(branched):
                    await branched
        assert len(requests) == (3 if status == 200 else 1)
    finally:
        if asynchronous:
            await http.aclose()
        else:
            http.close()


def test_rename_surface_signatures_match() -> None:
    assert inspect.signature(TerraScale.rename_database) == inspect.signature(AsyncTerraScale.rename_database)


@pytest.mark.parametrize("fields", [{}, {"previousName": None, "conflictOf": None},
                                    {"previousName": "orders", "conflictOf": PROJECT}])
def test_conflict_metadata_on_database_and_page(fields: dict) -> None:
    from terrascale.models import Database, DatabasePage

    body = {**RENAMED, **fields}
    for database in [Database.from_json(body), DatabasePage.from_json({"items": [body], "next": None}).items[0]]:
        assert database.previous_name == fields.get("previousName")
        assert database.conflict_of == fields.get("conflictOf")


def test_rejects_database_name_in_conflict_id() -> None:
    from terrascale.models import Database

    with pytest.raises(ValueError):
        Database.from_json({**RENAMED, "conflictOf": "orders"})
