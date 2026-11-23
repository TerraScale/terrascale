"""Synchronous client."""

from __future__ import annotations

import builtins
import os
import time
from collections.abc import Callable, Iterator, Mapping, Sequence
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
    APIKeyTokenSource,
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
from .custom_tokens import ClaimValue, CustomToken, mint_custom_token_call
from .errors import APIError, error_for
from .ids import validate_id
from .session import SqlSession
from .shapes import Shape
from .sql import Args, Row, Statement, StatementResult

__all__ = ["TerraScale", "Database"]

T = TypeVar("T")


def _tenant(client: Any, project: str | None) -> tuple[str | None, str]:
    return client._issuer, client.project if project is None else project


def _apply(client: Any, err: APIError, action: _spec.Next, token: str) -> _spec.Next:
    if action.kind == "reject" and client._source is not None:
        client._source.reject(err)
    elif action.kind == "refresh" and client._source is not None:
        client._source.invalidate(token)
    return action


def _settings(api_key: str | None, project: str | None, token: Any, base_url: str | None,
              issuer: str | None) -> tuple[str | None, str, str]:
    if api_key is not None and token is not None:
        raise ValueError("pass either api_key or token, not both")
    if token is None and api_key is None:
        api_key = os.environ.get("TERRASCALE_API_KEY")
    project = project if project is not None else os.environ.get("TERRASCALE_PROJECT_ID")
    if not project:
        raise ValueError("project is required: pass project= or set TERRASCALE_PROJECT_ID")
    validate_id(project, "project")
    if not api_key and token is None:
        raise ValueError("an API key is required: pass api_key= or set TERRASCALE_API_KEY")
    base = _spec.service_origin(base_url or os.environ.get("TERRASCALE_BASE_URL") or _spec.DEFAULT_BASE_URL, "base_url",
                                _spec.API_SERVICE)
    if issuer is not None:
        _spec.issuer_parts(issuer)
    if api_key and issuer is None and base != _spec.DEFAULT_BASE_URL:
        raise ValueError("set issuer to the ts-auth that exchanges the API key from api_key= or TERRASCALE_API_KEY: with a "
                         "base_url other than https://api.terrascale.tech the key is not sent to production ts-auth")
    return api_key, project, base


class TerraScale:
    """A project-scoped TerraBase client.

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
        token: str | Callable[[], str] | None = None,
        base_url: str | None = None,
        region: str | None = None,
        issuer: str | None = None,
        scopes: Sequence[str] | None = None,
        timeout: float = 30.0,
        max_attempts: int = _spec.MAX_ATTEMPTS,
        http_client: httpx.Client | None = None,
    ):
        api_key, self.project, self.base_url = _settings(api_key, project, token, base_url, issuer)
        self._http = http_client or httpx.Client(timeout=timeout, follow_redirects=False, http2=True,
                                                 mounts={"http://": httpx.HTTPTransport(http2=True)})
        self._owns_http = http_client is None
        self._max_attempts = max(1, max_attempts)
        self._source: APIKeyTokenSource | None = None
        self._token = token
        self._api_key = api_key
        self._issuer = issuer
        if token is None:
            assert api_key is not None
            self._source = APIKeyTokenSource(api_key, project=self.project, scopes=scopes, issuer=issuer,
                                             max_attempts=self._max_attempts, http_client=self._http)
        self._headers = {"User-Agent": _spec.USER_AGENT_PREFIX + __version__, "Accept": "application/json"}
        if region is not None and (not isinstance(region, str) or region.strip()):
            self._headers[_spec.REGION_PIN] = _spec.region(region)
        self._bookmarks = _spec.Bookmarks()

    @property
    def bookmarks(self) -> dict[str, str]:
        """The newest TerraScale-Bookmark seen per database handle name, sent on its later requests."""
        return self._bookmarks.snapshot()


    def close(self) -> None:
        if self._source is not None:
            self._source.close()
        if self._owns_http:
            self._http.close()

    def __enter__(self) -> TerraScale:
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()


    def _bearer(self) -> str:
        if self._source is not None:
            return self._source.token()
        return self._token() if callable(self._token) else str(self._token)

    def _send(self, call: Call) -> Any:
        url = call.url(self.base_url)
        content = call.content()
        refreshed = False
        attempt = 0
        conflicts = 0
        while True:
            direct = "Authorization" in call.headers
            project_key = _spec.project_bearer(self._api_key, call)
            token = "" if direct or call.anonymous else project_key or self._bearer()
            headers = _spec.request_headers(self._headers, call, self._bookmarks, direct=direct, token=token,
                                            has_body=content is not None)
            try:
                if direct:
                    response = self._http.send(httpx.Request(call.method, url, headers=headers, content=content),
                                               auth=None, follow_redirects=False)
                else:
                    response = self._http.request(
                        call.method, url, params=call.params or None, headers=headers, content=content,
                        timeout=call.timeout if call.timeout is not None else httpx.USE_CLIENT_DEFAULT)
            except (httpx.TransportError, httpx.DecodingError) as exc:
                attempt += 1
                time.sleep(_spec.after_transport(call, exc, attempt, self._max_attempts))
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
                time.sleep(action.seconds)
                continue
            raise err

    def _stream(self, call: Call) -> Iterator[bytes]:
        """Send a replay-safe GET and yield its body in chunks; the response closes when the iterator does."""
        url = call.url(self.base_url)
        attempt = 0
        refreshed = False
        while True:
            token = self._bearer()
            headers = _spec.request_headers(self._headers, call, self._bookmarks, direct=False, token=token, has_body=False)
            request = httpx.Request(call.method, url, headers=headers)
            try:
                response = self._http.send(request, auth=None, stream=True, follow_redirects=False)
            except httpx.TransportError as exc:
                attempt += 1
                if attempt >= self._max_attempts:
                    raise _spec.transport_failure(call.method, attempt, exc) from exc
                time.sleep(_spec.backoff(attempt, None))
                continue
            if 200 <= response.status_code < 300:
                break
            err = _error(response)
            response.close()
            action = _apply(self, err, _spec.after_status(
                call, err, attempt=attempt, conflicts=2, max_attempts=self._max_attempts, refreshed=refreshed,
                direct=False, has_source=self._source is not None, project_key=False), token)
            if action.kind == "refresh":
                refreshed = True
                continue
            if action.kind == "sleep":
                attempt = action.attempt
                time.sleep(action.seconds)
                continue
            raise err
        try:
            yield from response.iter_bytes()
        finally:
            response.close()


    def database(self, name: str) -> Database:
        """A handle by current database name or immutable ULID; no request is made."""
        return Database(self, _spec.database_selector(name))

    def list_databases(self, *, limit: int | None = None, cursor: str | None = None) -> models.DatabasePage:
        return self._send(_spec.list_databases(self.project, limit, cursor))

    def list_regions(self) -> models.Regions:
        """The anonymous ordered serving-region catalog, sent without a bearer token."""
        return self._send(_spec.list_regions())

    def rename_database(self, database_id: str, name: str) -> models.Database:
        """Rename by immutable ULID or current name with ``databases:write``; return metadata.

        See docs/database-rename.md for producer availability and conflict handling.
        ID, data, keys and placement stay unchanged. Invalid names return
        400 (or local ValueError); known same-region duplicates return 409.
        After convergence the old name has no alias and may be claimed again.
        Update name-based connection strings and handles; ID-based strings remain stable. Reconcile uncertain failures
        before explicitly retrying this mutation.
        """
        return self._send(_spec.rename_database(self.project, database_id, name))

    def create_database(
        self,
        name: str,
        *,
        ttl_seconds: int | None = None,
        regions: Sequence[str] | None = None,
        all_regions: bool | None = None,
        default_consistency: _spec.Consistency | None = None,
    ) -> models.Database:
        """Create in the selected regions, or the answering region; every region accepts writes.
        ``default_consistency`` is fast (the server default) or strong; TTL expiry deletes the database and backups."""
        return self._send(_spec.create_database(self.project, name, ttl_seconds, regions, all_regions, default_consistency))

    def fork_database(
        self, source: str, name: str, *, point_in_time: str | None = None, ttl_seconds: int | None = None,
    ) -> models.Database:
        """Fork the current database state, or an RFC 3339 point in its restore window, with an independent TTL."""
        operation = self._send(_spec.fork_database(self.project, source, name, point_in_time, ttl_seconds))
        if point_in_time is None:
            return operation
        done = _backups._send_or_timeout(self._send, self.project, source, operation)
        return self._send(_spec.get_database(self.project, done.database))

    def set_database_ttl(self, database: str, ttl_seconds: int) -> models.Database:
        """Set expiry in 60 to 31,536,000 seconds; the database and backups are deleted on expiry."""
        return self._send(_spec.set_database_ttl(self.project, database, ttl_seconds))

    def set_database_placement(
        self,
        database: str,
        *,
        regions: Sequence[str] | None = None,
        all_regions: bool | None = None,
    ) -> models.Database:
        """Set every region holding the database, or all regions; placement refusals are never automatically retried."""
        return self._send(_spec.set_database_placement(self.project, database, regions, all_regions))

    def clear_database_ttl(self, database: str) -> models.Database:
        """Remove the database's expiry."""
        return self._send(_spec.clear_database_ttl(self.project, database))

    def delete_database(self, database: str) -> None:
        """Delete the database and backups from any region holding it."""
        self._send(_spec.delete_database(self.project, database))

    def delete_database_if_id(self, database: str, expected_id: str) -> None:
        """Delete only when the name still resolves to expected_id; a changed identity answers 409."""
        self._send(_spec.delete_database_if_id(self.project, database, expected_id))

    def export_database(self, database: str) -> Iterator[bytes]:
        """Stream the database as one SQL dump (BEGIN … COMMIT) with ``sql:read``.

        Any region holding the database serves the export; consume the iterator to
        read the dump and close it when done.
        """
        return self._stream(_spec.export_database(self.project, _spec.database_selector(database)))

    def create_scoped_key(
        self, database: str, access: str, *, project: str | None = None, ttl_seconds: int | None = None,
    ) -> models.ScopedKey:
        """Mint a key for one database ULID, using this client's API key; the default lifetime is 3600 seconds."""
        return self._send(_scoped_key_call(
            self._api_key, *_tenant(self, project), database, access, ttl_seconds))

    def mint_custom_token(self, user_id: str, *, database: str | None = None,
                          claims: Mapping[str, ClaimValue] | None = None, project: str | None = None) -> CustomToken:
        return self._send(mint_custom_token_call(
            self._api_key, *_tenant(self, project), user_id, database, claims))

    def create_api_key(
        self, organization: str, name: str, scopes: Sequence[str], *,
        project: str | None = None, database: str | None = None, expires_at: str | None = None,
        allowed_cidrs: Sequence[str] | None = None,
    ) -> models.ApiKeySecret:
        """Create an organization key, or a project key with ``project``; the whole key string is answered once."""
        return self._send(create_api_key_call(
            self._issuer, organization, project, name, scopes, database, expires_at, allowed_cidrs))

    def list_api_keys(
        self, organization: str, *, project: str | None = None, limit: int | None = None, cursor: str | None = None,
    ) -> models.ApiKeyPage:
        """List an organization's keys, or a project's, in creation order; never secrets."""
        return self._send(list_api_keys_call(self._issuer, organization, project, limit, cursor))

    def revoke_api_key(self, organization: str, key: str, *, project: str | None = None) -> None:
        """Revoke a key at once; tokens already issued live out their 15 minutes."""
        self._send(revoke_api_key_call(self._issuer, organization, project, key))

    def rotate_api_key(
        self, organization: str, key: str, *, step_up_proof: str, project: str | None = None, grace_seconds: int = 0,
    ) -> models.ApiKeySecret:
        return self._send(rotate_api_key_call(
            self._issuer, organization, project, key, grace_seconds, step_up_proof))


    def set_user_api_key_policy(self, scopes: Sequence[str], *, project: str | None = None) -> models.UserApiKeyPolicy:
        return self._send(set_user_api_key_policy_call(
            *_tenant(self, project), scopes))

    def create_user_api_key(self, user: str, name: str, scopes: Sequence[str], *,
                              expires_at: str | None = None, project: str | None = None) -> models.UserApiKey:
        return self._send(create_user_api_key_call(*_tenant(self, project),
                                                   user, name, scopes, expires_at))

    def list_user_api_keys(self, user: str, *, project: str | None = None) -> models.UserApiKeyPage:
        return self._send(list_user_api_keys_call(*_tenant(self, project), user))

    def revoke_user_api_key(self, user: str, key: str, *, project: str | None = None) -> None:
        return self._send(revoke_user_api_key_call(
            *_tenant(self, project), user, key))

    def verify_user_api_key(self, key: str, *, project: str | None = None) -> models.UserApiKeyVerification:
        return self._send(verify_user_api_key_call(*_tenant(self, project), key))

    def create_project_client(
        self, name: str, client_type: str, redirect_uris: Sequence[str], *, step_up_proof: str,
        project: str | None = None, allowed_scopes: Sequence[str] | None = None,
        allowed_audiences: Sequence[str] | None = None, token_lifetime_minutes: int | None = None,
    ) -> models.ProjectClient:
        """Register a public or confidential OAuth client; a confidential secret is answered once."""
        return self._send(create_project_client_call(
            *_tenant(self, project), name, client_type, redirect_uris,
            step_up_proof, allowed_scopes, allowed_audiences, token_lifetime_minutes))

    def list_project_clients(self, *, project: str | None = None) -> models.ProjectClientPage:
        """List every registered client of the project in one page, without secrets."""
        return self._send(list_project_clients_call(*_tenant(self, project)))

    def update_project_client(
        self, client_id: str, name: str, redirect_uris: Sequence[str], *, step_up_proof: str,
        project: str | None = None, allowed_scopes: Sequence[str] | None = None,
        allowed_audiences: Sequence[str] | None = None, token_lifetime_minutes: int | None = None,
    ) -> models.ProjectClient:
        """Rename a client and replace its redirect URIs; the id, type and secret stay unchanged."""
        return self._send(update_project_client_call(
            *_tenant(self, project), client_id, name, redirect_uris,
            step_up_proof, allowed_scopes, allowed_audiences, token_lifetime_minutes))

    def rotate_project_client_secret(
        self, client_id: str, *, step_up_proof: str, grace_seconds: int | None = None, project: str | None = None,
    ) -> models.ProjectClientSecret:
        """Issue a new secret for a confidential client; the previous one keeps working for grace_seconds (0 to 86400)."""
        return self._send(rotate_project_client_secret_call(
            *_tenant(self, project), client_id, grace_seconds, step_up_proof))

    def delete_project_client(self, client_id: str, *, project: str | None = None) -> None:
        """Delete a registered client."""
        self._send(delete_project_client_call(*_tenant(self, project),
                                              client_id))


    def create_project_invitation(self, email: str, *, redirect_url: str | None = None,
                                        project: str | None = None) -> models.ProjectInvitation:
        return self._send(create_project_invitation_call(
            *_tenant(self, project), email, redirect_url))

    def list_project_invitations(self, *, limit: int | None = None, after: str | None = None,
                                 project: str | None = None) -> models.ProjectInvitationPage:
        return self._send(list_project_invitations_call(
            *_tenant(self, project), limit, after))

    def revoke_project_invitation(self, invitation: str, *, project: str | None = None) -> None:
        self._send(revoke_project_invitation_call(
            *_tenant(self, project), invitation))

    def list_project_waitlist(self, *, limit: int | None = None, after: str | None = None,
                                 project: str | None = None) -> models.ProjectWaitlistPage:
        return self._send(list_project_waitlist_call(
            *_tenant(self, project), limit, after))

    def approve_project_waitlist(self, entry: str, *, project: str | None = None) -> None:
        self._send(approve_project_waitlist_call(
            *_tenant(self, project), entry))

    def reject_project_waitlist(self, entry: str, *, project: str | None = None) -> None:
        self._send(reject_project_waitlist_call(
            *_tenant(self, project), entry))

    def create_organization(self, project: str, request: _organizations.OrganizationRequest) -> _organizations.Organization:
        """POST /v1/{project}/organizations."""
        return self._send(_organizations.create_organization_call(self._issuer, project, request))

    def list_organizations(
        self, project: str, limit: int | None = None, after: str | None = None
    ) -> _organizations.OrganizationPage:
        """GET /v1/{project}/organizations."""
        return self._send(_organizations.list_organizations_call(self._issuer, project, limit, after))

    def get_organization(self, project: str, org: str) -> _organizations.Organization:
        """GET /v1/{project}/organizations/{org}."""
        return self._send(_organizations.get_organization_call(self._issuer, project, org))

    def update_organization(
        self, project: str, org: str, request: _organizations.OrganizationRequest
    ) -> _organizations.Organization:
        """PUT /v1/{project}/organizations/{org}."""
        return self._send(_organizations.update_organization_call(self._issuer, project, org, request))

    def delete_organization(self, project: str, org: str) -> None:
        """DELETE /v1/{project}/organizations/{org}."""
        self._send(_organizations.delete_organization_call(self._issuer, project, org))

    def add_organization_member(
        self, project: str, org: str, request: _organizations.OrganizationMembershipRequest
    ) -> _organizations.OrganizationMembership:
        """POST /v1/{project}/organizations/{org}/memberships."""
        return self._send(_organizations.add_organization_member_call(self._issuer, project, org, request))

    def list_organization_members(
        self, project: str, org: str, limit: int | None = None, after: str | None = None
    ) -> _organizations.OrganizationMembershipPage:
        """GET /v1/{project}/organizations/{org}/memberships."""
        return self._send(_organizations.list_organization_members_call(self._issuer, project, org, limit, after))

    def update_organization_member(
        self, project: str, org: str, user: str, request: _organizations.OrganizationMembershipRequest
    ) -> _organizations.OrganizationMembership:
        """PUT /v1/{project}/organizations/{org}/memberships/{user}."""
        return self._send(_organizations.update_organization_member_call(self._issuer, project, org, user, request))

    def delete_organization_member(self, project: str, org: str, user: str) -> None:
        """DELETE /v1/{project}/organizations/{org}/memberships/{user}."""
        self._send(_organizations.delete_organization_member_call(self._issuer, project, org, user))

    def create_organization_invitation(
        self, project: str, org: str, request: _organizations.OrganizationInvitationRequest
    ) -> _organizations.OrganizationInvitation:
        """POST /v1/{project}/organizations/{org}/invitations."""
        return self._send(_organizations.create_organization_invitation_call(self._issuer, project, org, request))

    def list_organization_invitations(
        self, project: str, org: str, limit: int | None = None, after: str | None = None
    ) -> _organizations.OrganizationInvitationPage:
        """GET /v1/{project}/organizations/{org}/invitations."""
        return self._send(_organizations.list_organization_invitations_call(self._issuer, project, org, limit, after))

    def revoke_organization_invitation(self, project: str, org: str, invitation: str) -> None:
        """DELETE /v1/{project}/organizations/{org}/invitations/{invitation}."""
        self._send(_organizations.revoke_organization_invitation_call(self._issuer, project, org, invitation))

    def add_organization_domain(
        self, project: str, org: str, request: _organizations.OrganizationDomainRequest
    ) -> _organizations.OrganizationDomain:
        """POST /v1/{project}/organizations/{org}/domains."""
        return self._send(_organizations.add_organization_domain_call(self._issuer, project, org, request))

    def list_organization_domains(self, project: str, org: str) -> _organizations.OrganizationDomainPage:
        """GET /v1/{project}/organizations/{org}/domains."""
        return self._send(_organizations.list_organization_domains_call(self._issuer, project, org))

    def verify_organization_domain(self, project: str, org: str, domain: str) -> _organizations.OrganizationDomain:
        """POST /v1/{project}/organizations/{org}/domains/{domain}/verify."""
        return self._send(_organizations.verify_organization_domain_call(self._issuer, project, org, domain))

    def delete_organization_domain(self, project: str, org: str, domain: str) -> None:
        """DELETE /v1/{project}/organizations/{org}/domains/{domain}."""
        self._send(_organizations.delete_organization_domain_call(self._issuer, project, org, domain))

    def get_settings(self, *, project: str | None = None) -> models.ProjectSettings:
        """Read the project's end-user sign-in settings; viewer access with the caller's sign-in token."""
        return self._send(get_settings_call(*_tenant(self, project)))

    def update_settings(self, update: models.ProjectSettingsUpdate, *, project: str | None = None,
                       ) -> models.ProjectSettings:
        """Update settings; omitted parts stay unchanged and admin access is required."""
        return self._send(update_settings_call(*_tenant(self, project), update))

    def list_users(self, *, search: str | None = None, limit: int | None = None, after: str | None = None,
                   project: str | None = None) -> models.ProjectUserPage:
        """List the project's end users in creation order; ``search`` matches an email substring."""
        return self._send(list_users_call(*_tenant(self, project), search,
                                          limit, after))

    def create_user(self, request: models.CreateProjectUser, *, project: str | None = None) -> models.ProjectUser:
        return self._send(create_user_call(*_tenant(self, project), request))

    def update_user(self, user_id: str, request: models.UpdateProjectUser, *, project: str | None = None) -> models.ProjectUser:
        return self._send(update_user_call(*_tenant(self, project), user_id, request))

    def import_users(
        self, users: list[models.ImportProjectUser], *, project: str | None = None
    ) -> models.ImportProjectUsersResult:
        return self._send(import_users_call(*_tenant(self, project), users))

    def get_user(self, user_id: str, *, project: str | None = None) -> models.ProjectUser:
        """Read one end user by id."""
        return self._send(get_user_call(*_tenant(self, project), user_id))

    def disable_user(self, user_id: str, *, project: str | None = None) -> models.ProjectUser:
        """Disable a user; every session ends and refresh tokens stop at once."""
        return self._send(disable_user_call(*_tenant(self, project), user_id))

    def enable_user(self, user_id: str, *, project: str | None = None) -> models.ProjectUser:
        """Re-enable a disabled user."""
        return self._send(enable_user_call(*_tenant(self, project), user_id))

    def delete_user(self, user_id: str, *, project: str | None = None) -> None:
        """Delete a user's account; the address can sign up again."""
        self._send(delete_user_call(*_tenant(self, project), user_id))

    def export_users(self, *, limit: int | None = None, after: str | None = None,
                     project: str | None = None) -> models.ProjectUserExportPage:
        """Fetch one audited directory-export page; pass the previous page's ``next_cursor`` as ``after``."""
        return self._send(export_users_call(*_tenant(self, project), limit,
                                            after))

    def set_provider(self, provider: str, update: models.ProjectProviderUpdate, *,
                     project: str | None = None) -> models.ProjectSettings:
        """Configure a supported federated sign-in provider; an omitted secret is kept."""
        return self._send(set_provider_call(*_tenant(self, project), provider,
                                            update))

    def delete_provider(self, provider: str, *, project: str | None = None) -> models.ProjectSettings:
        """Remove a federated sign-in provider; the answered settings no longer carry it."""
        return self._send(delete_provider_call(*_tenant(self, project),
                                               provider))

    def list_sessions(self, *, user_id: str | None = None, limit: int | None = None, after: str | None = None,
                      project: str | None = None) -> models.ProjectSessionPage:
        """List live sessions, optionally of one user, in creation order."""
        return self._send(list_sessions_call(*_tenant(self, project), user_id,
                                             limit, after))

    def sign_out_user(self, user_id: str, *, project: str | None = None) -> None:
        """End all of a project user's sessions and refresh tokens (admin)."""
        self._send(sign_out_user_call(*_tenant(self, project), user_id))

    def revoke_session(self, session_id: str, *, project: str | None = None) -> None:
        """Revoke one session; the session is signed out at once."""
        self._send(revoke_session_call(*_tenant(self, project), session_id))


def _error(response: httpx.Response, *, mutation: bool = False) -> APIError:
    try:
        body = response.json()
    except ValueError:
        body = None
    if isinstance(body, dict) and isinstance(body.get("error"), str):
        body = {"error": {"code": body["error"], "message": body.get("error_description", "")}}
    return error_for(response.status_code, body, response.headers, mutation=mutation)


class Database:

    def __init__(self, client: TerraScale, name: str):
        self._c = client
        self.name = name

    def __repr__(self) -> str:
        return f"Database({self.name!r})"

    @property
    def _p(self) -> tuple[str, str]:
        return self._c.project, self.name

    def info(self) -> models.Database:
        return self._c._send(_spec.get_database(*self._p))

    def get_rules(self) -> _rules.Rules:
        return self._c._send(_rules.rules_call(*self._p, "get"))

    def deploy_rules(self, rules: _rules.RulesSet, current_version: str | None) -> _rules.Rules:
        return self._c._send(_rules.rules_call(*self._p, "deploy", rules=rules, current_version=current_version))

    def list_rules_versions(self, *, after: str | None = None, limit: int | None = None) -> _rules.RulesVersionPage:
        return self._c._send(_rules.rules_call(*self._p, "versions", after=after, limit=limit))

    def get_rules_version(self, version: str) -> _rules.Rules:
        return self._c._send(_rules.rules_call(*self._p, "version", version=version))

    def restore_rules(self, version: str, current_version: str | None) -> _rules.Rules:
        return self._c._send(_rules.rules_call(*self._p, "restore", version=version, current_version=current_version))

    def test_rules(self, request: _rules.RulesTestRequest) -> _rules.RulesTestResult:
        return self._c._send(_rules.rules_call(*self._p, "test", request=request))

    def list_database_logs(self, status: str | None = None) -> models.DatabaseRequestLogs:
        """The newest 100 recorded requests within 24 hours; ``status`` filters by HTTP status class.
        Recording is best effort, without a cursor or export guarantee."""
        return self._c._send(_spec.list_database_logs(*self._p, status))


    def regions(self) -> models.DatabaseRegions:
        """Read each selected region's applied bookmark, lag and health with databases:read; not acknowledgements."""
        return self._c._send(_spec.get_database_regions(*self._p))

    def delete(self) -> None:
        """Delete from any region holding the database. HTTP 204 confirms logical deletion and local cleanup;
        remote erasure is retried durably and may complete later."""
        self._c._send(_spec.delete_database(*self._p))

    def branch(self, name: str) -> models.Database:
        """Create an independent copy named ``name``."""
        return self._c._send(_spec.branch_database(*self._p, name))

    def branch_if_id(self, name: str, expected_id: str) -> models.Database:
        """Branch only when the name still resolves to expected_id; a changed identity answers 409."""
        return self._c._send(_spec.branch_database_if_id(*self._p, name, expected_id))

    def delete_if_id(self, expected_id: str) -> None:
        """Delete only when the name still resolves to expected_id; a changed identity answers 409."""
        self._c._send(_spec.delete_database_if_id(*self._p, expected_id))

    @property
    def bookmark(self) -> str | None:
        """The newest TerraScale-Bookmark seen for this database; later requests send it to read their writes in any region."""
        return self._c._bookmarks.get(self.name)

    @bookmark.setter
    def bookmark(self, value: str | None) -> None:
        self._c._bookmarks.set(self.name, value)

    @overload
    def query(self, sql: str, args: Args = None, *, consistency: _spec.Consistency | None = None,
              bookmark: str | None = None) -> builtins.list[Row]: ...

    @overload
    def query(self, sql: str, args: Args, mapper: Callable[..., T], *, consistency: _spec.Consistency | None = None,
              bookmark: str | None = None) -> builtins.list[T]: ...

    def query(self, sql: str, args: Args = None, mapper: Callable[..., T] | None = None, *,
              consistency: _spec.Consistency | None = None, bookmark: str | None = None) -> builtins.list[Any]:
        """Run one statement and return its rows, or ``mapper(**row)`` for each row."""
        result = self.execute(sql, args, consistency=consistency, bookmark=bookmark)
        return result.rows if mapper is None else result.as_(mapper)

    def execute(self, sql: str | Statement, args: Args = None, *, consistency: _spec.Consistency | None = None,
                bookmark: str | None = None) -> StatementResult:
        """Run one statement, reads and writes, with positional or named arguments.
        ``consistency`` is fast or strong; ``bookmark`` replaces this database's bookmark for the call."""
        call = _spec.execute(*self._p, _sql.statements([_sql.statement(sql, args)]), consistency, bookmark)
        return (self._c._send(call))[0]

    def batch(self, statements: Sequence[str | tuple[str, Args] | Statement], *,
              consistency: _spec.Consistency | None = None, bookmark: str | None = None) -> builtins.list[StatementResult]:
        """Run 1 to 256 statements as one all-or-nothing transaction; one result per statement."""
        return self._c._send(_spec.execute(*self._p, _sql.statements(statements), consistency, bookmark))

    def connect(self, *, consistency: _spec.Consistency | None = None) -> SqlSession:
        """Open a SQL WebSocket session for interactive transactions; close it or use ``with``.
        ``consistency`` is the session's default fast or strong mode."""
        return SqlSession(self, consistency=consistency)

    def shape(self, table: str, *, where: str | None = None, columns: Sequence[str] | None = None) -> Shape:
        """Follow a table with optional SQL filtering and column projection."""
        return Shape(self._c, self.name, table, where=where, columns=columns)
