from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any

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
        return cls(**{key: body[key] for key in cls.__dataclass_fields__})


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
        return cls(**{key: body[key] for key in cls.__dataclass_fields__})


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
        return cls(tuple(Organization.from_json(item) for item in body["items"]), body["next"])


@dataclass(frozen=True)
class OrganizationMembershipPage:
    """Project organization_membership page."""

    items: tuple[OrganizationMembership, ...]
    next: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> OrganizationMembershipPage:
        return cls(tuple(OrganizationMembership.from_json(item) for item in body["items"]), body["next"])


@dataclass(frozen=True)
class OrganizationInvitationPage:
    """Project organization_invitation page."""

    items: tuple[OrganizationInvitation, ...]
    next: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> OrganizationInvitationPage:
        return cls(tuple(OrganizationInvitation.from_json(item) for item in body["items"]), body["next"])


@dataclass(frozen=True)
class OrganizationDomainPage:
    """Project organization_domain page."""

    items: tuple[OrganizationDomain, ...]

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> OrganizationDomainPage:
        return cls(tuple(OrganizationDomain.from_json(item) for item in body["items"]))


def create_organization_call(issuer: str | None, project: str, request: OrganizationRequest) -> _spec.Call:
    return _spec.Call(
        "POST",
        _tenant_path(project, "organizations"),
        lambda response: Organization.from_json(response.json()),
        body=asdict(request),
        base_url=_origin(issuer),
        success=(201,),
    )


def list_organizations_call(issuer: str | None, project: str, limit: int | None, after: str | None) -> _spec.Call:
    return _spec.Call(
        "GET",
        _tenant_path(project, "organizations"),
        lambda response: OrganizationPage.from_json(response.json()),
        params=_spec._page_params(_page_limit(limit), _cursor(after)),
        base_url=_origin(issuer),
        success=(200,),
        replay_safe=True,
    )


def get_organization_call(issuer: str | None, project: str, org: str) -> _spec.Call:
    return _spec.Call(
        "GET",
        _tenant_path(project, "organizations", org),
        lambda response: Organization.from_json(response.json()),
        base_url=_origin(issuer),
        success=(200,),
        replay_safe=True,
    )


def update_organization_call(issuer: str | None, project: str, org: str, request: OrganizationRequest) -> _spec.Call:
    return _spec.Call(
        "PUT",
        _tenant_path(project, "organizations", org),
        lambda response: Organization.from_json(response.json()),
        body=asdict(request),
        base_url=_origin(issuer),
        success=(200,),
    )


def delete_organization_call(issuer: str | None, project: str, org: str) -> _spec.Call:
    return _spec.Call(
        "DELETE", _tenant_path(project, "organizations", org), _spec._none, base_url=_origin(issuer), success=(204,)
    )


def add_organization_member_call(
    issuer: str | None, project: str, org: str, request: OrganizationMembershipRequest
) -> _spec.Call:
    return _spec.Call(
        "POST",
        _tenant_path(project, "organizations", org, "memberships"),
        lambda response: OrganizationMembership.from_json(response.json()),
        body=asdict(request),
        base_url=_origin(issuer),
        success=(201,),
    )


def list_organization_members_call(
    issuer: str | None, project: str, org: str, limit: int | None, after: str | None
) -> _spec.Call:
    return _spec.Call(
        "GET",
        _tenant_path(project, "organizations", org, "memberships"),
        lambda response: OrganizationMembershipPage.from_json(response.json()),
        params=_spec._page_params(_page_limit(limit), _cursor(after)),
        base_url=_origin(issuer),
        success=(200,),
        replay_safe=True,
    )


def update_organization_member_call(
    issuer: str | None, project: str, org: str, user: str, request: OrganizationMembershipRequest
) -> _spec.Call:
    return _spec.Call(
        "PUT",
        _tenant_path(project, "organizations", org, "memberships", user),
        lambda response: OrganizationMembership.from_json(response.json()),
        body=asdict(request),
        base_url=_origin(issuer),
        success=(200,),
    )


def delete_organization_member_call(issuer: str | None, project: str, org: str, user: str) -> _spec.Call:
    return _spec.Call(
        "DELETE",
        _tenant_path(project, "organizations", org, "memberships", user),
        _spec._none,
        base_url=_origin(issuer),
        success=(204,),
    )


def create_organization_invitation_call(
    issuer: str | None, project: str, org: str, request: OrganizationInvitationRequest
) -> _spec.Call:
    return _spec.Call(
        "POST",
        _tenant_path(project, "organizations", org, "invitations"),
        lambda response: OrganizationInvitation.from_json(response.json()),
        body=asdict(request),
        base_url=_origin(issuer),
        success=(201,),
    )


def list_organization_invitations_call(
    issuer: str | None, project: str, org: str, limit: int | None, after: str | None
) -> _spec.Call:
    return _spec.Call(
        "GET",
        _tenant_path(project, "organizations", org, "invitations"),
        lambda response: OrganizationInvitationPage.from_json(response.json()),
        params=_spec._page_params(_page_limit(limit), _cursor(after)),
        base_url=_origin(issuer),
        success=(200,),
        replay_safe=True,
    )


def revoke_organization_invitation_call(issuer: str | None, project: str, org: str, invitation: str) -> _spec.Call:
    return _spec.Call(
        "DELETE",
        _tenant_path(project, "organizations", org, "invitations", invitation),
        _spec._none,
        base_url=_origin(issuer),
        success=(204,),
    )


def add_organization_domain_call(issuer: str | None, project: str, org: str, request: OrganizationDomainRequest) -> _spec.Call:
    return _spec.Call(
        "POST",
        _tenant_path(project, "organizations", org, "domains"),
        lambda response: OrganizationDomain.from_json(response.json()),
        body=asdict(request),
        base_url=_origin(issuer),
        success=(201,),
    )


def list_organization_domains_call(issuer: str | None, project: str, org: str) -> _spec.Call:
    return _spec.Call(
        "GET",
        _tenant_path(project, "organizations", org, "domains"),
        lambda response: OrganizationDomainPage.from_json(response.json()),
        base_url=_origin(issuer),
        success=(200,),
        replay_safe=True,
    )


def verify_organization_domain_call(issuer: str | None, project: str, org: str, domain: str) -> _spec.Call:
    return _spec.Call(
        "POST",
        _tenant_path(project, "organizations", org, "domains", domain, "verify"),
        lambda response: OrganizationDomain.from_json(response.json()),
        base_url=_origin(issuer),
        success=(200,),
    )


def delete_organization_domain_call(issuer: str | None, project: str, org: str, domain: str) -> _spec.Call:
    return _spec.Call(
        "DELETE",
        _tenant_path(project, "organizations", org, "domains", domain),
        _spec._none,
        base_url=_origin(issuer),
        success=(204,),
    )
