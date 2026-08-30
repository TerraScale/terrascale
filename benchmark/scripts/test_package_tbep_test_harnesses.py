#!/usr/bin/env python3
"""Black-box tests for the receipt-bound TBEP harness packager."""

from __future__ import annotations

import json
import os
import struct
import subprocess
import tempfile
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
PACKAGER = REPOSITORY_ROOT / "scripts" / "package_tbep_test_harnesses.sh"
DESCRIPTOR = REPOSITORY_ROOT / ".terrascale" / "deploy.json"
GITIGNORE = REPOSITORY_ROOT / ".gitignore"
OUTPUT = Path("artifacts/dependencies/terrabase-tbep-harnesses")
PRODUCTION = Path(
    "target/tbep-test-harnesses/production/"
    "x86_64-unknown-linux-musl/release/tbep-production-harness"
)
PEER = Path(
    "target/tbep-test-harnesses/flatbuffers/"
    "x86_64-unknown-linux-musl/release/tbep-fbs-quic-peer"
)
PREPARATION = Path("target/tbep-test-harnesses/prepared.v1")
BINDING = Path("target/tbep-test-harnesses/package-binding.v1")


class TbepHarnessPackagerTests(unittest.TestCase):
    """Exercises package and verification failures in disposable repositories."""

    def create_repository(self, prepare: bool = True) -> Path:
        """Creates a clean repository and optionally prepares valid static fixtures."""

        temporary = tempfile.TemporaryDirectory(prefix="terrabase-tbep-package-")
        self.addCleanup(temporary.cleanup)
        repository = Path(temporary.name)
        self.write(repository / ".gitignore", GITIGNORE.read_bytes())
        self.write(repository / ".terrascale/deploy.json", DESCRIPTOR.read_bytes())
        self.write(repository / "source.txt", b"source\n")
        self.git(repository, "init", "--quiet", "--initial-branch=main")
        self.git(repository, "config", "user.email", "candidate-test@terrascale.invalid")
        self.git(repository, "config", "user.name", "TerraBase Candidate Test")
        self.git(repository, "add", ".")
        self.git(repository, "commit", "--quiet", "-m", "fixture")
        if prepare:
            self.prepare(repository)
            self.write_elf(repository / PRODUCTION, b"production")
            self.write_elf(repository / PEER, b"peer")
        return repository

    @staticmethod
    def write(path: Path, content: bytes) -> None:
        """Writes one fixture below its exact parents."""

        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    @classmethod
    def write_elf(cls, path: Path, marker: bytes) -> None:
        """Writes a runnable static x86-64 ELF fixture with one RX load segment."""

        code = b"\xb8\x3c\x00\x00\x00\x31\xff\x0f\x05"
        image_size = 64 + 56 + len(code) + len(marker)
        identifier = b"\x7fELF\x02\x01\x01" + (b"\0" * 9)
        header = struct.pack(
            "<16sHHIQQQIHHHHHH",
            identifier,
            2,
            0x3E,
            1,
            0x400000 + 64 + 56,
            64,
            0,
            0,
            64,
            56,
            1,
            0,
            0,
            0,
        )
        program_header = struct.pack(
            "<IIQQQQQQ",
            1,
            5,
            0,
            0x400000,
            0x400000,
            image_size,
            image_size,
            0x1000,
        )
        content = header + program_header + code + marker
        cls.write(path, content)
        path.chmod(0o755)

    @classmethod
    def write_interpreted_elf(cls, path: Path, interpreter: bytes) -> None:
        """Writes a valid ELF fixture with a forbidden PT_INTERP segment."""

        code = b"\xb8\x3c\x00\x00\x00\x31\xff\x0f\x05"
        interpreter += b"\0"
        code_offset = 64 + (2 * 56)
        interpreter_offset = code_offset + len(code)
        image_size = interpreter_offset + len(interpreter)
        identifier = b"\x7fELF\x02\x01\x01" + (b"\0" * 9)
        header = struct.pack(
            "<16sHHIQQQIHHHHHH",
            identifier,
            2,
            0x3E,
            1,
            0x400000 + code_offset,
            64,
            0,
            0,
            64,
            56,
            2,
            0,
            0,
            0,
        )
        interpreter_header = struct.pack(
            "<IIQQQQQQ",
            3,
            4,
            interpreter_offset,
            0x400000 + interpreter_offset,
            0x400000 + interpreter_offset,
            len(interpreter),
            len(interpreter),
            1,
        )
        load_header = struct.pack(
            "<IIQQQQQQ",
            1,
            5,
            0,
            0x400000,
            0x400000,
            image_size,
            image_size,
            0x1000,
        )
        cls.write(
            path,
            header + interpreter_header + load_header + code + interpreter,
        )
        path.chmod(0o755)

    @classmethod
    def write_dynamic_elf(cls, path: Path, tag: int, value: bytes) -> None:
        """Writes a valid ELF fixture with one forbidden dynamic string tag."""

        code = b"\xb8\x3c\x00\x00\x00\x31\xff\x0f\x05"
        code_offset = 64 + (2 * 56)
        dynamic_offset = (code_offset + len(code) + 7) & ~7
        string_offset = dynamic_offset + (4 * 16)
        string_table = value + b"\0"
        image_size = string_offset + len(string_table)
        identifier = b"\x7fELF\x02\x01\x01" + (b"\0" * 9)
        header = struct.pack(
            "<16sHHIQQQIHHHHHH",
            identifier,
            2,
            0x3E,
            1,
            0x400000 + code_offset,
            64,
            0,
            0,
            64,
            56,
            2,
            0,
            0,
            0,
        )
        dynamic_header = struct.pack(
            "<IIQQQQQQ",
            2,
            4,
            dynamic_offset,
            0x400000 + dynamic_offset,
            0x400000 + dynamic_offset,
            4 * 16,
            4 * 16,
            8,
        )
        load_header = struct.pack(
            "<IIQQQQQQ",
            1,
            5,
            0,
            0x400000,
            0x400000,
            image_size,
            image_size,
            0x1000,
        )
        dynamic = b"".join(
            [
                struct.pack("<QQ", 5, 0x400000 + string_offset),
                struct.pack("<QQ", 10, len(string_table)),
                struct.pack("<QQ", tag, 0),
                struct.pack("<QQ", 0, 0),
            ]
        )
        padding = b"\0" * (dynamic_offset - code_offset - len(code))
        cls.write(
            path,
            header
            + dynamic_header
            + load_header
            + code
            + padding
            + dynamic
            + string_table,
        )
        path.chmod(0o755)

    @staticmethod
    def git(repository: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
        """Runs Git without reading ambient user configuration."""

        result = subprocess.run(
            ["git", *arguments],
            cwd=repository,
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode != 0:
            raise AssertionError(result.stderr)
        return result

    @staticmethod
    def run_packager(
        repository: Path,
        mode: str = "package",
        target: str = "tbep-test-harnesses",
        output: str = OUTPUT.as_posix(),
    ) -> subprocess.CompletedProcess[str]:
        """Runs the real packager with its fixed candidate arguments."""

        return subprocess.run(
            [
                str(PACKAGER),
                mode,
                "--repository-root",
                str(repository),
                "--target",
                target,
                "--output",
                output,
            ],
            capture_output=True,
            text=True,
            check=False,
        )

    def package(self, repository: Path) -> subprocess.CompletedProcess[str]:
        """Packages successfully or fails the current test immediately."""

        result = self.run_packager(repository)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def prepare(self, repository: Path) -> subprocess.CompletedProcess[str]:
        """Creates the one-shot clean-build binding or fails immediately."""

        result = self.run_packager(repository, mode="prepare")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue((repository / PREPARATION).is_file())
        return result

    def test_descriptor_builds_each_locked_release_binary_once(self) -> None:
        """Pins the producer name, paths, toolchain, architecture, and copy step."""

        descriptor = json.loads(DESCRIPTOR.read_text(encoding="utf-8"))
        self.assertEqual(
            descriptor,
            {
                "repository": "TerraBase",
                "buildTargets": [
                    {
                        "name": "tbep-test-harnesses",
                        "resourceWeight": "heavy",
                        "outputPath": OUTPUT.as_posix(),
                        "commands": [
                            {
                                "description": (
                                    "Require fresh candidate-private TBEP build directories"
                                ),
                                "command": "bash",
                                "arguments": [
                                    "scripts/package_tbep_test_harnesses.sh",
                                    "prepare",
                                    "--repository-root",
                                    ".",
                                    "--target",
                                    "tbep-test-harnesses",
                                    "--output",
                                    OUTPUT.as_posix(),
                                ],
                            },
                            {
                                "description": (
                                    "Build the static production TBEP harness "
                                    "from the locked workspace"
                                ),
                                "command": "cargo",
                                "arguments": [
                                    "+1.97.1",
                                    "build",
                                    "--locked",
                                    "--release",
                                    "--manifest-path",
                                    "crates/terrabase-tbep/Cargo.toml",
                                    "--bin",
                                    "tbep-production-harness",
                                    "--target",
                                    "x86_64-unknown-linux-musl",
                                    "--target-dir",
                                    "target/tbep-test-harnesses/production",
                                ],
                            },
                            {
                                "description": (
                                    "Build the static generated FlatBuffers QUIC peer "
                                    "from its locked workspace"
                                ),
                                "command": "cargo",
                                "arguments": [
                                    "+1.97.1",
                                    "build",
                                    "--locked",
                                    "--release",
                                    "--manifest-path",
                                    "crates/terrabase-tbep/tools/tbep-fbs/Cargo.toml",
                                    "--bin",
                                    "tbep-fbs-quic-peer",
                                    "--target",
                                    "x86_64-unknown-linux-musl",
                                    "--target-dir",
                                    "target/tbep-test-harnesses/flatbuffers",
                                ],
                            },
                            {
                                "description": (
                                    "Package and verify the exact receipt-bound "
                                    "TBEP harness tree"
                                ),
                                "command": "bash",
                                "arguments": [
                                    "scripts/package_tbep_test_harnesses.sh",
                                    "package",
                                    "--repository-root",
                                    ".",
                                    "--target",
                                    "tbep-test-harnesses",
                                    "--output",
                                    OUTPUT.as_posix(),
                                ],
                            },
                        ],
                    }
                ],
            },
        )

    def test_package_contains_exactly_the_two_source_identical_executables(self) -> None:
        repository = self.create_repository()

        result = self.package(repository)

        files = sorted(path.name for path in (repository / OUTPUT).iterdir())
        self.assertEqual(files, ["tbep-fbs-quic-peer", "tbep-production-harness"])
        self.assertEqual(
            (repository / OUTPUT / PRODUCTION.name).read_bytes(),
            (repository / PRODUCTION).read_bytes(),
        )
        self.assertEqual(
            (repository / OUTPUT / PEER.name).read_bytes(),
            (repository / PEER).read_bytes(),
        )
        self.assertTrue(os.access(repository / OUTPUT / PRODUCTION.name, os.X_OK))
        self.assertTrue(os.access(repository / OUTPUT / PEER.name, os.X_OK))
        for binary in (PRODUCTION.name, PEER.name):
            execution = subprocess.run(
                [repository / OUTPUT / binary],
                capture_output=True,
                check=False,
                timeout=2,
            )
            self.assertEqual(execution.returncode, 0, execution.stderr)
        self.assertIn("target=tbep-test-harnesses", result.stdout)
        self.assertIn("files=2", result.stdout)
        self.assertTrue((repository / BINDING).is_file())

    def test_missing_binary_fails_without_leaving_partial_output(self) -> None:
        repository = self.create_repository()
        (repository / PEER).unlink()

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("locked Release FlatBuffers QUIC peer is missing", result.stderr)
        self.assertFalse((repository / OUTPUT).exists())

    def test_non_elf_build_output_is_rejected(self) -> None:
        repository = self.create_repository()
        self.write(repository / PRODUCTION, b"not an ELF executable\n")
        (repository / PRODUCTION).chmod(0o755)

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("locked Release production harness has a truncated ELF header", result.stderr)
        self.assertFalse((repository / OUTPUT).exists())

    def test_wrong_architecture_build_output_is_rejected(self) -> None:
        repository = self.create_repository()
        content = bytearray((repository / PEER).read_bytes())
        content[18:20] = b"\xb7\x00"
        self.write(repository / PEER, bytes(content))
        (repository / PEER).chmod(0o755)

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("locked Release FlatBuffers QUIC peer is not an x86-64 ELF", result.stderr)
        self.assertFalse((repository / OUTPUT).exists())

    def test_wrong_elf_class_is_rejected(self) -> None:
        repository = self.create_repository()
        content = bytearray((repository / PRODUCTION).read_bytes())
        content[4] = 1
        self.write(repository / PRODUCTION, bytes(content))
        (repository / PRODUCTION).chmod(0o755)

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("is not an ELF64 executable", result.stderr)

    def test_wrong_elf_byte_order_is_rejected(self) -> None:
        repository = self.create_repository()
        content = bytearray((repository / PRODUCTION).read_bytes())
        content[5] = 2
        self.write(repository / PRODUCTION, bytes(content))
        (repository / PRODUCTION).chmod(0o755)

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("is not a little-endian ELF executable", result.stderr)

    def test_non_executable_elf_type_is_rejected(self) -> None:
        repository = self.create_repository()
        content = bytearray((repository / PRODUCTION).read_bytes())
        content[16:18] = b"\x01\x00"
        self.write(repository / PRODUCTION, bytes(content))
        (repository / PRODUCTION).chmod(0o755)

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("is not an executable or position-independent executable", result.stderr)

    def test_out_of_bounds_program_header_table_is_rejected(self) -> None:
        repository = self.create_repository()
        content = bytearray((repository / PRODUCTION).read_bytes())
        content[32:40] = struct.pack("<Q", len(content) + 4096)
        self.write(repository / PRODUCTION, bytes(content))
        (repository / PRODUCTION).chmod(0o755)

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("has malformed ELF program headers", result.stderr)

    def test_writable_executable_load_segment_is_rejected(self) -> None:
        repository = self.create_repository()
        content = bytearray((repository / PRODUCTION).read_bytes())
        content[68:72] = struct.pack("<I", 7)
        self.write(repository / PRODUCTION, bytes(content))
        (repository / PRODUCTION).chmod(0o755)

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("has a writable executable load segment", result.stderr)

    def test_shared_object_without_pie_identity_is_rejected(self) -> None:
        repository = self.create_repository()
        content = bytearray((repository / PRODUCTION).read_bytes())
        content[16:18] = b"\x03\x00"
        self.write(repository / PRODUCTION, bytes(content))
        (repository / PRODUCTION).chmod(0o755)

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("is a shared object rather than", result.stderr)

    def test_program_interpreter_is_rejected(self) -> None:
        repository = self.create_repository()
        self.write_interpreted_elf(
            repository / PRODUCTION,
            b"/nix/store/unbound/lib/ld-linux-x86-64.so.2",
        )

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("embeds a program interpreter", result.stderr)

    def test_shared_library_dependency_is_rejected(self) -> None:
        repository = self.create_repository()
        self.write_dynamic_elf(repository / PRODUCTION, 1, b"libc.so.6")

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("depends on a shared library", result.stderr)

    def test_runtime_library_path_is_rejected(self) -> None:
        repository = self.create_repository()
        self.write_dynamic_elf(repository / PRODUCTION, 29, b"/nix/store/unbound/lib")

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("embeds a runtime library search path", result.stderr)

    def test_linked_build_output_is_rejected(self) -> None:
        repository = self.create_repository()
        production = repository / PRODUCTION
        production.unlink()
        production.symlink_to(repository / PEER)

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("path contains a symbolic link", result.stderr)
        self.assertFalse((repository / OUTPUT).exists())

    def test_linked_output_parent_is_rejected(self) -> None:
        repository = self.create_repository()
        artifacts = repository / "artifacts"
        artifacts.mkdir()
        (artifacts / "dependencies").symlink_to(
            repository / "target",
            target_is_directory=True,
        )

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("path contains a symbolic link", result.stderr)
        self.assertFalse((repository / OUTPUT).exists())

    def test_preexisting_output_is_rejected(self) -> None:
        repository = self.create_repository()
        self.write(repository / OUTPUT / "stale", b"stale candidate\n")

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("declared output must be absent before packaging", result.stderr)
        self.assertTrue((repository / OUTPUT / "stale").is_file())

    def test_preseeded_build_directory_is_rejected_before_cargo(self) -> None:
        repository = self.create_repository(prepare=False)
        self.write_elf(repository / PRODUCTION, b"preseeded")

        result = self.run_packager(repository, mode="prepare")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("fresh candidate path must be absent before building", result.stderr)
        self.assertFalse((repository / PREPARATION).exists())

    def test_package_without_fresh_build_binding_is_rejected(self) -> None:
        repository = self.create_repository(prepare=False)
        self.write_elf(repository / PRODUCTION, b"production")
        self.write_elf(repository / PEER, b"peer")

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("fresh-build preparation binding is absent", result.stderr)
        self.assertFalse((repository / OUTPUT).exists())

    def test_extra_output_entry_is_rejected(self) -> None:
        repository = self.create_repository()
        self.package(repository)
        self.write(repository / OUTPUT / "unexpected", b"extra")

        result = self.run_packager(repository, mode="verify")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("must contain exactly two files", result.stderr)

    def test_linked_output_is_rejected(self) -> None:
        repository = self.create_repository()
        self.package(repository)
        packaged = repository / OUTPUT / PRODUCTION.name
        packaged.unlink()
        packaged.symlink_to(repository / PRODUCTION)

        result = self.run_packager(repository, mode="verify")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("symbolic link", result.stderr)

    def test_non_executable_output_is_rejected(self) -> None:
        repository = self.create_repository()
        self.package(repository)
        (repository / OUTPUT / PEER.name).chmod(0o644)

        result = self.run_packager(repository, mode="verify")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("mode must be 0755", result.stderr)

    def test_permission_drift_is_rejected(self) -> None:
        repository = self.create_repository()
        self.package(repository)
        (repository / OUTPUT / PEER.name).chmod(0o700)

        result = self.run_packager(repository, mode="verify")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("mode must be 0755", result.stderr)

    def test_tampered_output_is_rejected(self) -> None:
        repository = self.create_repository()
        self.package(repository)
        self.write_elf(repository / OUTPUT / PEER.name, b"tampered")

        result = self.run_packager(repository, mode="verify")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("differs from the locked Release binary", result.stderr)

    def test_cross_tree_binding_is_rejected(self) -> None:
        repository = self.create_repository()
        self.package(repository)
        self.write(repository / "source.txt", b"different source\n")
        self.git(repository, "add", "source.txt")
        self.git(repository, "commit", "--quiet", "-m", "different tree")

        result = self.run_packager(repository, mode="verify")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("source commit does not match the fresh-build preparation", result.stderr)

    def test_cross_target_request_is_rejected(self) -> None:
        repository = self.create_repository()

        result = self.run_packager(repository, target="database-regression-tests")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("--target must be tbep-test-harnesses", result.stderr)
        self.assertFalse((repository / OUTPUT).exists())

    def test_cross_output_request_is_rejected(self) -> None:
        repository = self.create_repository()

        result = self.run_packager(
            repository,
            output="artifacts/dependencies/another-package",
        )

        self.assertNotEqual(result.returncode, 0)
        self.assertIn(
            "--output must be artifacts/dependencies/terrabase-tbep-harnesses",
            result.stderr,
        )
        self.assertFalse((repository / OUTPUT).exists())

    def test_dirty_configuration_is_rejected(self) -> None:
        repository = self.create_repository()
        self.write(repository / ".terrascale/deploy.json", b"{}\n")

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("source tree is dirty", result.stderr)

    def test_artifacts_are_ignored_without_hiding_source_or_configuration(self) -> None:
        repository = self.create_repository()
        self.write(repository / OUTPUT / "generated", b"candidate")
        self.write(repository / "source.txt", b"dirty source\n")
        self.write(repository / ".terrascale/deploy.json", b"{}\n")

        status = self.git(repository, "status", "--porcelain=v1", "--untracked-files=all").stdout

        self.assertNotIn("artifacts/", status)
        self.assertIn(".terrascale/deploy.json", status)
        self.assertIn("source.txt", status)

    def test_preparation_field_tamper_is_rejected(self) -> None:
        repository = self.create_repository()
        preparation = repository / PREPARATION
        content = preparation.read_text(encoding="utf-8")
        preparation.write_text(
            content.replace("target=tbep-test-harnesses", "target=other"),
            encoding="utf-8",
        )

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("fresh-build preparation binding has the wrong target", result.stderr)
        self.assertFalse((repository / OUTPUT).exists())

    def test_unknown_preparation_field_is_rejected(self) -> None:
        repository = self.create_repository()
        preparation = repository / PREPARATION
        preparation.write_text(
            preparation.read_text(encoding="utf-8") + "unexpected=value\n",
            encoding="utf-8",
        )

        result = self.run_packager(repository)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("preparation binding has the wrong field set", result.stderr)

    def test_binding_field_tamper_is_rejected(self) -> None:
        repository = self.create_repository()
        self.package(repository)
        binding = repository / BINDING
        content = binding.read_text(encoding="utf-8")
        binding.write_text(
            content.replace("target=tbep-test-harnesses", "target=other"),
            encoding="utf-8",
        )

        result = self.run_packager(repository, mode="verify")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("local package binding has the wrong target", result.stderr)

    def test_unknown_package_binding_field_is_rejected(self) -> None:
        repository = self.create_repository()
        self.package(repository)
        binding = repository / BINDING
        binding.write_text(
            binding.read_text(encoding="utf-8") + "unexpected=value\n",
            encoding="utf-8",
        )

        result = self.run_packager(repository, mode="verify")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("local package binding has the wrong field set", result.stderr)


if __name__ == "__main__":
    unittest.main()
