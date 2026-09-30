#!/usr/bin/env python3
"""Qualify a specific native SDK nupkg through a clean package-only consumer."""

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[2]
PHASE_TIMEOUT_SECONDS = 120
MAXIMUM_OUTPUT_BYTES = 1024 * 1024
RETAINED_OUTPUT_BYTES = 32768
EXPECTED_PROVENANCE_SHA256 = "0a63bafe4ada0aca4d440944d514cf63ce11298143e332ae87cf7a3af3146450"
EXPECTED_ARTIFACTS = {
    "client-openapi.json": (98999, "dc9936a2825b10e5da9eb7801964751bcd211bc8e7a197ca41736af73af7fd98"),
    "atomic_vectors.json": (8776, "4ce4286b6e2eeae4ee15dec83f788f33cffdba7606a49021ec81afe0a4a9e816"),
    "atlas/native-wire-identity.json": (2581, "b789d6fb7782e34ece3b516139856f2b6d2dea0543436820eefa50ff31355538"),
    "atlas/native-discovery-success.json": (767, "d32d035bed11db296f3f911cc01f43cd6a3fafe208e005fe6abb35bc183759a0"),
    "atlas/native-discovery-forbidden.json": (154, "3208d206e45cde22446e0e227c4882bdacb97f2a4926c0341a13580c23e48b9c"),
}
EXPECTED_PROVENANCE = {
    "schema": "terrascale.dotnet.native-contract-inputs.v1",
    "producer_contract_family": "native-v1",
    "artifacts": [{"path": name, "bytes": size, "sha256": sha} for name, (size, sha) in EXPECTED_ARTIFACTS.items()],
    "foundation_manifest_sha256": "daf01f656ea1559029f417801d63bd746729d31f324ae41ab6ec11c9e3e22e7d",
    "foundation_review_sha256": "6a4edd023dd6b95389ce4325c82296789e24db9426ee2093dcb6af352d962911",
    "discovery_manifest_sha256": "7fd968f0e4552f8f3dcdf90eae2b3e64f704ea02c30ec45e0941ce9010b9f930",
    "discovery_review_sha256": "822871a5e0fd4798514e6e5783d191df79c8e18e49c78b0b026da57dc96d61cb",
    "openapi_effect_example_correction": {
        "preimage_sha256": "2c8fc075bb2714de6f0ba10756f903e09e4d2a43ecaa63b2135ccb2042545a52",
        "manifest_sha256": "79d13c23e706b4ff3a2a2d35dc98306a99fcd3f3cfa12bbb89b6dd08a540273e",
        "identity_mapping_sha256": "89e61a94011507c211c617db2ff6616c66cc309e7fd3d82ac539cd34b1b32388",
        "accepted_encoder_proof_sha256": "6a6505fe6076b7c734bfc60a07ae84a92b3aad55d7d98b48ba1a65d0d2ecdeb4",
        "accepted_encoder_result_sha256": "b980cc24a1ad7977abd1a0d7d3f3b8ec3538e1d315203b5234a6c0079e1d952e",
        "independent_reviewer": "/root/cli_check_04",
        "independent_verdict": "ACCEPTED_SOURCE_CORRECTION_ONLY",
    },
}


def stop_process_tree(process: subprocess.Popen) -> None:
    if os.name == "posix":
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    else:
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5, check=False)
        if process.poll() is None:
            process.kill()


def run_phase(command: list[str], directory: Path, environment: dict[str, str]) -> dict:
    phase = {"command": command, "exit_code": None, "output_bytes": 0, "output": ""}
    try:
        process = subprocess.Popen(command, cwd=directory, env=environment, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, start_new_session=os.name == "posix")
    except OSError as error:
        phase["failure"] = str(error)
        return phase
    tail = bytearray()
    exceeded = threading.Event()
    byte_count = 0

    def drain() -> None:
        nonlocal byte_count
        assert process.stdout is not None
        try:
            while chunk := process.stdout.read(8192):
                byte_count += len(chunk)
                tail.extend(chunk)
                del tail[:-RETAINED_OUTPUT_BYTES]
                if byte_count > MAXIMUM_OUTPUT_BYTES:
                    exceeded.set()
                    break
        finally:
            process.stdout.close()

    reader = threading.Thread(target=drain, daemon=True)
    reader.start()
    expires = time.monotonic() + PHASE_TIMEOUT_SECONDS
    try:
        while process.poll() is None:
            if exceeded.is_set():
                raise RuntimeError("Phase output exceeded 1 MiB.")
            remaining = expires - time.monotonic()
            if remaining <= 0:
                raise RuntimeError("Phase exceeded its 120-second deadline.")
            try:
                process.wait(timeout=min(remaining, 0.1))
            except subprocess.TimeoutExpired:
                pass
        reader.join(timeout=max(0, expires - time.monotonic()))
        if reader.is_alive():
            raise RuntimeError("Phase output stream exceeded its 120-second deadline.")
        if exceeded.is_set():
            raise RuntimeError("Phase output exceeded 1 MiB.")
    except RuntimeError as error:
        phase["failure"] = str(error)
        stop_process_tree(process)
        process.wait(timeout=5)
        reader.join(timeout=5)
    phase["exit_code"] = process.returncode
    phase["output_bytes"] = byte_count
    phase["output"] = tail.decode("utf-8", errors="replace")
    return phase


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--framework", choices=("net10.0", "net11.0"), required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    arguments = parser.parse_args()
    if arguments.package.stat().st_size > 64 * 1024 * 1024:
        raise SystemExit("Native candidate package exceeds the qualification input limit.")
    package_bytes = arguments.package.read_bytes()
    package_sha = hashlib.sha256(package_bytes).hexdigest()
    if package_sha != arguments.sha256 or len(arguments.sha256) != 64:
        raise SystemExit("Native candidate package identity differs.")
    if re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?", arguments.version) is None:
        raise SystemExit("An exact native candidate package version is required.")
    with zipfile.ZipFile(io.BytesIO(package_bytes)) as archive:
        nuspecs = [name for name in archive.namelist() if name.endswith(".nuspec")]
        if len(nuspecs) != 1 or archive.getinfo(nuspecs[0]).file_size > 1024 * 1024:
            raise SystemExit("Native candidate package metadata is invalid.")
        metadata = ET.fromstring(archive.read(nuspecs[0]))
        fields = {element.tag.rsplit("}", 1)[-1]: element.text for element in metadata.iter()}
        if fields.get("id") != "TerraScale.DotNet" or fields.get("version") != arguments.version:
            raise SystemExit("Native candidate package ID or version differs.")
        archive_contracts = {}
        for name, (size, expected_sha) in EXPECTED_ARTIFACTS.items():
            entry = "contracts/native/" + name
            if archive.namelist().count(entry) != 1 or archive.getinfo(entry).file_size != size:
                raise SystemExit("Native package contract entry differs from the independent producer pin.")
            data = archive.read(entry)
            actual_sha = hashlib.sha256(data).hexdigest()
            if actual_sha != expected_sha:
                raise SystemExit("Native package contract bytes differ from the independent producer pin.")
            archive_contracts[name] = {"bytes": size, "sha256": actual_sha}
        provenance_entry = "contracts/native/provenance.json"
        if archive.namelist().count(provenance_entry) != 1 or archive.getinfo(provenance_entry).file_size > 8192:
            raise SystemExit("Native package provenance entry is invalid.")
        provenance_bytes = archive.read(provenance_entry)
        if hashlib.sha256(provenance_bytes).hexdigest() != EXPECTED_PROVENANCE_SHA256 or json.loads(provenance_bytes) != EXPECTED_PROVENANCE:
            raise SystemExit("Native package provenance differs from the independent predecessor pins.")
    if arguments.receipt.exists():
        raise SystemExit("The qualification receipt must not overwrite existing evidence.")

    receipt = {
        "schema": "terrascale.dotnet.native-package-consumer.v1",
        "package_sha256": package_sha,
        "package_version": arguments.version,
        "framework": arguments.framework,
        "status": "NOT RUN",
        "source_inputs": {},
        "execution_source_inputs": {},
        "runner_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "archive_contracts": archive_contracts,
        "archive_provenance_sha256": hashlib.sha256(provenance_bytes).hexdigest(),
        "phases": [],
        "producer_native": "NOT RUN",
        "cleanup": False,
        "phase_timeout_seconds": PHASE_TIMEOUT_SECONDS,
        "phase_output_limit_bytes": MAXIMUM_OUTPUT_BYTES,
    }
    with tempfile.TemporaryDirectory(prefix="terrascale-native-package-") as temporary:
        scratch = Path(temporary)
        feed = scratch / "feed"
        feed.mkdir()
        (feed / arguments.package.name).write_bytes(package_bytes)
        consumer = scratch / "consumer"
        consumer.mkdir()
        for name in ("NativeConsumer.csproj", "NativeConsumer.cs", "NativeChecks.cs"):
            origin = ROOT / "tests/TerraScale.DotNet.Qualification" / name
            data = origin.read_bytes()
            receipt["source_inputs"][name] = hashlib.sha256(data).hexdigest()
            (consumer / name).write_bytes(data)
        project = ET.parse(consumer / "NativeConsumer.csproj")
        ET.SubElement(project.getroot().find("PropertyGroup"), "TargetFramework").text = arguments.framework
        project.write(consumer / "NativeConsumer.csproj", encoding="utf-8", xml_declaration=False)
        configuration = consumer / "NuGet.Config"
        configuration.write_text(
            '<configuration><packageSources><clear />'
            '<add key="candidate" value="../feed" />'
            '<add key="nuget" value="https://api.nuget.org/v3/index.json" />'
            '</packageSources><packageSourceMapping>'
            '<packageSource key="candidate"><package pattern="TerraScale.DotNet" /></packageSource>'
            '<packageSource key="nuget"><package pattern="*" /></packageSource>'
            '</packageSourceMapping></configuration>\n', encoding="utf-8")
        receipt["nuget_configuration_sha256"] = hashlib.sha256(configuration.read_bytes()).hexdigest()
        for name in ("NativeConsumer.csproj", "NativeConsumer.cs", "NativeChecks.cs"):
            receipt["execution_source_inputs"][name] = hashlib.sha256((consumer / name).read_bytes()).hexdigest()
        for name in ("tmp", "http-cache", "plugin-cache", "nuget-scratch"):
            (scratch / name).mkdir()
        environment = os.environ.copy()
        environment.update({
            "DOTNET_CLI_HOME": str(scratch / "dotnet-home"),
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
            "DOTNET_CLI_USE_MSBUILD_SERVER": "0",
            "NUGET_PACKAGES": str(scratch / "packages"),
            "NUGET_HTTP_CACHE_PATH": str(scratch / "http-cache"),
            "NUGET_PLUGINS_CACHE_PATH": str(scratch / "plugin-cache"),
            "NUGET_SCRATCH": str(scratch / "nuget-scratch"),
            "TMPDIR": str(scratch / "tmp"),
            "TMP": str(scratch / "tmp"),
            "TEMP": str(scratch / "tmp"),
        })
        property_argument = "-p:NativeSdkPackageVersion=" + arguments.version
        commands = [
            ["dotnet", "restore", "NativeConsumer.csproj", property_argument, "--disable-build-servers", "--verbosity", "quiet"],
            ["dotnet", "run", "--project", "NativeConsumer.csproj", "--no-restore", "--configuration", "Release",
             property_argument, "-p:UseSharedCompilation=false", "--disable-build-servers", "--verbosity", "quiet"],
        ]
        try:
            for command in commands:
                phase = run_phase(command, consumer, environment)
                receipt["phases"].append(phase)
                if phase["exit_code"] != 0 or "failure" in phase:
                    raise RuntimeError("Native package consumer phase failed.")
            assets = json.loads((consumer / "obj/project.assets.json").read_text())
            expected = "terrascale.dotnet/" + arguments.version.lower()
            if not any(name.lower() == expected for name in assets["libraries"]):
                raise RuntimeError("Consumer resolved another SDK version.")
            if "Native SDK public package consumer passed." not in receipt["phases"][-1]["output"]:
                raise RuntimeError("Native consumer completion marker is absent.")
            receipt["status"] = "PASS_PACKAGE_PUBLIC_HANDLER_CONSUMER"
        except (RuntimeError, OSError, subprocess.TimeoutExpired) as error:
            receipt["status"] = "FAIL"
            receipt["failure"] = str(error)
        finally:
            receipt["final_execution_source_inputs"] = {
                name: hashlib.sha256((consumer / name).read_bytes()).hexdigest()
                for name in ("NativeConsumer.csproj", "NativeConsumer.cs", "NativeChecks.cs")
            }
            receipt["final_nuget_configuration_sha256"] = hashlib.sha256(configuration.read_bytes()).hexdigest()
            receipt["final_runner_sha256"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
            receipt["execution_inputs_unchanged"] = (
                receipt["execution_source_inputs"] == receipt["final_execution_source_inputs"] and
                receipt["nuget_configuration_sha256"] == receipt["final_nuget_configuration_sha256"] and
                receipt["runner_sha256"] == receipt["final_runner_sha256"])
            if not receipt["execution_inputs_unchanged"]:
                receipt["status"] = "FAIL"
                receipt["failure"] = "Native package qualification inputs changed during execution."
    receipt["cleanup"] = not scratch.exists()
    arguments.receipt.parent.mkdir(parents=True, exist_ok=True)
    with arguments.receipt.open("x", encoding="utf-8") as output:
        output.write(json.dumps(receipt, indent=2) + "\n")
    if receipt["status"] == "FAIL":
        raise SystemExit("Native package consumer failed; inspect the owned receipt.")


if __name__ == "__main__":
    main()
