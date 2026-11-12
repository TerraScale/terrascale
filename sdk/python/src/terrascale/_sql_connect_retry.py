from __future__ import annotations

import time
from datetime import timezone
from email.utils import parsedate_to_datetime

from .errors import APIError

MAX_ATTEMPTS = 3
DELAY_CAP = 30.0


class ConnectRetry:
    def __init__(self, timeout: float | None) -> None:
        self.deadline = None if timeout is None else time.monotonic() + timeout
        self.attempt = 1

    def remaining(self) -> float | None:
        return None if self.deadline is None else max(0.0, self.deadline - time.monotonic())

    def delay(self, refusal: APIError, header: str | None) -> float | None:
        if refusal.status not in {429, 503} or header is None:
            return None
        value = header.strip()
        if value.isascii() and value.isdecimal():
            seconds = float(value)
        else:
            try:
                at = parsedate_to_datetime(value)
                if at.tzinfo is None:
                    at = at.replace(tzinfo=timezone.utc)
                seconds = max(0.0, at.timestamp() - time.time())
            except (TypeError, ValueError, OverflowError):
                return None
        refusal.retry_after = seconds
        delay = min(seconds, DELAY_CAP)
        remaining = self.remaining()
        if self.attempt >= MAX_ATTEMPTS or (remaining is not None and delay >= remaining):
            return None
        self.attempt += 1
        return delay

    @staticmethod
    def wait(delay: float) -> None:
        time.sleep(delay)
