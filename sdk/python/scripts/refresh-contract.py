"""Refresh contracts/openapi-routes.json from the TerraBase OpenAPI document.

Usage: python scripts/refresh-contract.py [SOURCE] [ORIGIN]
SOURCE is the live API URL (default) or a path to a dumped /openapi/v1.json.
ORIGIN labels the producer in the manifest (e.g. a commit) when dumping offline.
"""

import json
import sys
import urllib.request
from datetime import date
from pathlib import Path

SOURCE = "https://api.terrascale.tech/openapi/v1.json"
AGENT_ROUTES = [
    "DELETE /v1/projects/{project}/databases/{database}/ttl",
    "POST /v1/terrascale/api-keys/self/scoped-keys",
    "POST /v1/terrascale/api-keys/{key}/rotate",
    "PUT /v1/projects/{project}/databases/{database}/ttl",
]

source = sys.argv[1] if len(sys.argv) > 1 else SOURCE
origin = sys.argv[2] if len(sys.argv) > 2 else source
if "://" in source:
    with urllib.request.urlopen(source, timeout=60) as response:
        doc = json.load(response)
else:
    doc = json.loads(Path(source).read_text())
routes = sorted(
    f"{method.upper()} {path}"
    for path, ops in doc["paths"].items()
    for method in ops
    if method in ("get", "post", "put", "patch", "delete")
)
out = Path(__file__).parent.parent / "contracts" / "openapi-routes.json"
out.write_text(json.dumps({"source": origin, "fetched": date.today().isoformat(),
                           "contract": "sdk/SQL-API.md#databases-for-agents", "contract_routes": AGENT_ROUTES,
                           "routes": sorted(set(routes + AGENT_ROUTES))}, indent=1) + "\n")
print(f"{len(routes)} routes -> {out}")
