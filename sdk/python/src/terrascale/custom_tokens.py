from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from typing import Any

from . import _spec
from .ids import validate_id

ClaimValue = str | int | float | bool | Sequence[str] | Sequence[int | float]


@dataclass(frozen=True)
class CustomToken:
    access_token: str = field(repr=False)
    token_type: str
    expires_in: int

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> "CustomToken":
        token, kind, expiry = body["access_token"], body["token_type"], body["expires_in"]
        if not isinstance(token, str) or not token or kind != "Bearer" or type(expiry) is not int or not 1 <= expiry <= 300:
            raise ValueError("Invalid custom token response")
        return cls(token, kind, expiry)


def mint_custom_token_call(api_key: str | None, issuer: str | None, project: str, user_id: str,
                           database: str | None, claims: Mapping[str, ClaimValue] | None) -> _spec.Call:
    if not api_key:
        raise ValueError("mint_custom_token requires the client's project api_key")
    body: dict[str, Any] = {"user_id": validate_id(user_id, "user_id")}
    if database is not None:
        body["database"] = validate_id(database, "database")
    if claims is not None:
        body["claims"] = dict(claims)
    return _spec.Call("POST", ["v1", validate_id(project, "project"), "custom-tokens"],
                      lambda r: CustomToken.from_json(r.json()), body=body,
                      headers={"Authorization": f"Bearer {api_key}"}, base_url=_spec.issuer_parts(issuer)[0])
