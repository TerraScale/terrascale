import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[3]
feature = root / "src/TerraBase/Features/Rules"
base = "/v1/projects/{project}/databases/{database}/rules"
paths = {"Rules": base, "Versions": base + "/versions", "Version": base + "/versions/{version}",
         "Restore": base + "/versions/{version}/restore"}
operations = []
for source in sorted(feature.glob("*Endpoint.cs")):
    text = source.read_text()
    method, route, suffix = re.search(r'(Get|Put|Post)\(RulesRoute\.(\w+)(?: \+ "([^"]+)")?\)', text).groups()
    operations.append({"method": method.upper(), "path": paths[route] + (suffix or ""),
                       "operation": re.search(r'Name\("([^"]+)"\)', text)[1]})
snapshot = {
    "producer": "src/TerraBase/Features/Rules",
    "revision": subprocess.check_output(["git", "log", "-1", "--format=%H", "--", "src/TerraBase/Features/Rules"], cwd=root, text=True).strip(),
    "inputs": {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest()
               for p in sorted(feature.glob("*.cs"))},
    "operations": operations,
    "preconditions": {"currentVersion": "If-Match: quoted ULID", "absentVersion": "If-None-Match: *"},
    "pagination": {"query": ["after", "limit"], "limit": [1, 100], "response": ["items", "next"], "order": "newest first"},
    "errors": {"400": {"code": "invalid_rules", "details": {"problems": ["table", "field", "code", "message"]}},
               "412": "version_mismatch", "428": "precondition_required"},
    "retry": "mutations sent once; reconcile uncertain outcomes by reading current rules",
}
for sdk in ("dotnet", "go", "python", "rust"):
    target = root / "sdk" / sdk / "contracts/terrabase/rules.json"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text("{\n" + ",\n".join(" " + json.dumps(k) + ": " + json.dumps(v) for k, v in snapshot.items()) + "\n}\n")
inventory = root / "sdk/dotnet/contracts/terrabase/routes.json"
body = json.loads(inventory.read_text())
names = {op["operation"] for op in operations}
body["operations"] = [op for op in body["operations"] if op["operation"] not in names]
body["operations"].extend({"client": "database", **op} for op in operations)
inventory.write_text(json.dumps(body, indent=2) + "\n")
if len(sys.argv) > 1:
    openapi = json.loads(Path(sys.argv[1]).read_text())
    current = root / "sdk/dotnet/contracts/terrabase/current-public-routes.json"
    body = json.loads(current.read_text())
    body["serverCommit"] = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    body["operations"] = [{"method": method.upper(), "path": path, "operation": operation["operationId"]}
                          for path, methods in sorted(openapi["paths"].items()) for method, operation in sorted(methods.items())
                          if method in ("get", "put", "post", "delete", "patch", "head")]
    current.write_text(json.dumps(body, indent=2) + "\n")
    subprocess.run([sys.executable, str(root / "sdk/python/scripts/refresh-contract.py"), sys.argv[1],
                    "TerraBase " + body["serverCommit"] + " /openapi/v1.json (local stack)"], check=True)
