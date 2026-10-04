#!/usr/bin/env python3
"""Redact a test log in one bounded-memory pass and keep two excerpts.

usage: excerpt-log.py LOG FAILURES_OUT TAIL_OUT

Redaction runs on every line before anything is selected, so an excerpt never
holds a value without the key that marked it. State carries across lines: a
sensitive key whose quoted value is still open at the end of a line keeps
redacting until the closing quote, and a key followed only by its separator
redacts the next non-empty line (pretty-printed JSON). Lines are read in
chunks of at most CHUNK bytes, so one enormous line cannot exhaust memory.

FAILURES_OUT gets the first MAX_FAILURES lines that look like test failures,
TAIL_OUT the last TAIL_LINES lines; both are capped per line at LINE_CAP chars.
"""
import collections
import re
import sys

CHUNK = 64 * 1024
LINE_CAP = 2000
MAX_FAILURES = 60
TAIL_LINES = 80
REDACTED = "[REDACTED]"

KEY = (r"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{0,64}?"
       r"(?:SECRET|TOKEN|AUTHORIZATION|PASSWORD|PASSWD|API[_-]KEY|MASTER[_-]KEY|SIGNING[_-]KEY|"
       r"OPERATOR[_-]KEY|SESSION|COOKIE|DSN)[A-Za-z0-9_-]{0,64}[\"']?[ \t]*[:=][ \t]*"
       r"(?:(?:bearer|basic|digest|token)[ \t]+)?")
# Key with a complete value on this line (quoted, or unquoted to end of line).
KEY_VALUE = re.compile("(?i)(" + KEY + r")(?:\"(?:[^\"\\]|\\.)*\"|'(?:[^'\\]|\\.)*'|[^\"'\r\n][^\r\n]*)")
# Key whose quoted value is still open at the end of the line.
KEY_OPEN_QUOTE = re.compile("(?i)(" + KEY + r")([\"'])(?:[^\"'\\]|\\.)*$")
# Key followed only by its separator: the value is on the next line.
KEY_DANGLING = re.compile("(?i)(" + KEY + r")[ \t]*$")
RULES = [
    (re.compile(r"\x1b\[[0-9;]*[A-Za-z]"), ""),
    (re.compile(r"((?<![A-Za-z0-9+.-])[A-Za-z][A-Za-z0-9+.-]{0,31}://)[^/@\s]{1,2048}@"), r"\1[REDACTED]@"),
    (re.compile(r"(?i)((?:proxy-)?authorization|set-cookie|cookie|x-api-key|api-key)([\"']?[ \t]*[:=][ \t]*)[^\n]*"), r"\1\2[REDACTED]"),
    (re.compile(r"(?i)\b(bearer|basic|digest)([ \t]+)\S+"), r"\1\2[REDACTED]"),
    (re.compile(r"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+"), "[REDACTED-JWT]"),
    (re.compile(r"\b(sk|kub|tb)_[A-Za-z0-9_-]{16,}"), "[REDACTED]"),
    (re.compile(r"`{3,}"), "` ` `"),
]
FAILURE = re.compile(r"^failed |AssertionException|Expected |but (received|found)")


class Redactor:
    def __init__(self):
        self.open_quote = None  # quote character of a value still open
        self.dangling = False   # the next non-empty line is a value

    def line(self, text):
        if self.open_quote is not None:
            end = closing_quote(text, self.open_quote)
            if end < 0:
                return REDACTED
            self.open_quote = None
            text = REDACTED + text[end + 1:]
        elif self.dangling:
            if not text.strip():
                return text
            self.dangling = False
            return REDACTED

        opened = KEY_OPEN_QUOTE.search(text)
        if opened:
            self.open_quote = opened.group(2)
            text = text[:opened.start()] + opened.group(1) + REDACTED
        elif KEY_DANGLING.search(text):
            self.dangling = True
        text = KEY_VALUE.sub(lambda match: match.group(1) + REDACTED, text)
        for pattern, replacement in RULES:
            text = pattern.sub(replacement, text)
        return text


def closing_quote(text, quote):
    escaped = False
    for index, char in enumerate(text):
        if escaped:
            escaped = False
        elif char == "\\":
            escaped = True
        elif char == quote:
            return index
    return -1


def main():
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    log, failures_out, tail_out = sys.argv[1:]
    redactor = Redactor()
    failures = []
    tail = collections.deque(maxlen=TAIL_LINES)
    with open(log, "rb") as source:
        while True:
            chunk = source.readline(CHUNK)
            if not chunk:
                break
            text = redactor.line(chunk.decode("utf-8", errors="replace").rstrip("\r\n"))
            capped = text if len(text) <= LINE_CAP else text[:LINE_CAP] + " [line truncated]"
            if len(failures) < MAX_FAILURES and FAILURE.search(text):
                failures.append(capped)
            tail.append(capped)
    with open(failures_out, "w", encoding="utf-8") as out:
        out.writelines(line + "\n" for line in failures)
    with open(tail_out, "w", encoding="utf-8") as out:
        out.writelines(line + "\n" for line in tail)


if __name__ == "__main__":
    main()
