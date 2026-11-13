import asyncio
import json
import os
import signal
import threading

import httpx
from terrascale import AsyncTerraScale, TerraScale, dbapi
from terrascale.errors import CommitUncertainError, SqlError, TerraScaleError


def code(error, uncertain=False):
    if uncertain:
        assert isinstance(error, CommitUncertainError), (type(error), error)
        assert error.code in {"commit_uncertain", "write_outcome_unknown"}, error.code
    if isinstance(error, CommitUncertainError):
        return error.code
    if isinstance(error, SqlError):
        return error.code
    assert isinstance(error, TerraScaleError), (type(error), error)
    return "transport"


def failure(work, uncertain=False):
    try:
        work()
    except BaseException as error:
        return code(error, uncertain)
    assert not uncertain, "operation accepted after uncertainty"
    return "accepted"


def reenter(session):
    with session:
        pass


async def async_reenter(session):
    async with session:
        pass


def session_fault():
    origin, table, mode = (os.environ[key] for key in ("E2E_SESSION_PROXY", "E2E_SESSION_TABLE", "E2E_SESSION_FAULT"))
    settings = dict(
        api_key=os.environ["TERRASCALE_API_KEY"],
        project=os.environ["TERRASCALE_PROJECT_ID"],
        base_url=origin,
        issuer="https://auth.terrascale.tech/terrascale",
    )
    if os.environ.get("E2E_SESSION_ASYNC"):
        asyncio.run(async_session_fault(settings, origin, table, mode))
        return
    with TerraScale(**settings) as client:
        db = client.database(os.environ["TERRASCALE_DATABASE_NAME"])
        db.execute(f"CREATE TABLE {table} (n INTEGER)")
        try:
            if mode == "dbapi":
                connection = dbapi.connect(database=os.environ["TERRASCALE_DATABASE_NAME"], **settings)
                try:
                    cursor = connection.cursor()
                    cursor.execute(f"INSERT INTO {table} VALUES (1)")
                    first = failure(connection.commit, True)
                    next_error = failure(lambda: cursor.execute("SELECT 1"), True)
                    second = failure(connection.rollback, True)
                    failure(connection.commit, True)
                    failure(connection.cursor, True)
                    failure(lambda: setattr(connection, "isolation_level", "AUTOCOMMIT"), True)
                    connection.close()
                    failure(connection.cursor, True)
                    rows = db.query(f"SELECT count(*) AS n FROM {table}")
                    print(json.dumps(dict(first=first, next=next_error, second=second, rows=str(rows[0][0]))))
                finally:
                    connection.close()
                return
            with db.connect() as session:
                uncertain = mode not in {"before", "read"}
                if mode not in {"autocommit", "batch"}:
                    session.begin()
                    session.execute(f"INSERT INTO {table} VALUES (1)")
                if mode == "before":
                    httpx.post(origin + "/cut").raise_for_status()
                    failure(lambda: session.query("SELECT 1"))
                if mode == "cancel":
                    def interrupt():
                        httpx.post(origin + "/committed").raise_for_status()
                        os.kill(os.getpid(), signal.SIGINT)
                    interrupter = threading.Thread(target=interrupt)
                    interrupter.start()
                    try:
                        first = failure(session.commit, True)
                    finally:
                        interrupter.join()
                else:
                    first = failure(
                        lambda: (
                            session.execute(f"INSERT INTO {table} VALUES (1)")
                            if mode == "autocommit"
                            else session.batch([f"INSERT INTO {table} VALUES (1)", f"INSERT INTO {table} VALUES (2)"])
                            if mode == "batch"
                            else session.query("SELECT 1")
                            if mode == "read"
                            else session.commit()
                        ), uncertain
                    )
                next_error = failure(session.begin, uncertain)
                second = failure(lambda: session.execute("SELECT 2"), uncertain)
                if uncertain:
                    failure(lambda: reenter(session), True)
                    session.close()
                    failure(lambda: reenter(session), True)
                    failure(session.rollback, True)
                rows = db.query(f"SELECT count(*) AS n FROM {table}")
                print(json.dumps(dict(first=first, next=next_error, second=second, rows=str(rows[0][0]))))
        finally:
            db.execute(f"DROP TABLE {table}")


async def async_session_fault(settings, origin, table, mode):
    async def failed(work, uncertain=False):
        try:
            await work()
        except BaseException as error:
            return code(error, uncertain)
        assert not uncertain, "operation accepted after uncertainty"
        return "accepted"

    async with AsyncTerraScale(**settings) as client:
        db = client.database(os.environ["TERRASCALE_DATABASE_NAME"])
        await db.execute(f"CREATE TABLE {table} (n INTEGER)")
        try:
            async with db.connect() as session:
                uncertain = mode not in {"before", "read"}
                if mode not in {"autocommit", "batch"}:
                    await session.begin()
                    await session.execute(f"INSERT INTO {table} VALUES (1)")
                if mode == "before":
                    async with httpx.AsyncClient() as http:
                        (await http.post(origin + "/cut")).raise_for_status()
                    await failed(lambda: session.query("SELECT 1"))
                if mode == "cancel":
                    caller = asyncio.create_task(session.commit())
                    async with httpx.AsyncClient() as http:
                        (await http.post(origin + "/committed")).raise_for_status()
                    caller.cancel()
                    first = await failed(lambda: caller, True)
                else:
                    first = await failed(
                        lambda: (
                            session.execute(f"INSERT INTO {table} VALUES (1)")
                            if mode == "autocommit"
                            else session.batch([f"INSERT INTO {table} VALUES (1)", f"INSERT INTO {table} VALUES (2)"])
                            if mode == "batch"
                            else session.query("SELECT 1")
                            if mode == "read"
                            else session.commit()
                        ), uncertain
                    )
                next_error = await failed(session.begin, uncertain)
                second = await failed(lambda: session.execute("SELECT 2"), uncertain)
                if uncertain:
                    await failed(lambda: session, True)
                    await failed(lambda: async_reenter(session), True)
                    await session.close()
                    await failed(lambda: session, True)
                    await failed(lambda: async_reenter(session), True)
                    await failed(session.rollback, True)
                rows = await db.query(f"SELECT count(*) AS n FROM {table}")
                print(json.dumps(dict(first=first, next=next_error, second=second, rows=str(rows[0][0]))))
        finally:
            await db.execute(f"DROP TABLE {table}")
