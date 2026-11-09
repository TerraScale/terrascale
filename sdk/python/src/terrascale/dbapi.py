from __future__ import annotations

from collections.abc import Iterable, Sequence
from typing import Any

from .client import Database, TerraScale
from .errors import SqlError, SqlErrorCodes, TerraScaleError
from .sql import StatementResult

apilevel = "2.0"
threadsafety = 1
paramstyle = "qmark"
sqlite_version_info = (3, 45, 0)

STRING = 1
BINARY = 2
NUMBER = 3


class Error(Exception):
    pass


class InterfaceError(Error):
    pass


class DatabaseError(Error):
    pass


class DataError(DatabaseError):
    pass


class OperationalError(DatabaseError):
    pass


class IntegrityError(DatabaseError):
    pass


class InternalError(DatabaseError):
    pass


class ProgrammingError(DatabaseError):
    pass


class NotSupportedError(DatabaseError):
    pass


_INTEGRITY = frozenset(
    {
        SqlErrorCodes.PRIMARY_KEY_VIOLATION,
        SqlErrorCodes.UNIQUE_VIOLATION,
        SqlErrorCodes.NOT_NULL_VIOLATION,
        SqlErrorCodes.CHECK_VIOLATION,
        SqlErrorCodes.FOREIGN_KEY_VIOLATION,
        SqlErrorCodes.CONSTRAINT_VIOLATION,
    }
)
_PROGRAMMING = frozenset(
    {
        SqlErrorCodes.SYNTAX_ERROR,
        SqlErrorCodes.UNDEFINED_TABLE,
        SqlErrorCodes.UNDEFINED_COLUMN,
        SqlErrorCodes.DUPLICATE_OBJECT,
        SqlErrorCodes.INVALID_REQUEST,
        SqlErrorCodes.READ_ONLY_TRANSACTION,
        SqlErrorCodes.TRANSACTION_STATE,
    }
)
_OPERATIONAL = frozenset(
    {SqlErrorCodes.UNAVAILABLE, SqlErrorCodes.QUERY_CANCELED, SqlErrorCodes.TENANT_BUSY, SqlErrorCodes.UNAUTHORIZED}
)


def _translated(error: TerraScaleError) -> Error:
    if isinstance(error, SqlError):
        if error.code in _INTEGRITY:
            return IntegrityError(str(error))
        if error.code in _PROGRAMMING:
            return ProgrammingError(str(error))
        if error.code in _OPERATIONAL:
            return OperationalError(str(error))
        if error.code == SqlErrorCodes.TOO_LARGE:
            return DataError(str(error))
        return DatabaseError(str(error))
    return InterfaceError(str(error))


def _type_code(decltype: str | None) -> int:
    upper = (decltype or "").upper()
    if "INT" in upper:
        return NUMBER
    if "BLOB" in upper:
        return BINARY
    if "CHAR" in upper or "TEXT" in upper or "CLOB" in upper:
        return STRING
    if "REAL" in upper or "FLOA" in upper or "DOUB" in upper:
        return NUMBER
    return STRING


class Cursor:
    def __init__(self, database: Database) -> None:
        self._db = database
        self._rows: list[tuple[Any, ...]] = []
        self._pos = 0
        self.description: tuple[tuple[str, int, None, None, None, None, None], ...] | None = None
        self.rowcount = -1
        self.lastrowid: int | None = None
        self.arraysize = 1

    def _apply(self, results: Sequence[StatementResult]) -> None:
        first = results[0]
        self.description = tuple(
            (column.name, _type_code(column.decltype), None, None, None, None, None) for column in first.columns
        )
        self._rows = [tuple(row) for result in results for row in result.rows]
        self._pos = 0
        self.rowcount = sum(result.rows_affected for result in results)
        ids = [result.last_insert_rowid for result in results if result.last_insert_rowid is not None]
        self.lastrowid = ids[-1] if ids else None

    def execute(self, operation: str, parameters: Sequence[Any] | None = None) -> None:
        try:
            result = self._db.execute(operation, list(parameters) if parameters is not None else None)
        except TerraScaleError as error:
            raise _translated(error) from error
        self._apply([result])

    def executemany(self, operation: str, seq: Iterable[Sequence[Any] | None]) -> None:
        statements = [(operation, list(p) if p is not None else None) for p in seq]
        if not statements:
            self.description = None
            self._rows = []
            self._pos = 0
            self.rowcount = 0
            self.lastrowid = None
            return
        try:
            results = self._db.batch(statements)
        except TerraScaleError as error:
            raise _translated(error) from error
        self._apply(results)

    def fetchone(self) -> tuple[Any, ...] | None:
        if self._pos >= len(self._rows):
            return None
        row = self._rows[self._pos]
        self._pos += 1
        return row

    def fetchmany(self, size: int | None = None) -> list[tuple[Any, ...]]:
        count = size if size is not None else self.arraysize
        out = self._rows[self._pos : self._pos + count]
        self._pos += len(out)
        return out

    def fetchall(self) -> list[tuple[Any, ...]]:
        out = self._rows[self._pos :]
        self._pos = len(self._rows)
        return out

    def close(self) -> None:
        self._rows = []
        self._pos = 0


class Connection:
    def __init__(
        self,
        *,
        project: str | None = None,
        database: str,
        api_key: str | None = None,
        token: str | None = None,
        base_url: str | None = None,
        region: str | None = None,
        timeout: float = 30.0,
        http_client: Any = None,
    ) -> None:
        self.isolation_level: str | None = None
        self._client = TerraScale(
            project=project,
            api_key=api_key,
            token=token,
            base_url=base_url,
            region=region,
            timeout=timeout,
            http_client=http_client,
        )
        self._db = self._client.database(database)

    def cursor(self) -> Cursor:
        return Cursor(self._db)

    def commit(self) -> None:
        pass

    def rollback(self) -> None:
        pass

    def close(self) -> None:
        self._client.close()


def connect(*args: Any, **kwargs: Any) -> Connection:
    return Connection(*args, **kwargs)
