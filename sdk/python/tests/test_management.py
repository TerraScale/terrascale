from __future__ import annotations

from fake import PROJECT, Fake

from terrascale import AsyncTerraScale, TerraScale, models, new_id


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


def test_project_user_admin(ts: TerraScale, fake: Fake) -> None:
    page = ts.list_users(search="example.com", limit=50)
    assert [u.email for u in page.items] == ["ada@example.com"]
    assert page.items[0].disabled is False
    assert page.next_cursor is None
    request = fake.requests[-1]
    assert request.method == "GET"
    assert request.url.path == f"/v1/{PROJECT}/users"
    assert dict(request.url.params) == {"q": "example.com", "limit": "50"}
    assert request.headers["authorization"].startswith("Bearer tok-")
    user_id = page.items[0].id
    disabled = ts.disable_user(user_id)
    assert disabled.disabled is True
    request = fake.requests[-1]
    assert (request.method, request.url.path) == ("POST", f"/v1/{PROJECT}/users/{user_id}/disable")


def test_project_sessions_and_providers(ts: TerraScale, fake: Fake) -> None:
    page = ts.list_sessions(user_id="01J" + "V" * 23, limit=10)
    assert [s.email for s in page.items] == ["ada@example.com"]
    assert page.items[0].expires_at == "2026-11-18T00:00:00Z"
    assert page.next_cursor is None
    request = fake.requests[-1]
    assert (request.method, request.url.path) == ("GET", f"/v1/{PROJECT}/sessions")
    assert dict(request.url.params) == {"user": "01J" + "V" * 23, "limit": "10"}
    assert request.headers["authorization"].startswith("Bearer tok-")
    ts.revoke_session(page.items[0].id)
    request = fake.requests[-1]
    assert (request.method, request.url.path) == ("DELETE", f"/v1/{PROJECT}/sessions/{page.items[0].id}")
    assert fake.sessions == {}
    settings = ts.set_provider("google", models.ProjectProviderUpdate(client_id="google-client"))
    assert settings.providers[0].provider == "google"
    request = fake.requests[-1]
    assert (request.method, request.url.path) == ("PUT", f"/v1/{PROJECT}/providers/google")
