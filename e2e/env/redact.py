import re
import sys
from pathlib import Path

query = re.compile(
    rb"""((?:&amp;|\\u0026|[?&#])[A-Za-z0-9_.%~-]+=)(?:(?!\\u0026|\\?["\'])[^\s&#<>`()\[\]{}])*"""
)


def redact(data: bytes) -> bytes:
    try:
        data.decode("utf-8")
    except UnicodeDecodeError:
        return data
    return query.sub(rb"\1REDACTED", data)


if __name__ == "__main__":
    for name in sys.argv[1:]:
        path = Path(name)
        path.write_bytes(redact(path.read_bytes()))
