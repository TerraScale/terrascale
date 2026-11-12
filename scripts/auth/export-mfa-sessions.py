import json
import sys
from pathlib import Path

schema = json.loads(Path(sys.argv[1]).read_text())
path = "/v1/{project}/step-up"
entry = schema["paths"][path]
schemas = schema["components"]["schemas"]
names = set()


def refs(value):
    if isinstance(value, dict):
        ref = value.get("$ref")
        if ref and ref.startswith("#/components/schemas/"):
            name = ref.rsplit("/", 1)[1]
            if name not in names:
                names.add(name)
                refs(schemas[name])
        for child in value.values():
            refs(child)
    elif isinstance(value, list):
        for child in value:
            refs(child)


refs(entry)
out = {
    "openapi": schema["openapi"],
    "info": {"title": "Project step-up", "version": "v1"},
    "servers": schema["servers"],
    "paths": {path: entry},
    "components": {
        "schemas": {name: schemas[name] for name in sorted(names)},
        "securitySchemes": schema["components"]["securitySchemes"],
    },
}
Path(sys.argv[2]).write_text(json.dumps(out, indent=2) + "\n")
