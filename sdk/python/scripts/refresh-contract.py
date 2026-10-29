"""Refresh contracts/openapi-routes.json from the published TerraBase API."""

import json
import urllib.request
from datetime import date
from pathlib import Path

SOURCE = "https://api.terrascale.tech/openapi/v1.json"

with urllib.request.urlopen(SOURCE, timeout=60) as response:
    doc = json.load(response)
routes = sorted(
    f"{method.upper()} {path}"
    for path, ops in doc["paths"].items()
    for method in ops
    if method in ("get", "post", "put", "patch", "delete")
)
out = Path(__file__).parent.parent / "contracts" / "openapi-routes.json"
out.write_text(json.dumps({"source": SOURCE, "fetched": date.today().isoformat(), "routes": routes}, indent=1) + "\n")
print(f"{len(routes)} routes -> {out}")
