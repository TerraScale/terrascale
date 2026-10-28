"""Real SDK cleanup outcomes after bounded setup; no live requests or tenant."""

import asyncio
import io
import json

import httpx
from terrascale import AsyncTerraScale, new_id

from observer import PIN_HEADER
from parity import Tenant, _cleanup, _setup


async def case(mode):
    out = io.StringIO()
    tenant = Tenant(new_id() + "_" + "a" * 52, new_id(), "api-perf-" + new_id().lower(), out)
    requests, stopped = [], asyncio.Event()

    async def handle(request):
        if request.url.host == "auth.terrascale.tech":
            assert PIN_HEADER not in request.headers
            return httpx.Response(200, json={"access_token": "synthetic.1", "expires_in": 3600, "token_type": "Bearer"})
        requests.append((request.method, request.url.path))
        if request.method == "POST":
            try:
                await asyncio.Event().wait()
            finally:
                stopped.set()
        assert request.method == "DELETE" and request.url.path == tenant.db_path
        status = 403 if mode == "refused" else 404 if mode in ("absent", "uncertain") else 204
        return httpx.Response(status, json={"error": {"code": "not_found" if status == 404 else "forbidden"}}) if status != 204 else httpx.Response(204)

    async with httpx.AsyncClient(transport=httpx.MockTransport(handle)) as pool:
        client = AsyncTerraScale(tenant.key, project=tenant.project, http_client=pool)
        if mode == "uncertain":
            try:
                await asyncio.wait_for(_setup(client, "fra", tenant.db, tenant), .1)
            except TimeoutError:
                pass
            else:
                raise AssertionError("setup was not bounded")
            assert stopped.is_set() and tenant.create == "uncertain"
        else:
            tenant.create = "not_attempted" if mode == "absent" else "created"
        accepted = await _cleanup(client, tenant.db, tenant)
    row = json.loads(out.getvalue())
    assert row["database"] == tenant.db and row["remote_erasure"] == "unverified"
    assert row["deletion"] == ("failed" if mode == "refused" else "absent" if mode in ("absent", "uncertain") else "logical_accepted")
    assert row["reconcile_required"] == (mode in ("uncertain", "refused"))
    assert accepted == (mode in ("absent", "accepted"))
    assert requests[-1] == ("DELETE", tenant.db_path)
    print(json.dumps({"case": mode, "create": row["create"], "deletion": row["deletion"],
                      "reconcile_required": row["reconcile_required"], "accepted": accepted}))


async def main():
    await asyncio.gather(*(case(mode) for mode in ("accepted", "absent", "uncertain", "refused")))


if __name__ == "__main__":
    asyncio.run(main())
