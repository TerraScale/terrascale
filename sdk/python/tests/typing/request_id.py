"""Public consumer typing, checked by the CI mypy invocation."""

from terrascale import APIError, NotFoundError, TokenError


def support_id(error: APIError | TokenError) -> str | None:
    return error.request_id


def missing_document_id(error: NotFoundError) -> str | None:
    return error.request_id
