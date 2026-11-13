import asyncio
import json
import os

import httpx
from terrascale import AsyncTerraScale, TerraScale
from terrascale.errors import CommitUncertainError


def code(error):
    if isinstance(error, CommitUncertainError):
        return "commit_uncertain"
    return getattr(error, "code", type(error).__name__)


def failure(work):
    try:
        work()
        return "accepted"
    except Exception as error:
        return code(error)


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
            with db.connect() as session:
                if mode not in {"autocommit", "batch"}:
                    session.begin()
                    session.execute(f"INSERT INTO {table} VALUES (1)")
                if mode == "before":
                    httpx.post(origin + "/cut").raise_for_status()
                    failure(lambda: session.query("SELECT 1"))
                first = failure(
                    lambda: (
                        session.execute(f"INSERT INTO {table} VALUES (1)")
                        if mode == "autocommit"
                        else session.batch([f"INSERT INTO {table} VALUES (1)", f"INSERT INTO {table} VALUES (2)"])
                        if mode == "batch"
                        else session.query("SELECT 1")
                        if mode == "read"
                        else session.commit()
                    )
                )
                next_error = failure(session.begin)
                rows = db.query(f"SELECT count(*) AS n FROM {table}")
                print(json.dumps(dict(first=first, next=next_error, rows=str(rows[0][0]))))
        finally:
            db.execute(f"DROP TABLE {table}")


async def async_session_fault(settings, origin, table, mode):
    async def failed(work):
        try:
            await work()
            return "accepted"
        except Exception as error:
            return code(error)

    async with AsyncTerraScale(**settings) as client:
        db = client.database(os.environ["TERRASCALE_DATABASE_NAME"])
        await db.execute(f"CREATE TABLE {table} (n INTEGER)")
        try:
            async with db.connect() as session:
                if mode not in {"autocommit", "batch"}:
                    await session.begin()
                    await session.execute(f"INSERT INTO {table} VALUES (1)")
                if mode == "before":
                    async with httpx.AsyncClient() as http:
                        (await http.post(origin + "/cut")).raise_for_status()
                    await failed(lambda: session.query("SELECT 1"))
                first = await failed(
                    lambda: (
                        session.execute(f"INSERT INTO {table} VALUES (1)")
                        if mode == "autocommit"
                        else session.batch([f"INSERT INTO {table} VALUES (1)", f"INSERT INTO {table} VALUES (2)"])
                        if mode == "batch"
                        else session.query("SELECT 1")
                        if mode == "read"
                        else session.commit()
                    )
                )
                next_error = await failed(session.begin)
                rows = await db.query(f"SELECT count(*) AS n FROM {table}")
                print(json.dumps(dict(first=first, next=next_error, rows=str(rows[0][0]))))
        finally:
            await db.execute(f"DROP TABLE {table}")
