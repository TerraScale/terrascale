import os
from pathlib import Path
import re
import subprocess
import sys

groups = (
    {"Management", "Import", "Tenancy"},
    {"Backup", "Storage", "Qos"},
    {"Shapes", "Documents", "Sql"},
)
shard = int(sys.argv[1])
if shard not in range(1, 5):
    raise ValueError("Expected shard 1 through 4")
namespaces = set()
for path in Path("tests/TerraBase.Fast").rglob("*.cs"):
    if {"bin", "obj"} & set(path.parts):
        continue
    match = re.search(r"^namespace (TerraBase\.Fast[\w.]*);", path.read_text(), re.M)
    if match:
        namespaces.add(match[1])
assigned = [{n for n in namespaces if n.split(".")[2] in group} for group in groups]
assigned.append(namespaces - set.union(*assigned))
assert set.union(*assigned) == namespaces
assert sum(map(len, assigned)) == len(namespaces)
selected = sorted(assigned[shard - 1])
if not selected:
    raise ValueError(f"Shard {shard} has no namespaces")
filter_value = "/*/" + "|".join(f"({n})" for n in selected) + "/*/*"
results = Path("artifacts/test-results")
results.mkdir(parents=True, exist_ok=True)
print(f"Shard {shard}: {filter_value}", flush=True)
subprocess.run([
    "dotnet", "tests/TerraBase.Fast/bin/Release/net11.0/TerraBase.Fast.dll",
    "--treenode-filter", filter_value, "--minimum-expected-tests", "1",
    "--maximum-parallel-tests", "16", "--report-trx",
    "--results-directory", str(results.resolve()),
    "--report-trx-filename", f"terrabase-{shard}.trx", "--no-ansi",
], check=True)
if shard == 2:
    subprocess.run([
        "dotnet", "tests/Turso.Data.Sqlite.Tests/bin/Release/net11.0/Turso.Data.Sqlite.Tests.dll",
        "--minimum-expected-tests", "23",
    ], check=True)
if shard == 4:
    subprocess.run([
        "make", "-j2", "-o", "restore", "-o", "terrabase-build",
        "terrabase-replication-matrix", "terrabase-e2e-local", "CONFIGURATION=Release",
        "TERRABASE_E2E_RUN=dotnet tests/TerraBase.E2E/bin/Release/net11.0/TerraBase.E2E.dll",
    ], check=True, env=os.environ)
