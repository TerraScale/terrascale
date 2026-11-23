"""Asyncio client: the same API as :mod:`terrascale.client`, awaited."""

from __future__ import annotations

import asyncio
import builtins
from collections.abc import AsyncIterator, Awaitable, Callable, Mapping, Sequence
from typing import Any, TypeVar, overload

import httpx

from . import _spec, models
from . import auth_organizations as _organizations
from . import backups as _backups
from . import database_rules as _rules
from . import sql as _sql
from ._spec import Call
from ._version import __version__
from .auth import (
    AsyncAPIKeyTokenSource,
    _scoped_key_call,
    approve_project_waitlist_call,
    create_api_key_call,
    create_project_client_call,
    create_project_invitation_call,
    create_user_api_key_call,
    create_user_call,
    delete_project_client_call,
    delete_provider_call,
    delete_user_call,
    disable_user_call,
    enable_user_call,
    export_users_call,
    get_settings_call,
    get_user_call,
    import_users_call,
    list_api_keys_call,
    list_project_clients_call,
    list_project_invitations_call,
    list_project_waitlist_call,
    list_sessions_call,
    list_user_api_keys_call,
    list_users_call,
    reject_project_waitlist_call,
    revoke_api_key_call,
    revoke_project_invitation_call,
    revoke_session_call,
    revoke_user_api_key_call,
    rotate_api_key_call,
    rotate_project_client_secret_call,
    set_provider_call,
    set_user_api_key_policy_call,
    sign_out_user_call,
    update_project_client_call,
    update_settings_call,
    update_user_call,
    verify_user_api_key_call,
)
from .client import _apply, _error, _settings, _tenant
from .custom_tokens import ClaimValue, CustomToken, mint_custom_token_call
from .session import AsyncSqlSession
from .shapes import AsyncShape
from .sql import Args, Row, Statement, StatementResult

__all__ = ["AsyncTerraScale", "AsyncDatabase"]

T = TypeVar("T")

class AsyncTerraScale:
    """A project-scoped asyncio TerraBase client; see :class:`terrascale.TerraScale`.

    ``api_key`` is exchanged for short-lived access tokens with ts-auth and
    refreshed automatically. ``token`` instead supplies an access token, or a
    callable returning one. Arguments default to the ``TERRASCALE_API_KEY``
    and ``TERRASCALE_PROJECT_ID`` environment variables. ``region`` pins every
    request to one region, such as ``"gru"``.
    """

    def __init__(
        self,
        api_key: str | None = None,
        *,
        project: str | None = None,
        token: str | Callable[[], str | Awaitable[str]] | None = None,
        base_url: str | None = None,
        region: str | None = None,
        issuer: str | None = None,
        scopes: Sequence[str] | None = None,
        timeout: float = 30.0,
        max_attempts: int = _spec.MAX_ATTEMPTS,
        http_client: httpx.AsyncClient | None = None,
    ):
        api_key, self.project, self.base_url = _settings(api_key, project, token, base_url, issuer)
        self._http = http_client or httpx.AsyncClient(timeout=timeout, follow_redirects=False, http2=True,
                                                      mounts={"http://": httpx.AsyncHTTPTransport(http2=True)})
        self._owns_http = http_client is None
        self._max_attempts = max(1, max_attempts)
        self._source: AsyncAPIKeyTokenSource | None = None
        self._token = token
        self._api_key = api_key
        self._issuer = issuer
        if token is None:
            assert api_key is not None
            self._source = AsyncAPIKeyTokenSource(api_key, project=self.project, scopes=scopes, issuer=issuer,
                                                  max_attempts=self._max_attempts, http_client=self._http)
        self._headers = {"User-Agent": _spec.USER_AGENT_PREFIX + __version__, "Accept": "application/json"}
        if region is not None and (not isinstance(region, str) or region.strip()):
            self._headers[_spec.REGION_PIN] = _spec.region(region)
        self._bookmarks = _spec.Bookmarks()

    @property
    def bookmarks(self) -> dict[str, str]:
        return self._bookmarks.snapshot()

    async def close(self) -> None:
        if self._source is not None:
            await self._source.close()
        if self._owns_http:
            await self._http.aclose()

    async def __aenter__(self) -> AsyncTerraScale:
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.close()

    async def _bearer(self) -> str:
        if self._source is not None:
            return await self._source.token()
        if callable(self._token):
            token = self._token()
            return await token if isinstance(token, Awaitable) else token
        return str(self._token)

    async def _send(self, call: Call) -> Any:
        url = call.url(self.base_url)
        content = call.content()
        refreshed = False
        attempt = 0
        conflicts = 0
        while True:
            direct = "Authorization" in call.headers
            project_key = _spec.project_bearer(self._api_key, call)
            token = "" if direct or call.anonymous else project_key or await self._bearer()
            headers = _spec.request_headers(self._headers, call, self._bookmarks, direct=direct, token=token,
                                            has_body=content is not None)
            try:
                if direct:
                    response = await self._http.send(httpx.Request(call.method, url, headers=headers, content=content),
                                                     auth=None, follow_redirects=False)
                else:
                    response = await self._http.request(
                        call.method, url, params=call.params or None, headers=headers, content=content,
                        timeout=call.timeout if call.timeout is not None else httpx.USE_CLIENT_DEFAULT)
            except (httpx.TransportError, httpx.DecodingError) as exc:
                attempt += 1
                await asyncio.sleep(_spec.after_transport(call, exc, attempt, self._max_attempts))
                continue
            if 200 <= response.status_code < 300:
                return call.accept(response, self._bookmarks)
            err = _error(response, mutation=not call.replay_safe)
            action = _apply(self, err, _spec.after_status(
                call, err, attempt=attempt, conflicts=conflicts, max_attempts=self._max_attempts, refreshed=refreshed,
                direct=direct, has_source=self._source is not None, project_key=project_key is not None), token)
            if action.kind == "refresh":
                refreshed = True
                continue
            if action.kind == "sleep":
                attempt, conflicts = action.attempt, action.conflicts
                await asyncio.sleep(action.seconds)
                continue
            raise err

    async def _stream(self, call: Call) -> AsyncIterator[bytes]:
        url = call.url(self.base_url)
        attempt = 0
        refreshed = False
        while True:
            token = await self._bearer()
            headers = _spec.request_headers(self._headers, call, self._bookmarks, direct=False, token=token, has_body=False)
            request = httpx.Request(call.method, url, headers=headers)
            try:
                response = await self._http.send(request, auth=None, stream=True, follow_redirects=False)
            except httpx.TransportError as exc:
                attempt += 1
                if attempt >= self._max_attempts:
                    raise _spec.transport_failure(call.method, attempt, exc) from exc
                await asyncio.sleep(_spec.backoff(attempt, None))
                continue
            if 200 <= response.status_code < 300:
                break
            err = _error(response)
            await response.aclose()
            action = _apply(self, err, _spec.after_status(
                call, err, attempt=attempt, conflicts=2, max_attempts=self._max_attempts, refreshed=refreshed,
                direct=False, has_source=self._source is not None, project_key=False), token)
            if action.kind == "refresh":
                refreshed = True
                continue
            if action.kind == "sleep":
                attempt = action.attempt
                await asyncio.sleep(action.seconds)
                continue
            raise err
        try:
            async for chunk in response.aiter_bytes():
                yield chunk
        finally:
            await response.aclose()

    def database(self, name: str) -> AsyncDatabase:
        return AsyncDatabase(self, _spec.database_selector(name))

    async def list_databases(self, *, limit: int | None = None, cursor: str | None = None) -> models.DatabasePage:
        return await self._send(_spec.list_databases(self.project, limit, cursor))

    async def list_regions(self) -> models.Regions:
        return await self._send(_spec.list_regions())

    async def rename_database(self, database_id: str, name: str) -> models.Database:
        return await self._send(_spec.rename_database(self.project, database_id, name))

    async def create_database(
        self,
        name: str,
        *,
        ttl_seconds: int | None = None,
        regions: Sequence[str] | None = None,
        all_regions: bool | None = None,
        default_consistency: _spec.Consistency | None = None,
    ) -> models.Database:
        return await self._send(_spec.create_database(self.project, name, ttl_seconds, regions, all_regions, default_consistency))

    async def fork_database(
        self, source: str, name: str, *, point_in_time: str | None = None, ttl_seconds: int | None = None,
    ) -> models.Database:
        operation = await self._send(_spec.fork_database(self.project, source, name, point_in_time, ttl_seconds))
        if point_in_time is None:
            return operation
        done = await _backups._send_or_timeout_async(self._send, self.project, source, operation)
        return await self._send(_spec.get_database(self.project, done.database))

    async def set_database_ttl(self, database: str, ttl_seconds: int) -> models.Database:
        return await self._send(_spec.set_database_ttl(self.project, database, ttl_seconds))

    async def set_database_placement(
        self,
        database: str,
        *,
        regions: Sequence[str] | None = None,
        all_regions: bool | None = None,
    ) -> models.Database:
        return await self._send(_spec.set_database_placement(self.project, database, regions, all_regions))

    async def clear_database_ttl(self, database: str) -> models.Database:
        return await self._send(_spec.clear_database_ttl(self.project, database))

    async def delete_database(self, database: str) -> None:
        await self._send(_spec.delete_database(self.project, database))

    async def delete_database_if_id(self, database: str, expected_id: str) -> None:
        await self._send(_spec.delete_database_if_id(self.project, database, expected_id))

    def export_database(self, database: str) -> AsyncIterator[bytes]:
        return self._stream(_spec.export_database(self.project, _spec.database_selector(database)))

    async def create_scoped_key(
        self, database: str, access: str, *, project: str | None = None, ttl_seconds: int | None = None,
    ) -> models.ScopedKey:
        return await self._send(_scoped_key_call(
            self._api_key, *_tenant(self, project), database, access, ttl_seconds))

    async def mint_custom_token(self, user_id: str, *, database: str | None = None,
                          claims: Mapping[str, ClaimValue] | None = None, project: str | None = None) -> CustomToken:
        return await self._send(mint_custom_token_call(
            self._api_key, *_tenant(self, project), user_id, database, claims))

    async def create_api_key(
        self, organization: str, name: str, scopes: Sequence[str], *,
        project: str | None = None, database: str | None = None, expires_at: str | None = None,
        allowed_cidrs: Sequence[str] | None = None,
    ) -> models.ApiKeySecret:
        return await self._send(create_api_key_call(
            self._issuer, organization, project, name, scopes, database, expires_at, allowed_cidrs))

    async def list_api_keys(
        self, organization: str, *, project: str | None = None, limit: int | None = None, cursor: str | None = None,
    ) -> models.ApiKeyPage:
        return await self._send(list_api_keys_call(self._issuer, organization, project, limit, cursor))

    async def revoke_api_key(self, organization: str, key: str, *, project: str | None = None) -> None:
        await self._send(revoke_api_key_call(self._issuer, organization, project, key))

    async def rotate_api_key(
        self, organization: str, key: str, *, step_up_proof: str, project: str | None = None, grace_seconds: int = 0,
    ) -> models.ApiKeySecret:
        return await self._send(rotate_api_key_call(
            self._issuer, organization, project, key, grace_seconds, step_up_proof))

    async def set_user_api_key_policy(self, scopes: Sequence[str], *, project: str | None = None) -> models.UserApiKeyPolicy:
        return await self._send(set_user_api_key_policy_call(
            *_tenant(self, project), scopes))

    async def create_user_api_key(self, user: str, name: str, scopes: Sequence[str], *,
                              expires_at: str | None = None, project: str | None = None) -> models.UserApiKey:
        return await self._send(create_user_api_key_call(*_tenant(self, project),
                                                   user, name, scopes, expires_at))

    async def list_user_api_keys(self, user: str, *, project: str | None = None) -> models.UserApiKeyPage:
        return await self._send(list_user_api_keys_call(*_tenant(self, project), user))

    async def revoke_user_api_key(self, user: str, key: str, *, project: str | None = None) -> None:
        return await self._send(revoke_user_api_key_call(
            *_tenant(self, project), user, key))

    async def verify_user_api_key(self, key: str, *, project: str | None = None) -> models.UserApiKeyVerification:
        return await self._send(verify_user_api_key_call(*_tenant(self, project), key))

    async def create_project_client(
        self, name: str, client_type: str, redirect_uris: Sequence[str], *, step_up_proof: str,
        project: str | None = None, allowed_scopes: Sequence[str] | None = None,
        allowed_audiences: Sequence[str] | None = None, token_lifetime_minutes: int | None = None,
    ) -> models.ProjectClient:
        return await self._send(create_project_client_call(
            *_tenant(self, project), name, client_type, redirect_uris,
            step_up_proof, allowed_scopes, allowed_audiences, token_lifetime_minutes))

    async def list_project_clients(self, *, project: str | None = None) -> models.ProjectClientPage:
        return await self._send(list_project_clients_call(*_tenant(self, project)))

    async def update_project_client(
        self, client_id: str, name: str, redirect_uris: Sequence[str], *, step_up_proof: str,
        project: str | None = None, allowed_scopes: Sequence[str] | None = None,
        allowed_audiences: Sequence[str] | None = None, token_lifetime_minutes: int | None = None,
    ) -> models.ProjectClient:
        return await self._send(update_project_client_call(
            *_tenant(self, project), client_id, name, redirect_uris,
            step_up_proof, allowed_scopes, allowed_audiences, token_lifetime_minutes))

    async def rotate_project_client_secret(
        self, client_id: str, *, step_up_proof: str, grace_seconds: int | None = None, project: str | None = None,
    ) -> models.ProjectClientSecret:
        return await self._send(rotate_project_client_secret_call(
            *_tenant(self, project), client_id, grace_seconds, step_up_proof))

    async def delete_project_client(self, client_id: str, *, project: str | None = None) -> None:
        await self._send(delete_project_client_call(*_tenant(self, project),
                                                    client_id))

    async def create_project_invitation(self, email: str, *, redirect_url: str | None = None,
                                        project: str | None = None) -> models.ProjectInvitation:
        return await self._send(create_project_invitation_call(
            *_tenant(self, project), email, redirect_url))

    async def list_project_invitations(self, *, limit: int | None = None, after: str | None = None,
                                 project: str | None = None) -> models.ProjectInvitationPage:
        return await self._send(list_project_invitations_call(
            *_tenant(self, project), limit, after))

    async def revoke_project_invitation(self, invitation: str, *, project: str | None = None) -> None:
        await self._send(revoke_project_invitation_call(
            *_tenant(self, project), invitation))

    async def list_project_waitlist(self, *, limit: int | None = None, after: str | None = None,
                                 project: str | None = None) -> models.ProjectWaitlistPage:
        return await self._send(list_project_waitlist_call(
            *_tenant(self, project), limit, after))

    async def approve_project_waitlist(self, entry: str, *, project: str | None = None) -> None:
        await self._send(approve_project_waitlist_call(
            *_tenant(self, project), entry))

    async def reject_project_waitlist(self, entry: str, *, project: str | None = None) -> None:
        await self._send(reject_project_waitlist_call(
            *_tenant(self, project), entry))

    async def create_organization(self, project: str, request: _organizations.OrganizationRequest) -> _organizations.Organization:
        return await self._send(_organizations.create_organization_call(self._issuer, project, request))

    async def list_organizations(
        self, project: str, limit: int | None = None, after: str | None = None
    ) -> _organizations.OrganizationPage:
        return await self._send(_organizations.list_organizations_call(self._issuer, project, limit, after))

    async def get_organization(self, project: str, org: str) -> _organizations.Organization:
        return await self._send(_organizations.get_organization_call(self._issuer, project, org))

    async def update_organization(
        self, project: str, org: str, request: _organizations.OrganizationRequest
    ) -> _organizations.Organization:
        return await self._send(_organizations.update_organization_call(self._issuer, project, org, request))

    async def delete_organization(self, project: str, org: str) -> None:
        await self._send(_organizations.delete_organization_call(self._issuer, project, org))

    async def add_organization_member(
        self, project: str, org: str, request: _organizations.OrganizationMembershipRequest
    ) -> _organizations.OrganizationMembership:
        return await self._send(_organizations.add_organization_member_call(self._issuer, project, org, request))

    async def list_organization_members(
        self, project: str, org: str, limit: int | None = None, after: str | None = None
    ) -> _organizations.OrganizationMembershipPage:
        return await self._send(_organizations.list_organization_members_call(self._issuer, project, org, limit, after))

    async def update_organization_member(
        self, project: str, org: str, user: str, request: _organizations.OrganizationMembershipRequest
    ) -> _organizations.OrganizationMembership:
        return await self._send(_organizations.update_organization_member_call(self._issuer, project, org, user, request))

    async def delete_organization_member(self, project: str, org: str, user: str) -> None:
        await self._send(_organizations.delete_organization_member_call(self._issuer, project, org, user))

    async def create_organization_invitation(
        self, project: str, org: str, request: _organizations.OrganizationInvitationRequest
    ) -> _organizations.OrganizationInvitation:
        return await self._send(_organizations.create_organization_invitation_call(self._issuer, project, org, request))

    async def list_organization_invitations(
        self, project: str, org: str, limit: int | None = None, after: str | None = None
    ) -> _organizations.OrganizationInvitationPage:
        return await self._send(_organizations.list_organization_invitations_call(self._issuer, project, org, limit, after))

    async def revoke_organization_invitation(self, project: str, org: str, invitation: str) -> None:
        await self._send(_organizations.revoke_organization_invitation_call(self._issuer, project, org, invitation))

    async def add_organization_domain(
        self, project: str, org: str, request: _organizations.OrganizationDomainRequest
    ) -> _organizations.OrganizationDomain:
        return await self._send(_organizations.add_organization_domain_call(self._issuer, project, org, request))

    async def list_organization_domains(self, project: str, org: str) -> _organizations.OrganizationDomainPage:
        return await self._send(_organizations.list_organization_domains_call(self._issuer, project, org))

    async def verify_organization_domain(self, project: str, org: str, domain: str) -> _organizations.OrganizationDomain:
        return await self._send(_organizations.verify_organization_domain_call(self._issuer, project, org, domain))

    async def delete_organization_domain(self, project: str, org: str, domain: str) -> None:
        await self._send(_organizations.delete_organization_domain_call(self._issuer, project, org, domain))

    async def get_settings(self, *, project: str | None = None) -> models.ProjectSettings:
        return await self._send(get_settings_call(*_tenant(self, project)))

    async def update_settings(self, update: models.ProjectSettingsUpdate, *, project: str | None = None,
                             ) -> models.ProjectSettings:
        return await self._send(
            update_settings_call(*_tenant(self, project), update))

    async def list_users(self, *, search: str | None = None, limit: int | None = None, after: str | None = None,
                         project: str | None = None) -> models.ProjectUserPage:
        return await self._send(list_users_call(*_tenant(self, project),
                                                search, limit, after))

    async def create_user(self, request: models.CreateProjectUser, *, project: str | None = None) -> models.ProjectUser:
        return await self._send(create_user_call(*_tenant(self, project), request))

    async def update_user(
        self, user_id: str, request: models.UpdateProjectUser, *, project: str | None = None
    ) -> models.ProjectUser:
        return await self._send(
            update_user_call(*_tenant(self, project), user_id, request)
        )

    async def import_users(
        self, users: list[models.ImportProjectUser], *, project: str | None = None
    ) -> models.ImportProjectUsersResult:
        return await self._send(import_users_call(*_tenant(self, project), users))

    async def get_user(self, user_id: str, *, project: str | None = None) -> models.ProjectUser:
        return await self._send(get_user_call(*_tenant(self, project),
                                              user_id))

    async def disable_user(self, user_id: str, *, project: str | None = None) -> models.ProjectUser:
        return await self._send(disable_user_call(*_tenant(self, project),
                                                  user_id))

    async def enable_user(self, user_id: str, *, project: str | None = None) -> models.ProjectUser:
        return await self._send(enable_user_call(*_tenant(self, project),
                                                 user_id))

    async def delete_user(self, user_id: str, *, project: str | None = None) -> None:
        await self._send(delete_user_call(*_tenant(self, project), user_id))

    async def export_users(self, *, limit: int | None = None, after: str | None = None,
                           project: str | None = None) -> models.ProjectUserExportPage:
        return await self._send(export_users_call(*_tenant(self, project),
                                                  limit, after))

    async def set_provider(self, provider: str, update: models.ProjectProviderUpdate, *,
                           project: str | None = None) -> models.ProjectSettings:
        return await self._send(set_provider_call(*_tenant(self, project),
                                                  provider, update))

    async def delete_provider(self, provider: str, *, project: str | None = None) -> models.ProjectSettings:
        return await self._send(delete_provider_call(*_tenant(self, project),
                                                     provider))

    async def list_sessions(self, *, user_id: str | None = None, limit: int | None = None,
                            after: str | None = None, project: str | None = None) -> models.ProjectSessionPage:
        return await self._send(list_sessions_call(*_tenant(self, project),
                                                   user_id, limit, after))

    async def sign_out_user(self, user_id: str, *, project: str | None = None) -> None:
        await self._send(sign_out_user_call(*_tenant(self, project), user_id))

    async def revoke_session(self, session_id: str, *, project: str | None = None) -> None:
        await self._send(revoke_session_call(*_tenant(self, project),
                                             session_id))

class AsyncDatabase:
    def __init__(self, client: AsyncTerraScale, name: str):
        self._c = client
        self.name = name

    def __repr__(self) -> str:
        return f"AsyncDatabase({self.name!r})"

    @property
    def _p(self) -> tuple[str, str]:
        return self._c.project, self.name

    async def info(self) -> models.Database:
        return await self._c._send(_spec.get_database(*self._p))

    async def get_rules(self) -> _rules.Rules:
        return await self._c._send(_rules.rules_call(*self._p, "get"))

    async def deploy_rules(self, rules: _rules.RulesSet, current_version: str | None) -> _rules.Rules:
        return await self._c._send(_rules.rules_call(*self._p, "deploy", rules=rules, current_version=current_version))

    async def list_rules_versions(self, *, after: str | None = None, limit: int | None = None) -> _rules.RulesVersionPage:
        return await self._c._send(_rules.rules_call(*self._p, "versions", after=after, limit=limit))

    async def get_rules_version(self, version: str) -> _rules.Rules:
        return await self._c._send(_rules.rules_call(*self._p, "version", version=version))

    async def restore_rules(self, version: str, current_version: str | None) -> _rules.Rules:
        return await self._c._send(_rules.rules_call(*self._p, "restore", version=version, current_version=current_version))

    async def test_rules(self, request: _rules.RulesTestRequest) -> _rules.RulesTestResult:
        return await self._c._send(_rules.rules_call(*self._p, "test", request=request))

    async def list_database_logs(self, status: str | None = None) -> models.DatabaseRequestLogs:
        return await self._c._send(_spec.list_database_logs(*self._p, status))

    async def regions(self) -> models.DatabaseRegions:
        return await self._c._send(_spec.get_database_regions(*self._p))

    async def delete(self) -> None:
        await self._c._send(_spec.delete_database(*self._p))

    async def branch(self, name: str) -> models.Database:
        return await self._c._send(_spec.branch_database(*self._p, name))

    async def branch_if_id(self, name: str, expected_id: str) -> models.Database:
        return await self._c._send(_spec.branch_database_if_id(*self._p, name, expected_id))

    async def delete_if_id(self, expected_id: str) -> None:
        await self._c._send(_spec.delete_database_if_id(*self._p, expected_id))

    @property
    def bookmark(self) -> str | None:
        return self._c._bookmarks.get(self.name)

    @bookmark.setter
    def bookmark(self, value: str | None) -> None:
        self._c._bookmarks.set(self.name, value)

    @overload
    async def query(self, sql: str, args: Args = None, *, consistency: _spec.Consistency | None = None,
              bookmark: str | None = None) -> builtins.list[Row]: ...

    @overload
    async def query(self, sql: str, args: Args, mapper: Callable[..., T], *, consistency: _spec.Consistency | None = None,
              bookmark: str | None = None) -> builtins.list[T]: ...

    async def query(self, sql: str, args: Args = None, mapper: Callable[..., T] | None = None, *,
              consistency: _spec.Consistency | None = None, bookmark: str | None = None) -> builtins.list[Any]:
        result = await self.execute(sql, args, consistency=consistency, bookmark=bookmark)
        return result.rows if mapper is None else result.as_(mapper)

    async def execute(self, sql: str | Statement, args: Args = None, *, consistency: _spec.Consistency | None = None,
                bookmark: str | None = None) -> StatementResult:
        call = _spec.execute(*self._p, _sql.statements([_sql.statement(sql, args)]), consistency, bookmark)
        return (await self._c._send(call))[0]

    async def batch(self, statements: Sequence[str | tuple[str, Args] | Statement], *,
              consistency: _spec.Consistency | None = None, bookmark: str | None = None) -> builtins.list[StatementResult]:
        return await self._c._send(_spec.execute(*self._p, _sql.statements(statements), consistency, bookmark))

    def connect(self, *, consistency: _spec.Consistency | None = None) -> AsyncSqlSession:
        """A SQL WebSocket session: ``await db.connect()`` or ``async with db.connect() as session``.
        ``consistency`` is the session's default fast or strong mode."""
        return AsyncSqlSession(self, consistency=consistency)

    def shape(self, table: str, *, where: str | None = None, columns: Sequence[str] | None = None) -> AsyncShape:
        return AsyncShape(self._c, self.name, table, where=where, columns=columns)
