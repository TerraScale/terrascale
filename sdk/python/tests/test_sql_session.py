"""SQL WebSocket sessions against a local websockets server replaying the recorded
exchanges in sdk/dotnet/contracts/terrabase/sql/websocket-session.json."""

from __future__ import annotations

import json
import threading
from collections.abc import Callable, Iterator
from pathlib import Path
from typing import Any

import pytest
from fake import PROJECT
from websockets.http11 import Request, Response
from websockets.sync.server import ServerConnection, serve

from terrascale import AsyncTerraScale, AuthenticationError, SqlError, SqlErrorCodes, TerraScale, TerraScaleError

EXCHANGES = json.loads((Path(__file__).parents[2] / "dotnet/contracts/terrabase/sql/websocket-session.json").read_text())[
    "exchanges"]
ROUTE = f"/v1/projects/{PROJECT}/databases/app/sql/ws"


class Server:
    """A local SQL socket: records upgrades and client frames; ``refuse`` answers an upgrade with 401."""

    def __init__(self, handler: Callable[[ServerConnection, Server], None]):
        self.upgrades: list[Request] = []
        self.frames: list[dict[str, Any]] = []
        self.refuse = False
        self._handler = handler

    def process_request(self, connection: ServerConnection, request: Request) -> Response | None:
        self.upgrades.append(request)
        if self.refuse:
            return connection.respond(401, json.dumps({"error": {"code": "invalid_key", "message": "unknown key"}}))
        return None

    def recv(self, ws: ServerConnection) -> int:
        frame = json.loads(ws.recv())
        frame_id: int = frame.pop("id")
        self.frames.append(frame)
        return frame_id

    def handle(self, ws: ServerConnection) -> None:
        self._handler(ws, self)


@pytest.fixture
def serve_sql() -> Iterator[Callable[[Callable[[ServerConnection, Server], None]], tuple[Server, str]]]:
    servers = []

    def start(handler: Callable[[ServerConnection, Server], None]) -> tuple[Server, str]:
        state = Server(handler)
        server = serve(state.handle, "127.0.0.1", 0, subprotocols=["terrascale.sql.v1"],
                       process_request=state.process_request)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        servers.append(server)
        return state, f"http://127.0.0.1:{server.socket.getsockname()[1]}"

    yield start
    for server in servers:
        server.shutdown()


def replay(ws: ServerConnection, server: Server) -> None:
    for exchange in EXCHANGES:
        ws.send(json.dumps({"id": server.recv(ws), **exchange["server"]}))


def test_session_replays_transactions_errors_rollback_and_blobs(serve_sql: Any) -> None:
    server, base = serve_sql(replay)
    with TerraScale(project=PROJECT, token="fixture-token", region="gru", base_url=base) as ts, \
            ts.database("app").connect() as session:
        def work(s: Any) -> Any:
            assert s.in_transaction
            assert s.execute("INSERT INTO ledger VALUES (?, ?)", [2, 25]).last_insert_rowid == 2
            return s.query("SELECT amount FROM ledger WHERE id = ?", [2])

        assert [row.as_dict() for row in session.transaction(work)] == [{"amount": 25}]
        assert not session.in_transaction

        session.begin()
        with pytest.raises(SqlError) as failed:
            session.execute("INSERT INTO ledger VALUES (1, 0)")
        assert (failed.value.code, failed.value.sqlstate, failed.value.statement, failed.value.status) == (
            SqlErrorCodes.PRIMARY_KEY_VIOLATION, "23505", 0, 0)
        assert session.in_transaction
        with pytest.raises(SqlError, match=SqlErrorCodes.TRANSACTION_ABORTED):
            session.query("SELECT count(*) FROM ledger")
        with pytest.raises(SqlError) as rolled_back:
            session.commit()
        assert (rolled_back.value.code, rolled_back.value.sqlstate) == (SqlErrorCodes.TRANSACTION_ROLLED_BACK, "40000")
        assert not session.in_transaction

        session.begin()
        assert session.execute("DELETE FROM ledger").rows_affected == 2
        session.rollback()
        blob, count = session.batch([("SELECT ? AS data, ? AS name", [b"\x01\x02\x03", "Ada"]),
                                     "SELECT count(*) AS n FROM ledger"])
        assert (blob.rows[0].get_bytes("data"), blob.rows[0]["name"], count.rows[0].get_int64("n")) == (
            b"\x01\x02\x03", "Ada", 2)

    assert server.frames == [exchange["client"] for exchange in EXCHANGES]
    (upgrade,) = server.upgrades
    assert (upgrade.path, upgrade.headers["Authorization"], upgrade.headers["X-TerraScale-Region-Pin"]) == (
        ROUTE, "Bearer fixture-token", "gru")


async def test_dropped_transaction_must_roll_back_and_refused_reconnect_raises_auth_error(serve_sql: Any) -> None:
    def drop_in_transaction(ws: ServerConnection, server: Server) -> None:
        ws.send(json.dumps({"id": server.recv(ws), "type": "result", "results": [], "transaction": True}))
        server.recv(ws)
        server.refuse = True
        ws.close(1001, "restarting")

    server, base = serve_sql(drop_in_transaction)
    async with AsyncTerraScale(project=PROJECT, token="fixture-token", base_url=base) as ts, \
            ts.database("app").connect() as session:
        await session.begin()
        with pytest.raises(TerraScaleError, match="closed") as dropped:
            await session.execute("SELECT 1")
        assert not isinstance(dropped.value, SqlError)
        for call in (session.execute("SELECT 1"), session.begin(), session.commit()):
            with pytest.raises(SqlError) as lost:
                await call
            assert (lost.value.code, lost.value.sqlstate) == (SqlErrorCodes.TRANSACTION_ROLLED_BACK, "40000")
        await session.rollback()
        assert len(server.upgrades) == 1
        with pytest.raises(AuthenticationError) as refused:
            await session.execute("SELECT 1")
        assert (refused.value.status, refused.value.code) == (401, "invalid_key")

    assert [frame["type"] for frame in server.frames] == ["begin", "execute"]
    assert [u.headers["Authorization"] for u in server.upgrades] == ["Bearer fixture-token"] * 2
