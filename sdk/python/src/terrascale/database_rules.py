from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any

from ._spec import Call, database_selector
from .ids import validate_id


@dataclass(frozen=True)
class RulesValues:
    create: dict[str, str] | None = None
    update: dict[str, str] | None = None


@dataclass(frozen=True)
class RulesTable:
    read: str | None = None
    create: str | None = None
    update: str | None = None
    delete: str | None = None
    columns: list[str] | None = None
    writable: list[str] | None = None
    validate: list[str] | None = None
    set: RulesValues | None = None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> RulesTable:
        values = body.get("set")
        return cls(body.get("read"), body.get("create"), body.get("update"), body.get("delete"),
                   body.get("columns"), body.get("writable"), body.get("validate"),
                   RulesValues(**values) if values is not None else None)


@dataclass(frozen=True)
class RulesSet:
    tables: dict[str, RulesTable]


@dataclass(frozen=True)
class RulesProblem:
    table: str
    field: str
    code: str
    message: str


@dataclass(frozen=True)
class Rules:
    version: str
    tables: dict[str, RulesTable]
    updated_at: str
    updated_by: str
    problems: list[RulesProblem]
    warnings: list[RulesProblem]

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Rules:
        problems = body.get("problems")
        warnings = body.get("warnings")
        return cls(validate_id(body["version"], "rules version"),
                   {name: RulesTable.from_json(value) for name, value in body["tables"].items()},
                   body["updatedAt"], body["updatedBy"],
                   [RulesProblem(**p) for p in problems] if problems is not None else [],
                   [RulesProblem(**p) for p in warnings] if warnings is not None else [])


@dataclass(frozen=True)
class RulesVersion:
    version: str
    updated_at: str
    updated_by: str
    digest: str


@dataclass(frozen=True)
class RulesVersionPage:
    items: list[RulesVersion]
    next: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> RulesVersionPage:
        return cls([RulesVersion(validate_id(v["version"], "rules version"), v["updatedAt"],
                                 v["updatedBy"], v["digest"]) for v in body["items"]], body.get("next"))


@dataclass(frozen=True)
class RulesTestAuth:
    uid: str
    claims: dict[str, Any] | None = None
    org_id: str | None = None
    org_role: str | None = None
    is_anonymous: bool = False


@dataclass(frozen=True)
class RulesTestRequest:
    operation: str
    table: str
    auth: RulesTestAuth
    row: dict[str, Any] | None = None
    new: dict[str, Any] | None = None
    time: int | None = None
    rules: RulesSet | None = None


@dataclass(frozen=True)
class RulesTestValue:
    rule: str
    value: bool


@dataclass(frozen=True)
class RulesTestResult:
    allowed: bool
    decided_by: str
    clause: str | None
    results: list[RulesTestValue]
    columns: list[str]
    problems: list[RulesProblem]

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> RulesTestResult:
        return cls(body["allowed"], body.get("decidedBy", ""), body.get("condition", body.get("clause")),
                   [RulesTestValue(**v) for v in body.get("results", [])], body.get("columns", []),
                   [RulesProblem(**p) for p in body["problems"]])


def rules_call(project: str, database: str, operation: str, *, rules: RulesSet | None = None,
               current_version: str | None = None, version: str | None = None,
               after: str | None = None, limit: int | None = None, request: RulesTestRequest | None = None) -> Call:
    path = ["v1", "projects", validate_id(project, "project"), "databases", database_selector(database), "rules"]
    method = "GET"
    headers: dict[str, str] = {}
    params: dict[str, str] = {}
    body: Any = None
    if operation == "versions":
        path.append("versions")
        if after is not None:
            params["after"] = validate_id(after, "after")
        if limit is not None:
            if type(limit) is not int or not 1 <= limit <= 100:
                raise ValueError("limit must be an integer from 1 to 100")
            params["limit"] = str(limit)
    if operation in ("version", "restore"):
        path.extend(["versions", validate_id(version, "version")])
    if operation in ("deploy", "restore"):
        headers = {"If-None-Match": "*"} if current_version is None else {
            "If-Match": '"' + validate_id(current_version, "current_version") + '"'}
        method = "PUT" if operation == "deploy" else "POST"
        if operation == "deploy":
            if rules is None:
                raise ValueError("rules are required")
            body = asdict(rules)
        else:
            path.append("restore")
    if operation == "test":
        if request is None:
            raise ValueError("request is required")
        path[-1] = "rules:test"
        method, body = "POST", asdict(request)
        auth = body["auth"]
        auth["orgId"] = auth.pop("org_id")
        auth["orgRole"] = auth.pop("org_role")
        auth["isAnonymous"] = auth.pop("is_anonymous")
        draft = body.pop("rules")
        if draft is not None:
            body["tables"] = draft["tables"]
    return Call(method, path, lambda r: RulesVersionPage.from_json(r.json()) if operation == "versions" else (
        RulesTestResult.from_json(r.json()) if operation == "test" else Rules.from_json(r.json())),
        body=body, headers=headers, params=params, replay_safe=method == "GET")
