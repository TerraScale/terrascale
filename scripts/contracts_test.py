#!/usr/bin/env python3
"""Adversarial tests for the contract archive boundary."""

import io
import tarfile
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from contracts import openapi_metadata, unpack_url


class _Response:
    def __init__(self, payload):
        self.payload = payload

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False

    def read(self, size=-1):
        if size < 0:
            result, self.payload = self.payload, b""
            return result
        result, self.payload = self.payload[:size], self.payload[size:]
        return result


class _Zeros:
    def __init__(self, remaining):
        self.remaining = remaining

    def read(self, size=-1):
        size = self.remaining if size < 0 else min(size, self.remaining)
        self.remaining -= size
        return b"\0" * size


def archive(*entries):
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode="w") as bundle:
        for name, kind, payload in entries:
            member = tarfile.TarInfo(name)
            member.type = kind
            if kind == tarfile.REGTYPE:
                member.size = len(payload)
                bundle.addfile(member, io.BytesIO(payload))
            else:
                bundle.addfile(member)
    return output.getvalue()


class ArchiveBoundaryTests(unittest.TestCase):
    def assert_rejected(self, payload):
        with tempfile.TemporaryDirectory() as directory:
            with patch("contracts.urllib.request.urlopen", return_value=_Response(payload)):
                with self.assertRaises(SystemExit):
                    unpack_url("https://registry.invalid/v1.tar", Path(directory))

    def test_rejects_path_traversal_and_duplicate_entries(self):
        self.assert_rejected(archive(("../escape", tarfile.REGTYPE, b"x")))
        self.assert_rejected(archive(("one", tarfile.REGTYPE, b"x"), ("one", tarfile.REGTYPE, b"y")))

    def test_rejects_links_and_special_files(self):
        self.assert_rejected(archive(("link", tarfile.SYMTYPE, b"target")))
        self.assert_rejected(archive(("hard", tarfile.LNKTYPE, b"target")))
        self.assert_rejected(archive(("device", tarfile.CHRTYPE, b"")))
        self.assert_rejected(archive(("pipe", tarfile.FIFOTYPE, b"")))

    def test_rejects_oversized_entry(self):
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode="w") as bundle:
            member = tarfile.TarInfo("oversized")
            member.size = 16 * 1024 * 1024 + 1
            # tarfile does not read the payload until extraction; the boundary
            # check must reject the declared size first.
            bundle.addfile(member, _Zeros(member.size))
        self.assert_rejected(output.getvalue())

    def test_rejects_too_many_entries(self):
        entries = [(f"entry-{index}", tarfile.DIRTYPE, b"") for index in range(257)]
        self.assert_rejected(archive(*entries))


class OpenApiMetadataTests(unittest.TestCase):
    def test_resolves_inline_array_and_scalar_success_schemas(self):
        document = {
            "components": {"schemas": {"ImportJob": {"type": "object"}}},
            "paths": {
                "/imports": {"get": {"operationId": "list_imports", "responses": {
                    "200": {"content": {"application/json": {"schema": {
                        "type": "array", "items": {"$ref": "#/components/schemas/ImportJob"}
                    }}}}
                }}},
                "/exports/{member}": {"get": {"operationId": "download_export", "responses": {
                    "200": {"content": {"application/octet-stream": {"schema": {"type": "string"}}}}
                }}},
            },
        }
        operations, _, _ = openapi_metadata(document)
        by_id = {operation["operationID"]: operation for operation in operations}
        self.assertEqual(by_id["list_imports"]["responseModel"], "[]ImportJob")
        self.assertEqual(by_id["download_export"]["responseModel"], "string")

if __name__ == "__main__":
    unittest.main()
