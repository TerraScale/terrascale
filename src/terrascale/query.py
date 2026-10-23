"""Query filters, sort keys and patch operations.

Paths are RFC 6901 JSON Pointers (``"/title"``) or :data:`ID_PATH` for the
document id. Filters and sort keys are plain dicts, so they can also be
written by hand::

    from terrascale import query as q
    q.and_(q.eq("/done", False), q.gte("/priority", 2))
"""

from __future__ import annotations

import math
from collections.abc import Sequence
from typing import Any

__all__ = [
    "ID_PATH",
    "and_",
    "or_",
    "not_",
    "eq",
    "ne",
    "lt",
    "lte",
    "gt",
    "gte",
    "in_",
    "nin",
    "exists",
    "prefix",
    "contains",
    "asc",
    "desc",
    "near",
    "set_",
    "unset",
    "append",
    "inc",
    "add",
    "remove",
]

ID_PATH = "$id"
"""Pseudo-path of the document id."""

Filter = dict[str, Any]
SortKey = dict[str, Any]
PatchOp = dict[str, Any]


def _path(path: str) -> str:
    if not isinstance(path, str) or not (path == ID_PATH or path.startswith("/")):
        raise ValueError(f"path {path!r} must be a JSON Pointer starting with '/' or ID_PATH")
    return path


def _scalar(value: Any) -> Any:
    if value is not None and not isinstance(value, (str, int, float, bool)):
        raise TypeError(f"filter value {value!r} is not a JSON scalar")
    if isinstance(value, float) and not math.isfinite(value):
        raise ValueError("filter value must be a finite number")
    return value


def _combine(op: str, filters: Sequence[Filter]) -> Filter:
    if not filters:
        raise ValueError(f"{op} needs at least one filter")
    return {op: list(filters)}


def and_(*filters: Filter) -> Filter:
    """Match when every child matches (1-64 children)."""
    return _combine("and", filters)


def or_(*filters: Filter) -> Filter:
    """Match when any child matches (1-64 children)."""
    return _combine("or", filters)


def not_(f: Filter) -> Filter:
    """Negate a filter."""
    return {"not": f}


def eq(path: str, value: Any) -> Filter:
    return {"eq": [_path(path), _scalar(value)]}


def ne(path: str, value: Any) -> Filter:
    return {"ne": [_path(path), _scalar(value)]}


def lt(path: str, value: Any) -> Filter:
    return {"lt": [_path(path), _scalar(value)]}


def lte(path: str, value: Any) -> Filter:
    return {"lte": [_path(path), _scalar(value)]}


def gt(path: str, value: Any) -> Filter:
    return {"gt": [_path(path), _scalar(value)]}


def gte(path: str, value: Any) -> Filter:
    return {"gte": [_path(path), _scalar(value)]}


def prefix(path: str, value: str) -> Filter:
    """Match strings at path that start with value."""
    return {"prefix": [_path(path), _scalar(value)]}


def contains(path: str, value: Any) -> Filter:
    """Match arrays at path that contain the scalar value."""
    return {"contains": [_path(path), _scalar(value)]}


def in_(path: str, values: Sequence[Any]) -> Filter:
    """Match when the value at path equals one of values (1-256)."""
    if not values:
        raise ValueError("in needs 1-256 values")
    return {"in": [_path(path), [_scalar(v) for v in values]]}


def nin(path: str, values: Sequence[Any]) -> Filter:
    """Match when the value at path equals none of values (1-256)."""
    if not values:
        raise ValueError("nin needs 1-256 values")
    return {"nin": [_path(path), [_scalar(v) for v in values]]}


def exists(path: str) -> Filter:
    """Match when the member at path is present, including null."""
    return {"exists": _path(path)}


def asc(path: str) -> SortKey:
    return {"path": _path(path), "order": "asc"}


def desc(path: str) -> SortKey:
    return {"path": _path(path), "order": "desc"}


def near(path: str, vector: Sequence[float], metric: str = "cosine") -> SortKey:
    """Sort by vector distance to the array at path, nearest first.

    A near key must be the only sort key; the query then has ``limit`` <= 100,
    no cursor, and each returned document carries ``distance``.
    """
    if not 1 <= len(vector) <= 4096:
        raise ValueError("near vector needs 1-4096 numbers")
    if any(not isinstance(v, (int, float)) or not math.isfinite(v) for v in vector):
        raise ValueError("near vector must hold finite numbers")
    if metric not in ("cosine", "l2"):
        raise ValueError(f"unknown near metric {metric!r}: use 'cosine' or 'l2'")
    if metric == "cosine" and all(v == 0 for v in vector):
        raise ValueError("cosine near vector must not be all zeros")
    return {"near": {"path": _path(path), "vector": list(vector), "metric": metric}}


def set_(path: str, value: Any) -> PatchOp:
    """Set the member at path."""
    return {"op": "set", "path": _path(path), "value": value}


def unset(path: str) -> PatchOp:
    """Remove the member at path."""
    return {"op": "unset", "path": _path(path)}


def append(path: str, value: Any) -> PatchOp:
    """Append value to the array at path, creating it when missing."""
    return {"op": "append", "path": _path(path), "value": value}


def inc(path: str, by: int) -> PatchOp:
    """Add a nonzero integer to the number at path (missing counts as 0)."""
    if isinstance(by, bool) or not isinstance(by, int) or by == 0:
        raise ValueError("inc needs a nonzero integer")
    return {"op": "inc", "path": _path(path), "by": by}


def add(path: str, value: Any) -> PatchOp:
    """Add a scalar to a top-level add-wins set."""
    return {"op": "add", "path": _path(path), "value": _scalar(value)}


def remove(path: str, value: Any) -> PatchOp:
    """Remove observed occurrences of a scalar from a top-level add-wins set."""
    return {"op": "remove", "path": _path(path), "value": _scalar(value)}
