from __future__ import annotations

from collections.abc import Callable
from dataclasses import asdict, dataclass
from typing import Any

import httpx

from . import _spec
from .auth import _cursor, _origin, _page_limit, _tenant_path


@dataclass(frozen=True)
class Organization:
    """Project organization."""

    id: str
    name: str
    slug: str
    image_url: str | None
    public_metadata: dict[str, Any]
    private_metadata: dict[str, Any]
    max_members: int | None
    domain_join: str
    created_at: str

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Organization:
        return cls(body["id"], body["name"], body["slug"], body.get("image_url"), body["public_metadata"],
                   body["private_metadata"], body.get("max_members"), body["domain_join"], body["created_at"])


@dataclass(frozen=True)
class OrganizationRequest:
    """Project organization request."""

    name: str
    slug: str
    admin_user_id: str | None
    image_url: str | None
    public_metadata: dict[str, Any] | None
    private_metadata: dict[str, Any] | None
    max_members: int | None
    domain_join: str


@dataclass(frozen=True)
class OrganizationMembership:
    """Project organization membership."""

    project_id: str
    organization_id: str
    user_id: str
    role: str
    created_at: str

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> OrganizationMembership:
        return cls(**{key: body[key] for key in cls.__dataclass_fields__})


@dataclass(frozen=True)
class OrganizationMembershipRequest:
    """Project organization membership request."""

    user_id: str | None
    role: str


@dataclass(frozen=True)
class OrganizationInvitation:
    """Project organization invitation."""

    id: str
    email: str
    role: str
    status: str
    created_at: str
    expires_at: str

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> OrganizationInvitation:
        return cls(**{key: body[key] for key in cls.__dataclass_fields__})


@dataclass(frozen=True)
class OrganizationInvitationRequest:
    """Project organization invitation request."""

    email: str
    role: str
    inviter_name: str | None = None


@dataclass(frozen=True)
class OrganizationDomain:
    """Project organization domain."""

    domain: str
    record_name: str
    record_value: str
    verified_at: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> OrganizationDomain:
        return cls(body["domain"], body["record_name"], body["record_value"], body.get("verified_at"))


@dataclass(frozen=True)
class OrganizationDomainRequest:
    """Project organization domain request."""

    domain: str


@dataclass(frozen=True)
class OrganizationPage:
    """Project organization page."""

    items: tuple[Organization, ...]
    next: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> OrganizationPage:
        return cls(tuple(Organization.from_json(item) for item in body["items"]), body.get("next"))


@dataclass(frozen=True)
class OrganizationMembershipPage:
    """Project organization_membership page."""

    items: tuple[OrganizationMembership, ...]
    next: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> OrganizationMembershipPage:
        return cls(tuple(OrganizationMembership.from_json(item) for item in body["items"]), body.get("next"))


@dataclass(frozen=True)
class OrganizationInvitationPage:
    """Project organization_invitation page."""

    items: tuple[OrganizationInvitation, ...]
    next: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> OrganizationInvitationPage:
        return cls(tuple(OrganizationInvitation.from_json(item) for item in body["items"]), body.get("next"))


@dataclass(frozen=True)
class OrganizationDomainPage:
    """Project organization_domain page."""

    items: tuple[OrganizationDomain, ...]

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> OrganizationDomainPage:
        return cls(tuple(OrganizationDomain.from_json(item) for item in body["items"]))


def _call(method: str, path: list[str], parse: Callable[[httpx.Response], Any], issuer: str | None, *,
          body: Any = None, params: dict[str, str] | None = None, success: tuple[int, ...] = (200,),
          replay_safe: bool = False) -> _spec.Call:
    return _spec.Call(method, path, parse, body=body, params=params or {}, base_url=_origin(issuer),
                      success=success, replay_safe=replay_safe)


def _json(parse: Callable[[dict[str, Any]], Any]) -> Callable[[httpx.Response], Any]:
    return lambda response: parse(response.json())


def _page(limit: int | None, after: str | None) -> dict[str, str]:
    return _spec._page_params(_page_limit(limit), _cursor(after))


def create_organization_call(issuer: str | None, project: str, request: OrganizationRequest) -> _spec.Call:
    return _call("POST", _tenant_path(project, "organizations"), _json(Organization.from_json), issuer,
                 body=asdict(request), success=(201,))


def list_organizations_call(issuer: str | None, project: str, limit: int | None, after: str | None) -> _spec.Call:
    return _call("GET", _tenant_path(project, "organizations"), _json(OrganizationPage.from_json), issuer,
                 params=_page(limit, after), replay_safe=True)


def get_organization_call(issuer: str | None, project: str, org: str) -> _spec.Call:
    return _call("GET", _tenant_path(project, "organizations", org), _json(Organization.from_json), issuer,
                 replay_safe=True)


def update_organization_call(issuer: str | None, project: str, org: str, request: OrganizationRequest) -> _spec.Call:
    return _call("PUT", _tenant_path(project, "organizations", org), _json(Organization.from_json), issuer,
                 body=asdict(request))


def delete_organization_call(issuer: str | None, project: str, org: str) -> _spec.Call:
    return _call("DELETE", _tenant_path(project, "organizations", org), _spec._none, issuer, success=(204,))


def add_organization_member_call(issuer: str | None, project: str, org: str,
                                 request: OrganizationMembershipRequest) -> _spec.Call:
    return _call("POST", _tenant_path(project, "organizations", org, "memberships"),
                 _json(OrganizationMembership.from_json), issuer, body=asdict(request), success=(201,))


def list_organization_members_call(issuer: str | None, project: str, org: str, limit: int | None,
                                   after: str | None) -> _spec.Call:
    return _call("GET", _tenant_path(project, "organizations", org, "memberships"),
                 _json(OrganizationMembershipPage.from_json), issuer, params=_page(limit, after), replay_safe=True)


def update_organization_member_call(issuer: str | None, project: str, org: str, user: str,
                                    request: OrganizationMembershipRequest) -> _spec.Call:
    return _call("PUT", _tenant_path(project, "organizations", org, "memberships", user),
                 _json(OrganizationMembership.from_json), issuer, body=asdict(request))


def delete_organization_member_call(issuer: str | None, project: str, org: str, user: str) -> _spec.Call:
    return _call("DELETE", _tenant_path(project, "organizations", org, "memberships", user), _spec._none, issuer,
                 success=(204,))


def create_organization_invitation_call(issuer: str | None, project: str, org: str,
                                        request: OrganizationInvitationRequest) -> _spec.Call:
    return _call("POST", _tenant_path(project, "organizations", org, "invitations"),
                 _json(OrganizationInvitation.from_json), issuer, body=asdict(request), success=(201,))


def list_organization_invitations_call(issuer: str | None, project: str, org: str, limit: int | None,
                                       after: str | None) -> _spec.Call:
    return _call("GET", _tenant_path(project, "organizations", org, "invitations"),
                 _json(OrganizationInvitationPage.from_json), issuer, params=_page(limit, after), replay_safe=True)


def revoke_organization_invitation_call(issuer: str | None, project: str, org: str, invitation: str) -> _spec.Call:
    return _call("DELETE", _tenant_path(project, "organizations", org, "invitations", invitation), _spec._none,
                 issuer, success=(204,))


def add_organization_domain_call(issuer: str | None, project: str, org: str,
                                 request: OrganizationDomainRequest) -> _spec.Call:
    return _call("POST", _tenant_path(project, "organizations", org, "domains"), _json(OrganizationDomain.from_json),
                 issuer, body=asdict(request), success=(201,))


def list_organization_domains_call(issuer: str | None, project: str, org: str) -> _spec.Call:
    return _call("GET", _tenant_path(project, "organizations", org, "domains"), _json(OrganizationDomainPage.from_json),
                 issuer, replay_safe=True)


def verify_organization_domain_call(issuer: str | None, project: str, org: str, domain: str) -> _spec.Call:
    return _call("POST", _tenant_path(project, "organizations", org, "domains", domain, "verify"),
                 _json(OrganizationDomain.from_json), issuer)


def delete_organization_domain_call(issuer: str | None, project: str, org: str, domain: str) -> _spec.Call:
    return _call("DELETE", _tenant_path(project, "organizations", org, "domains", domain), _spec._none, issuer,
                 success=(204,))
