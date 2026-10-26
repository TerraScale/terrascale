"""Project file-bucket metadata; transfers are a separate lifecycle."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any

from ._spec import Call, name
from .errors import APIError
from .ids import validate_id
from .rules import CollectionRules, _policy_call, _policy_failure

if TYPE_CHECKING:
    from .aio import AsyncTerraScale
    from .client import TerraScale


@dataclass(frozen=True)
class FileBucket:
    id: str
    name: str
    region: str
    rules: CollectionRules
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> FileBucket:
        if not isinstance(b["region"], str):
            raise ValueError("bucket region must be a string")
        return cls(validate_id(b["id"], "bucket"), name(b["name"], "bucket"), b["region"],
                   CollectionRules.from_json(b["rules"]), b)


@dataclass(frozen=True)
class FileBucketPage:
    items: list[FileBucket]
    next_cursor: str | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> FileBucketPage:
        if not isinstance(b["items"], list):
            raise ValueError("bucket items must be an array")
        cursor = b["next"]
        return cls([FileBucket.from_json(x) for x in b["items"]],
                   validate_id(cursor, "bucket cursor") if cursor is not None else None)


class _Requests:
    def __init__(self, project: str):
        self._path = ["v1", "projects", validate_id(project, "project"), "buckets"]

    def _list(self, after: str | None) -> Call:
        return _policy_call("GET", self._path, lambda r: FileBucketPage.from_json(r.json()), 200,
                            params={} if after is None else {"after": validate_id(after, "bucket cursor")})

    def _create(self, bucket_name: str, rules: CollectionRules | None) -> Call:
        policy = CollectionRules() if rules is None else rules
        if not isinstance(policy, CollectionRules):
            raise ValueError("bucket rules must be a complete policy")
        return _policy_call("POST", self._path, lambda r: FileBucket.from_json(r.json()), 201,
                            body={"name": name(bucket_name, "bucket"), "rules": policy.body()})

    def _replace(self, bucket: str, rules: CollectionRules) -> Call:
        if not isinstance(rules, CollectionRules):
            raise ValueError("bucket rules must be a complete policy")
        return _policy_call("PUT", [*self._path, validate_id(bucket, "bucket"), "rules"],
                            lambda r: FileBucket.from_json(r.json()), 200, body={"rules": rules.body()})

    def _delete(self, bucket: str) -> Call:
        return _policy_call("DELETE", [*self._path, validate_id(bucket, "bucket")], lambda _: None, 204)


class Storage(_Requests):
    """Manage buckets using an existing synchronous project client."""

    def __init__(self, client: TerraScale):
        super().__init__(client.project)
        self._client = client

    def _send(self, call: Call) -> Any:
        try:
            return self._client._send(call)
        except APIError as error:
            raise _policy_failure(call, error) from None

    def list_buckets(self, *, after: str | None = None) -> FileBucketPage:
        return self._send(self._list(after))

    def create_bucket(self, name: str, rules: CollectionRules | None = None) -> FileBucket:
        return self._send(self._create(name, rules))

    def set_bucket_rules(self, bucket: str, rules: CollectionRules) -> FileBucket:
        return self._send(self._replace(bucket, rules))

    def delete_bucket(self, bucket: str) -> None:
        self._send(self._delete(bucket))


class AsyncStorage(_Requests):
    """The asynchronous twin of Storage, sharing its DTOs and wire builders."""

    def __init__(self, client: AsyncTerraScale):
        super().__init__(client.project)
        self._client = client

    async def _send(self, call: Call) -> Any:
        try:
            return await self._client._send(call)
        except APIError as error:
            raise _policy_failure(call, error) from None

    async def list_buckets(self, *, after: str | None = None) -> FileBucketPage:
        return await self._send(self._list(after))

    async def create_bucket(self, name: str, rules: CollectionRules | None = None) -> FileBucket:
        return await self._send(self._create(name, rules))

    async def set_bucket_rules(self, bucket: str, rules: CollectionRules) -> FileBucket:
        return await self._send(self._replace(bucket, rules))

    async def delete_bucket(self, bucket: str) -> None:
        await self._send(self._delete(bucket))
