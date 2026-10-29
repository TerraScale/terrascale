"""Collection webhook registration and sanitized delivery history."""

from __future__ import annotations

import base64
import re
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any
from urllib.parse import urlsplit

from ._spec import Call, name
from .backup_config import _asend, _call, _optional_text, _send
from .ids import validate_id
from .tooling import _array, _integer, _text

if TYPE_CHECKING:
    from .aio import AsyncCollection
    from .client import Collection


@dataclass(frozen=True)
class Webhook:
    id: str
    url: str = field(repr=False)
    created_at: str
    status: str

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> Webhook:
        return cls(validate_id(b["id"], "webhook"), _text(b["url"]), _text(b["createdAt"]), _text(b["status"]))


@dataclass(frozen=True)
class CreatedWebhook(Webhook):
    secret: str = field(repr=False)

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> CreatedWebhook:
        metadata = Webhook.from_json(b)
        secret = _text(b["secret"])
        if not re.fullmatch(r"[A-Za-z0-9_-]{43}", secret):
            raise ValueError("expected a base64url 32-byte signing key")
        key = base64.urlsafe_b64decode(secret + "=")
        if len(key) != 32 or base64.urlsafe_b64encode(key).decode().rstrip("=") != secret:
            raise ValueError("expected a canonical signing key")
        return cls(metadata.id, metadata.url, metadata.created_at, metadata.status, secret)


@dataclass(frozen=True)
class WebhookDelivery:
    id: str
    created_at: str
    status: str
    attempts: int
    last_attempt_at: str | None
    next_attempt_at: str | None
    http_status: int | None
    error_code: str | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> WebhookDelivery:
        status = b["httpStatus"]
        return cls(validate_id(b["id"], "delivery"), _text(b["createdAt"]), _text(b["status"]), _integer(b["attempts"]),
                   _optional_text(b["lastAttemptAt"]), _optional_text(b["nextAttemptAt"]),
                   None if status is None else _integer(status, 32), _optional_text(b["errorCode"]))


class _Webhooks:
    def __init__(self, project: str, database: str, collection: str):
        self._path = ["v1", "projects", validate_id(project, "project"), "databases", name(database, "database"),
                      "collections", name(collection, "collection"), "webhooks"]

    def _create(self, url: str) -> Call:
        try:
            target = urlsplit(_text(url))
            if (target.scheme != "https" or not target.hostname or target.port not in (None, 443)
                    or target.username is not None or target.password is not None or target.fragment or len(url) > 2048
                    or any(ord(c) < 32 or ord(c) == 127 for c in url)):
                raise ValueError("invalid receiver")
        except ValueError:
            raise ValueError("receiver must be a public HTTPS URL on port 443 without credentials or fragment") from None
        return _call("POST", self._path, CreatedWebhook.from_json, body={"url": url}, success=201)

    def _page(self, deliveries: str | None, limit: int | None, after: str | None) -> Call:
        params = {}
        if limit is not None:
            if type(limit) is not int or not 1 <= limit <= 100:
                raise ValueError("limit must be an integer from 1 to 100")
            params["limit"] = str(limit)
        if after is not None:
            params["after"] = validate_id(after, "after")
        suffix = [] if deliveries is None else [validate_id(deliveries, "webhook"), "deliveries"]
        parse = Webhook.from_json if deliveries is None else WebhookDelivery.from_json
        return _call("GET", [*self._path, *suffix], lambda b: [parse(x) for x in _array(b)], params=params)

    def _delete(self, webhook: str) -> Call:
        return _call("DELETE", [*self._path, validate_id(webhook, "webhook")], lambda _: None, success=204)


class CollectionWebhooks(_Webhooks):
    def __init__(self, collection: Collection):
        super().__init__(*collection._p)
        self._client = collection._c

    def create_webhook(self, url: str) -> CreatedWebhook:
        return _send(self._client, self._create(url))

    def list_webhooks(self, *, limit: int | None = None, after: str | None = None) -> list[Webhook]:
        return _send(self._client, self._page(None, limit, after))

    def delete_webhook(self, webhook: str) -> None:
        _send(self._client, self._delete(webhook))

    def list_webhook_deliveries(self, webhook: str, *, limit: int | None = None,
                                after: str | None = None) -> list[WebhookDelivery]:
        return _send(self._client, self._page(webhook, limit, after))


class AsyncCollectionWebhooks(_Webhooks):
    def __init__(self, collection: AsyncCollection):
        super().__init__(*collection._p)
        self._client = collection._c

    async def create_webhook(self, url: str) -> CreatedWebhook:
        return await _asend(self._client, self._create(url))

    async def list_webhooks(self, *, limit: int | None = None, after: str | None = None) -> list[Webhook]:
        return await _asend(self._client, self._page(None, limit, after))

    async def delete_webhook(self, webhook: str) -> None:
        await _asend(self._client, self._delete(webhook))

    async def list_webhook_deliveries(self, webhook: str, *, limit: int | None = None,
                                      after: str | None = None) -> list[WebhookDelivery]:
        return await _asend(self._client, self._page(webhook, limit, after))
