"""TerraScale Python SDK: SQL on TerraBase, sync and async.

    from terrascale import TerraScale, new_id

    db = TerraScale().database("app")  # TERRASCALE_API_KEY, TERRASCALE_PROJECT_ID
    db.execute("CREATE TABLE IF NOT EXISTS notes (id TEXT PRIMARY KEY, body TEXT)")
    db.execute("INSERT INTO notes VALUES (?, ?)", [new_id(), "hello"])
    print(db.query("SELECT * FROM notes"))
"""

from . import query
from ._version import __version__
from .aio import AsyncCollection, AsyncDatabase, AsyncTerraScale, AsyncTransaction
from .auth import APIKeyTokenSource, AsyncAPIKeyTokenSource
from .client import Collection, Database, TerraScale, Transaction
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
    PreconditionFailedError,
    RateLimitError,
    ServiceUnavailableError,
    SqlError,
    SqlErrorCodes,
    TerraScaleError,
    TokenError,
    UniqueViolationError,
)
from .ids import is_valid_id, new_id, validate_id
from .models import Document, Group, Page, SQLResult, TransactionResult, WriteResult
from .session import AsyncSqlSession, SqlSession
from .sql import Column, Row, Statement, StatementResult

__all__ = [
    "__version__",
    "query",
    "TerraScale",
    "Database",
    "Collection",
    "Transaction",
    "AsyncTerraScale",
    "AsyncDatabase",
    "AsyncCollection",
    "AsyncTransaction",
    "APIKeyTokenSource",
    "AsyncAPIKeyTokenSource",
    "Document",
    "Page",
    "Group",
    "WriteResult",
    "SQLResult",
    "SqlSession",
    "AsyncSqlSession",
    "Statement",
    "StatementResult",
    "Column",
    "Row",
    "SqlError",
    "SqlErrorCodes",
    "TransactionResult",
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
    "PreconditionFailedError",
    "RateLimitError",
    "ServiceUnavailableError",
    "CommitUncertainError",
]
