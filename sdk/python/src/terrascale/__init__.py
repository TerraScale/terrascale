"""TerraScale Python SDK: SQL on TerraBase, sync and async.

    from terrascale import TerraScale, new_id

    db = TerraScale().database("app")  # TERRASCALE_API_KEY, TERRASCALE_PROJECT_ID
    db.execute("CREATE TABLE IF NOT EXISTS notes (id TEXT PRIMARY KEY, body TEXT)")
    db.execute("INSERT INTO notes VALUES (?, ?)", [new_id(), "hello"])
    print(db.query("SELECT * FROM notes"))
"""

from ._version import __version__
from .aio import AsyncDatabase, AsyncTerraScale
from .auth import APIKeyTokenSource, AsyncAPIKeyTokenSource
from .client import Database, TerraScale
from .errors import (
    APIError,
    AuthenticationError,
    CommitUncertainError,
    ConflictError,
    InvalidRequestError,
    InvalidResponseError,
    KeyExpiredError,
    KeyRevokedError,
    NotFoundError,
    PermissionDeniedError,
    RateLimitError,
    ServiceUnavailableError,
    SqlError,
    SqlErrorCodes,
    TerraScaleError,
    TokenError,
    UniqueViolationError,
)
from .ids import is_valid_id, new_id, validate_id
from .models import ScopedKey
from .session import AsyncSqlSession, SqlSession
from .sql import Column, Row, Statement, StatementResult

__all__ = [
    "__version__",
    "TerraScale",
    "Database",
    "AsyncTerraScale",
    "AsyncDatabase",
    "APIKeyTokenSource",
    "AsyncAPIKeyTokenSource",
    "SqlSession",
    "AsyncSqlSession",
    "ScopedKey",
    "Statement",
    "StatementResult",
    "Column",
    "Row",
    "SqlError",
    "SqlErrorCodes",
    "new_id",
    "is_valid_id",
    "validate_id",
    "TerraScaleError",
    "TokenError",
    "APIError",
    "InvalidRequestError",
    "InvalidResponseError",
    "AuthenticationError",
    "KeyRevokedError",
    "KeyExpiredError",
    "PermissionDeniedError",
    "NotFoundError",
    "ConflictError",
    "UniqueViolationError",
    "RateLimitError",
    "ServiceUnavailableError",
    "CommitUncertainError",
]
