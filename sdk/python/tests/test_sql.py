"""SQL REST through both clients against recorded responses and the shared
error-code contract (sdk/dotnet/contracts/terrabase/sql/error-codes.json)."""

import inspect
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import httpx
import pytest
from fake import PROJECT

from terrascale import AsyncTerraScale, SqlError, SqlErrorCodes, Statement, TerraScale

CODES = json.loads((Path(__file__).parents[2] / "dotnet/contracts/terrabase/sql/error-codes.json").read_text())["codes"]
ROUTE = f"/v1/projects/{PROJECT}/databases/app/sql/execute"


@dataclass
class Person:
    id: int
    name: str
    score: float | None
    avatar: bytes | None


async def resolve(value: Any) -> Any:
    return await value if inspect.isawaitable(value) else value


@pytest.fixture(params=[False, True], ids=["sync", "async"])
async def sdk(request: Any, monkeypatch: pytest.MonkeyPatch) -> Any:
    monkeypatch.setattr("terrascale._spec.backoff", lambda *_: 0)
    calls: list[httpx.Request] = []
    responses: list[httpx.Response] = []

    def handler(req: httpx.Request) -> httpx.Response:
        calls.append(req)
        return responses.pop(0) if len(responses) > 1 else responses[0]

    http_cls, cls = (httpx.AsyncClient, AsyncTerraScale) if request.param else (httpx.Client, TerraScale)
    http = http_cls(transport=httpx.MockTransport(handler))
    client = cls(project=PROJECT, token="fixture-token", region="gru", http_client=http)
    yield client.database("app"), calls, responses
    await resolve(http.aclose() if request.param else http.close())


async def test_query_execute_and_batch(sdk: Any) -> None:
    db, calls, responses = sdk
    rows = {"results": [{"columns": [{"name": "id", "decltype": "INTEGER"}, {"name": "name", "decltype": "TEXT"},
                                     {"name": "score"}, {"name": "avatar", "decltype": "BLOB"}],
                         "rows": [[9007199254740993, "Ada", 1.5, {"base64": "AQID"}], [2, "Bob", 0, None]],
                         "rows_affected": 0, "last_insert_rowid": None}]}
    responses.extend([httpx.Response(200, json=rows), httpx.Response(200, json=rows), httpx.Response(200, json={"results": [
        {"columns": [], "rows": [], "rows_affected": 0, "last_insert_rowid": None},
        {"columns": [], "rows": [], "rows_affected": 1, "last_insert_rowid": 7}]})])

    found = await resolve(db.query("SELECT * FROM people WHERE id > ? AND active = ? AND note IS ? AND avatar <> ?",
                                   [1, True, None, b"\x01\x02\x03"]))
    people = await resolve(db.query("SELECT * FROM people WHERE name = :name AND score > @min",
                                    {"name": "Ada", "min": 0.25}, Person))
    results = await resolve(db.batch(["CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT)",
                                      Statement("INSERT INTO people (name) VALUES ($name)", {"name": "Ada"})]))

    assert [r.url.path for r in calls] == [ROUTE] * 3
    assert calls[0].headers["authorization"] == "Bearer fixture-token" and calls[0].headers["x-terrascale-region-pin"] == "gru"
    assert json.loads(calls[0].content)["statements"][0]["args"] == [1, True, None, {"base64": "AQID"}]
    assert json.loads(calls[1].content)["statements"] == [
        {"sql": "SELECT * FROM people WHERE name = :name AND score > @min", "named": {"name": "Ada", "min": 0.25}}]
    first = found[0]
    assert first.get_int64("id") == 9007199254740993 and first["name"] == "Ada" and first.get_double(2) == 1.5
    assert first.get_bytes("avatar") == b"\x01\x02\x03" and found[1].is_null("avatar") and not found[1].get_boolean("score")
    assert dict(first) == {"id": 9007199254740993, "name": "Ada", "score": 1.5, "avatar": b"\x01\x02\x03"}
    assert people == [Person(9007199254740993, "Ada", 1.5, b"\x01\x02\x03"), Person(2, "Bob", 0, None)]
    assert [(r.rows_affected, r.last_insert_rowid) for r in results] == [(0, None), (1, 7)]
    with pytest.raises(TypeError):
        first.get_string("id")


async def test_every_error_code_is_a_sql_error_and_bad_statements_never_send(sdk: Any) -> None:
    db, calls, responses = sdk
    assert sorted(c["code"] for c in CODES) == sorted(v for k, v in vars(SqlErrorCodes).items() if k.isupper())
    for entry in CODES:
        calls.clear()
        responses[:] = [httpx.Response(entry["status"], json={"error": {
            "code": entry["code"], "message": "refused", "statement": 1, "sqlstate": entry["sqlstate"]}})]
        with pytest.raises(SqlError) as raised:
            await resolve(db.batch(["SELECT 1", "INSERT INTO people VALUES (1)"]))
        err = raised.value
        assert (err.status, err.code, err.sqlstate, err.statement, err.message) == (
            entry["status"], entry["code"], entry["sqlstate"], 1, "refused")
        assert err.is_transient == (entry["code"] in ("serialization_conflict", "tenant_busy"))
        assert len(calls) == (3 if entry["code"] == "serialization_conflict" else 1), entry["code"]

    calls.clear()
    for bad in ([], ["SELECT 1"] * 257, [" "], [("SELECT :a", {":a": 1})], [("SELECT ?", [float("nan")])]):
        with pytest.raises(ValueError):
            await resolve(db.batch(bad))
    assert calls == []
