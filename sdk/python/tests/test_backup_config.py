"""Production backup policy contracts, with no backup worker or live requests."""

import inspect
import json
import traceback

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, CommitUncertainError, TerraScale
from terrascale.backup_config import AsyncDatabaseBackupPolicy, BackupPolicyUpdate, DatabaseBackupPolicy

DEFAULT = {"fullBackupCron": "0 2 * * *", "retentionDays": "7", "disabled": False, "updatedAt": None}


async def invoke(asynchronous, handler, policy=None):
    transport = httpx.MockTransport(handler)
    if asynchronous:
        async with httpx.AsyncClient(transport=transport) as http:
            client = AsyncTerraScale(token="fixture-only", project=PROJECT, http_client=http)
            handle = AsyncDatabaseBackupPolicy(client.database("main"))
            return await (handle.get_backup_policy() if policy is None else handle.set_backup_policy(policy))
    with httpx.Client(transport=transport) as http:
        client = TerraScale(token="fixture-only", project=PROJECT, http_client=http)
        handle = DatabaseBackupPolicy(client.database("main"))
        return handle.get_backup_policy() if policy is None else handle.set_backup_policy(policy)


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("policy", [None, BackupPolicyUpdate(None, 1), BackupPolicyUpdate("", 35, True),
                                   BackupPolicyUpdate("0 2 * * *", 7)])
async def test_policy_contract(asynchronous, policy):
    response = DEFAULT if policy is None else policy.body() | {"updatedAt": "2026-10-26T00:00:00Z"}
    def respond(r):
        assert r.method == ("GET" if policy is None else "PUT")
        assert r.url.path == f"/v1/projects/{PROJECT}/databases/main/backup-policy" and _route_ok(r.method, r.url.path)
        assert not r.url.query and r.headers["authorization"] == "Bearer fixture-only"
        assert (json.loads(r.content) if r.content else None) == (None if policy is None else policy.body())
        return httpx.Response(200, json=response)
    result = await invoke(asynchronous, respond, policy)
    assert result.retention_days == int(response["retentionDays"]) and result.full_backup_cron == response["fullBackupCron"]
    assert result.disabled is response["disabled"] and result.updated_at == response["updatedAt"]


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("failure", ["transport", "server", "malformed", "wrong_status"])
async def test_mutations_uncertain_and_redacted(asynchronous, failure):
    requests = []
    marker = "sensitive-fixture-only"
    def respond(r):
        requests.append(r)
        if failure == "transport":
            raise httpx.ReadError(marker, request=r)
        status = 500 if failure == "server" else 201 if failure == "wrong_status" else 200
        return httpx.Response(status, json={"error": {"code": marker, "message": marker}}, headers={"X-Request-ID": PROJECT})
    with pytest.raises(CommitUncertainError) as error:
        await invoke(asynchronous, respond, BackupPolicyUpdate(None, 7))
    assert len(requests) == 1 and marker not in "".join(traceback.format_exception(error.value))
    assert marker not in repr(vars(error.value)) and error.value.__suppress_context__


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("patch", [{"retentionDays": True}, {"retentionDays": 7.1}, {"retentionDays": 2**31},
                                  {"disabled": 0}, {"fullBackupCron": []}, {"updatedAt": 7}])
async def test_invalid_responses(asynchronous, patch):
    with pytest.raises(APIError) as error:
        await invoke(asynchronous, lambda _: httpx.Response(200, json=DEFAULT | patch))
    assert error.value.code == "invalid_response"


@pytest.mark.parametrize("asynchronous", [False, True])
async def test_default_read_retries(asynchronous, monkeypatch):
    monkeypatch.setattr("terrascale._spec.backoff", lambda *_: 0)
    requests = []
    def respond(r):
        requests.append(r)
        if len(requests) == 1:
            raise httpx.ReadError("fixture-only", request=r)
        return httpx.Response(200, json=DEFAULT)
    assert (await invoke(asynchronous, respond)).updated_at is None and len(requests) == 2


def test_invalid_inputs_and_twins():
    for policy in [BackupPolicyUpdate(None, True), BackupPolicyUpdate(None, "7"), BackupPolicyUpdate(None, 0),
                   BackupPolicyUpdate(None, 36), BackupPolicyUpdate(None, 7, "false")]:
        with pytest.raises(ValueError):
            policy.body()
    methods = {x for x in dir(DatabaseBackupPolicy) if not x.startswith("_")}
    assert methods == {x for x in dir(AsyncDatabaseBackupPolicy) if not x.startswith("_")}
    for method in methods:
        assert inspect.signature(getattr(DatabaseBackupPolicy, method)) == inspect.signature(
            getattr(AsyncDatabaseBackupPolicy, method))
