"""TerraScale Python SDK: the TerraBase document database, sync and async.

    from terrascale import TerraScale, query as q

    with TerraScale() as ts:  # TERRASCALE_API_KEY, TERRASCALE_PROJECT_ID
        todos = ts.database("main").collection("todos")
        res = todos.create({"title": "Ship it", "done": False})
        for doc in todos.items(q.eq("/done", False)):
            print(doc.id, doc.data)
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
    TerraScaleError,
    TokenError,
    UniqueViolationError,
)
from .ids import is_valid_id, new_id, validate_id
from .models import Document, Group, Page, SQLColumn, SQLResult, SQLStatementResult, TransactionResult, WriteResult

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
    "SQLColumn",
    "SQLStatementResult",
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
