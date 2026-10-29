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
    "within_radius",
    "within_box",
    "asc",
    "desc",
    "near",
    "geo_near",
    "aggregate",
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
Aggregate = dict[str, Any]


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


def _degrees(name: str, value: float, bound: float) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or abs(value) > bound:
        raise ValueError(f"{name} must be a finite number of degrees in [-{bound:g}, {bound:g}]")
    return value


def within_radius(path: str, lat: float, lon: float, meters: float) -> Filter:
    """Match ``{"lat", "lon"}`` points at path at most ``meters`` (0-20000000) away (haversine, WGS 84)."""
    if isinstance(meters, bool) or not isinstance(meters, (int, float)) or not 0 <= meters <= 20_000_000:
        raise ValueError("within meters must be a number in [0, 20000000]")
    return {"within": [_path(path), {"lat": _degrees("lat", lat, 90), "lon": _degrees("lon", lon, 180), "meters": meters}]}


def within_box(path: str, south: float, west: float, north: float, east: float) -> Filter:
    """Match ``{"lat", "lon"}`` points at path inside a box; ``west > east`` crosses the antimeridian."""
    box = {"south": _degrees("south", south, 90), "west": _degrees("west", west, 180),
           "north": _degrees("north", north, 90), "east": _degrees("east", east, 180)}
    return {"within": [_path(path), box]}


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


def geo_near(path: str, lat: float, lon: float) -> SortKey:
    """Sort by meters from the point, nearest first, over ``{"lat", "lon"}`` points at path.

    Like :func:`near` it must be the only sort key, with ``limit`` <= 100 and no
    cursor; each returned document carries ``distance`` in meters.
    """
    return {"near": {"path": _path(path), "lat": _degrees("lat", lat, 90), "lon": _degrees("lon", lon, 180)}}


def aggregate(group_by: Sequence[str] = (), *, sum: Sequence[str] = (), avg: Sequence[str] = (),
              min: Sequence[str] = (), max: Sequence[str] = ()) -> Aggregate:
    """Group the matches by 0-4 paths and fold numbers at JSON Pointers (at most 16 in total).

    Pass it as ``query(filter, aggregate=...)``; the page then has ``groups``
    and no items. Aggregates take no sort, project, cursor or count, and
    ``limit`` bounds the number of groups.
    """
    if len(group_by) > 4:
        raise ValueError("aggregate group_by takes at most 4 paths")
    body: Aggregate = {}
    if group_by:
        body["group_by"] = [_path(p) for p in group_by]
    total = 0
    for name, paths in (("sum", sum), ("avg", avg), ("min", min), ("max", max)):
        if paths:
            if len(set(paths)) != len(paths) or ID_PATH in paths:
                raise ValueError(f"aggregate {name} takes distinct JSON Pointers")
            body[name] = [_path(p) for p in paths]
            total += len(paths)
    if total > 16:
        raise ValueError("aggregate takes at most 16 sum/avg/min/max paths in total")
    return body


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
