from __future__ import annotations

from fake import Fake

from terrascale import AsyncTerraScale, TerraScale, new_id


def test_api_key_lifecycle(ts: TerraScale, fake: Fake) -> None:
    org = new_id()
    created = ts.create_api_key(org, "ci", ["databases:read"], step_up_proof="proof", allowed_cidrs=["203.0.113.0/24"])
    assert created.key == created.api_key.id + "_" + "c" * 52
    assert created.api_key.organization_id == org
    assert created.api_key.project_id is None
    assert created.api_key.scopes == ["databases:read"]
    assert created.api_key.allowed_cidrs == ["203.0.113.0/24"]
    assert created.key not in repr(created)
    page = ts.list_api_keys(org)
    assert [k.id for k in page.items] == [created.api_key.id]
    assert page.next_cursor is None
    ts.revoke_api_key(org, created.api_key.id)
    assert fake.api_keys == {}
    assert ts.list_api_keys(org).items == []


async def test_project_client_create_and_rotate(ats: AsyncTerraScale, fake: Fake) -> None:
    created = await ats.create_project_client("web app", "confidential", ["https://app.example/cb"],
                                              step_up_proof="proof")
    assert created.type == "confidential"
    assert created.redirect_uris == ["https://app.example/cb"]
    assert created.client_secret == created.client_id + "_" + "d" * 52
    assert created.client_secret not in repr(created)
    rotated = await ats.rotate_project_client_secret(created.client_id, step_up_proof="proof", grace_seconds=600)
    assert rotated.client_id == created.client_id
    assert rotated.client_secret == created.client_id + "_" + "e" * 52
    assert rotated.previous_secret_expires_at is not None
    assert rotated.client_secret not in repr(rotated)
    page = await ats.list_project_clients()
    assert [c.client_id for c in page.items] == [created.client_id]
    assert page.items[0].client_secret is None
