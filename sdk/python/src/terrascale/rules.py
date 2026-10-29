"""End-user permission grants shared by collection and bucket policies."""

from __future__ import annotations

from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any

import httpx

from ._spec import Call, name
from .errors import APIError, CommitUncertainError, response_request_id
from .ids import validate_id

if TYPE_CHECKING:
    from .aio import AsyncCollection
    from .client import Collection


@dataclass(frozen=True)
class OperationGrants:
    """Independent grants; public applies only to authenticated end users."""

    owner: bool = False
    public: bool = False
    roles: list[str] | None = field(default_factory=list)

    def body(self) -> dict[str, Any]:
        if type(self.owner) is not bool or type(self.public) is not bool:
            raise ValueError("owner and public grants must be booleans")
        if self.roles is not None and (not isinstance(self.roles, list) or any(not isinstance(r, str) for r in self.roles)):
            raise ValueError("roles must be an array of strings or null")
        return {"owner": self.owner, "public": self.public, "roles": None if self.roles is None else list(self.roles)}

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> OperationGrants:
        grant = cls(b["owner"], b["public"], b["roles"])
        grant.body()
        return grant


@dataclass(frozen=True)
class CollectionRules:
    owner: str | None = None
    read: OperationGrants | None = field(default_factory=OperationGrants)
    write: OperationGrants | None = field(default_factory=OperationGrants)

    def body(self) -> dict[str, Any]:
        if self.owner is not None and not isinstance(self.owner, str):
            raise ValueError("owner must be a JSON Pointer string or null")
        if not isinstance(self.read, OperationGrants) or not isinstance(self.write, OperationGrants):
            raise ValueError("read and write grants are required when replacing rules")
        return {"owner": self.owner, "read": self.read.body(), "write": self.write.body()}

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> CollectionRules:
        owner, read, write = b["owner"], b["read"], b["write"]
        if owner is not None and not isinstance(owner, str):
            raise ValueError("invalid owner pointer")
        return cls(owner, OperationGrants.from_json(read) if read is not None else None,
                   OperationGrants.from_json(write) if write is not None else None)


class _PolicyCall(Call):
    def decode(self, response: httpx.Response) -> Any:
        return self.parse(response)


def _policy_call(method: str, path: Sequence[str], parse: Callable[[httpx.Response], Any], success: int, *,
                 body: Any = None, params: dict[str, str] | None = None) -> Call:
    def decode(r: httpx.Response) -> Any:
        try:
            if r.status_code != success:
                raise ValueError("unexpected success status")
            if success == 204:
                if r.content:
                    raise ValueError("unexpected response body")
                return None
            return parse(r)
        except (ValueError, KeyError, TypeError, AttributeError, IndexError, OverflowError):
            error = APIError if method == "GET" else CommitUncertainError
            raise error(r.status_code, "invalid_response", "invalid policy response; reconcile mutations before retrying",
                        request_id=response_request_id(r.headers)) from None
    return _PolicyCall(method, path, decode, body=body, params=params or {}, replay_safe=method == "GET")


def _policy_failure(call: Call, error: APIError) -> APIError:
    if not call.replay_safe and error.status >= 500 and not isinstance(error, CommitUncertainError):
        return CommitUncertainError(error.status, error.code, "policy mutation may have committed; read before retrying",
                                    request_id=error.request_id)
    return error


class _Requests:
    def __init__(self, project: str, database: str, collection: str):
        self._path = ["v1", "projects", validate_id(project, "project"), "databases", name(database, "database"),
                      "collections", name(collection, "collection"), "rules"]

    def _call(self, method: str, rules: CollectionRules | None = None) -> Call:
        if method == "PUT" and not isinstance(rules, CollectionRules):
            raise ValueError("a complete rules policy is required")
        def parse(r: httpx.Response) -> CollectionRules | None:
            policy = r.json()["rules"]
            return CollectionRules.from_json(policy) if policy is not None else None
        return _policy_call(method, self._path, parse, 200, body=rules.body() if rules is not None else None)


class CollectionPermissions(_Requests):
    def __init__(self, collection: Collection):
        super().__init__(*collection._p)
        self._client = collection._c

    def _send(self, call: Call) -> CollectionRules | None:
        try:
            return self._client._send(call)
        except APIError as error:
            raise _policy_failure(call, error) from None

    def get_rules(self) -> CollectionRules | None:
        return self._send(self._call("GET"))

    def put_rules(self, rules: CollectionRules) -> CollectionRules | None:
        return self._send(self._call("PUT", rules))


class AsyncCollectionPermissions(_Requests):
    def __init__(self, collection: AsyncCollection):
        super().__init__(*collection._p)
        self._client = collection._c

    async def _send(self, call: Call) -> CollectionRules | None:
        try:
            return await self._client._send(call)
        except APIError as error:
            raise _policy_failure(call, error) from None

    async def get_rules(self) -> CollectionRules | None:
        return await self._send(self._call("GET"))

    async def put_rules(self, rules: CollectionRules) -> CollectionRules | None:
        return await self._send(self._call("PUT", rules))
