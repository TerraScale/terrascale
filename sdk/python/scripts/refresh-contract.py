"""Refresh contracts/openapi-routes.json from the published TerraBase API."""

import json
import urllib.request
from datetime import date
from pathlib import Path

SOURCE = "https://api.terrascale.tech/openapi/v1.json"
AGENT_ROUTES = [
    "DELETE /v1/projects/{project}/databases/{database}/ttl",
    "POST /v1/projects/{project}/databases/{database}/move-home",
    "POST /v1/terrascale/api-keys/self/scoped-keys",
    "PUT /v1/projects/{project}/databases/{database}/ttl",
]

with urllib.request.urlopen(SOURCE, timeout=60) as response:
    doc = json.load(response)
routes = sorted(
    f"{method.upper()} {path}"
    for path, ops in doc["paths"].items()
    for method in ops
    if method in ("get", "post", "put", "patch", "delete")
)
out = Path(__file__).parent.parent / "contracts" / "openapi-routes.json"
out.write_text(json.dumps({"source": SOURCE, "fetched": date.today().isoformat(),
                           "contract": "sdk/SQL-API.md#databases-for-agents", "contract_routes": AGENT_ROUTES,
                           "routes": sorted(set(routes + AGENT_ROUTES))}, indent=1) + "\n")
print(f"{len(routes)} routes -> {out}")
