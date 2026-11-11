"""SQL values, statements, rows and results shared by the sync and async clients."""

from __future__ import annotations

import base64
import math
from collections.abc import Callable, Iterator, Mapping, Sequence
from dataclasses import dataclass
from typing import Any, TypeVar, overload

__all__ = ["MAX_STATEMENTS", "Column", "Row", "Statement", "StatementResult"]

MAX_STATEMENTS = 256
_INT64 = range(-(1 << 63), 1 << 63)

T = TypeVar("T")
Args = Sequence[Any] | Mapping[str, Any] | None


@dataclass(frozen=True)
class Statement:
    """One SQL statement with positional (``?``) or named (``:name``) arguments.
    Named keys omit their ``:``, ``@`` or ``$`` prefix."""

    sql: str
    args: Args = None

    def to_json(self) -> dict[str, Any]:
        if not isinstance(self.sql, str) or not self.sql.strip():
            raise ValueError("SQL statements must be non-empty text")
        body: dict[str, Any] = {"sql": self.sql}
        if isinstance(self.args, Mapping):
            for key in self.args:
                if not isinstance(key, str) or not key or key[0] in ":@$":
                    raise ValueError(f"named argument {key!r} must be non-empty and written without its :, @ or $ prefix")
            body["named"] = {k: encode(v) for k, v in self.args.items()}
        elif isinstance(self.args, (str, bytes)):
            raise TypeError("SQL arguments must be a sequence or a mapping")
        elif self.args is not None:
            body["args"] = [encode(v) for v in self.args]
        return body


def encode(value: Any) -> Any:
    if value is None or isinstance(value, (bool, str)):
        return value
    if isinstance(value, int):
        if value not in _INT64:
            raise ValueError("SQL integers must fit in 64 bits")
        return value
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ValueError("SQL arguments must be finite numbers")
        return value
    if isinstance(value, (bytes, bytearray, memoryview)):
        return {"base64": base64.b64encode(bytes(value)).decode()}
    raise TypeError(f"unsupported SQL argument type {type(value).__name__}")


def statement(sql: str | Statement, args: Args) -> Statement:
    if not isinstance(sql, Statement):
        return Statement(sql, args)
    if args is not None:
        raise TypeError("a Statement carries its own arguments: pass Statement(sql, args) alone, or execute(sql, args)")
    return sql


def statements(items: Sequence[str | tuple[str, Args] | Statement]) -> list[dict[str, Any]]:
    if isinstance(items, (str, bytes)) or not 1 <= len(items) <= MAX_STATEMENTS:
        raise ValueError(f"a SQL request holds 1 to {MAX_STATEMENTS} statements")
    out = []
    for item in items:
        if isinstance(item, str):
            item = Statement(item)
        elif isinstance(item, tuple):
            item = Statement(*item)
        if not isinstance(item, Statement):
            raise TypeError("statements are SQL text, (sql, args) tuples or Statement")
        out.append(item.to_json())
    return out


def _decode(cell: Any) -> Any:
    if cell is None or isinstance(cell, str) or type(cell) in (int, float):
        return cell
    if isinstance(cell, dict) and cell.keys() == {"base64"} and isinstance(cell["base64"], str):
        return base64.b64decode(cell["base64"], validate=True)
    raise ValueError("invalid SQL value")


def _integer(value: Any) -> int:
    if type(value) is not int:
        raise ValueError("expected an integer")
    return value


@dataclass(frozen=True)
class Column:
    """A result column; ``decltype`` is None for an expression."""

    name: str
    decltype: str | None = None


class Row(Sequence[Any]):
    """One result row, read by ordinal or column name. ``dict(row)`` and
    ``Model(**row)`` key it by column name; a repeated name keeps its first
    value. Blobs are ``bytes``; integers are exact."""

    __slots__ = ("_ordinals", "_values", "columns")

    def __init__(self, columns: list[Column], ordinals: dict[str, int], values: list[Any]):
        self.columns = columns
        self._ordinals = ordinals
        self._values = values

    def __len__(self) -> int:
        return len(self._values)

    @overload
    def __getitem__(self, key: int | str) -> Any: ...

    @overload
    def __getitem__(self, key: slice) -> list[Any]: ...

    def __getitem__(self, key: int | str | slice) -> Any:
        return self._values[self.ordinal(key) if isinstance(key, str) else key]

    def __iter__(self) -> Iterator[Any]:
        return iter(self._values)

    def __eq__(self, other: object) -> bool:
        if isinstance(other, (Row, list, tuple)):
            return list(self) == list(other)
        return NotImplemented

    def __repr__(self) -> str:
        return f"Row({self.as_dict()!r})"

    def keys(self) -> list[str]:
        return list(self._ordinals)

    def as_dict(self) -> dict[str, Any]:
        return {name: self._values[i] for name, i in self._ordinals.items()}

    def ordinal(self, name: str) -> int:
        try:
            return self._ordinals[name]
        except KeyError:
            raise KeyError(f"the result has no column {name!r}") from None

    def is_null(self, key: int | str) -> bool:
        return self[key] is None

    def get_int64(self, key: int | str) -> int:
        value = self[key]
        if type(value) is not int:
            raise TypeError(f"column {key!r} is not an integer")
        return value

    def get_double(self, key: int | str) -> float:
        value = self[key]
        if type(value) not in (int, float):
            raise TypeError(f"column {key!r} is not a number")
        return float(value)

    def get_boolean(self, key: int | str) -> bool:
        return self.get_int64(key) != 0

    def get_string(self, key: int | str) -> str:
        value = self[key]
        if not isinstance(value, str):
            raise TypeError(f"column {key!r} is not text")
        return value

    def get_bytes(self, key: int | str) -> bytes:
        value = self[key]
        if not isinstance(value, bytes):
            raise TypeError(f"column {key!r} is not a blob")
        return value


@dataclass(frozen=True)
class StatementResult:
    """One statement's result: ``rows`` in result order, ``rows_affected`` by a
    write and the integer key of the last inserted row, when there is one."""

    columns: list[Column]
    rows: list[Row]
    rows_affected: int
    last_insert_rowid: int | None

    def as_(self, mapper: Callable[..., T]) -> list[T]:
        """Each row as ``mapper(**row)``: a dataclass, TypedDict, Pydantic model or function."""
        return [mapper(**row) for row in self.rows]

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> StatementResult:
        raw_columns, raw_rows = body["columns"], body["rows"]
        if not isinstance(raw_columns, list) or not isinstance(raw_rows, list):
            raise ValueError("expected SQL columns and rows arrays")
        columns = []
        for c in raw_columns:
            name, decltype = c["name"], c.get("decltype")
            if not isinstance(name, str) or decltype is not None and not isinstance(decltype, str):
                raise ValueError("invalid SQL column")
            columns.append(Column(name, decltype))
        ordinals: dict[str, int] = {}
        for i, column in enumerate(columns):
            ordinals.setdefault(column.name, i)
        rows = []
        for raw in raw_rows:
            if not isinstance(raw, list) or len(raw) != len(columns):
                raise ValueError("invalid SQL row")
            rows.append(Row(columns, ordinals, [_decode(cell) for cell in raw]))
        affected = _integer(body["rows_affected"])
        if affected < 0:
            raise ValueError("negative rows affected")
        rowid = body.get("last_insert_rowid")
        return cls(columns, rows, affected, _integer(rowid) if rowid is not None else None)


def results(body: Any, expected: int) -> list[StatementResult]:
    items = body.get("results") if isinstance(body, dict) else None
    if not isinstance(items, list) or len(items) != expected:
        raise ValueError("result count does not match the statements")
    return [StatementResult.from_json(item) for item in items]
