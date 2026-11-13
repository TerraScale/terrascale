import json
import sys
from pathlib import Path

source = json.loads(Path(sys.argv[1]).read_text())
prefix = "/v1/projects/{project}"
routes = [
    prefix + "/databases/{database}",
    prefix + "/databases/{database}/if-id/{expectedId}",
    prefix + "/deleted-databases",
    prefix + "/deleted-databases/{deletedDatabase}/restore",
]
paths = {}
for route in routes:
    methods = ["delete"] if "/databases/" in route else ["get"] if route.endswith("deleted-databases") else ["post"]
    paths[route] = {
        method: {key: value for key, value in source["paths"][route][method].items() if not key.startswith("x-")}
        for method in methods
    }
for item in paths.values():
    for operation in item.values():
        for response in operation["responses"].values():
            response.pop("headers", None)
            for media in response.get("content", {}).values():
                media.pop("example", None)
                media.pop("examples", None)
schemas = {}


def collect(value):
    if isinstance(value, dict):
        reference = value.get("$ref", "")
        if reference.startswith("#/components/schemas/"):
            name = reference.removeprefix("#/components/schemas/")
            if name not in schemas:
                schemas[name] = source["components"]["schemas"][name]
                collect(schemas[name])
        for child in value.values():
            collect(child)
    elif isinstance(value, list):
        for child in value:
            collect(child)


collect(paths)
result = {
    "openapi": source["openapi"],
    "info": {"title": "Database deletion and restoration", "version": "v1"},
    "paths": paths,
    "components": {
        "schemas": dict(sorted(schemas.items())),
        "securitySchemes": {
            name: {key: value for key, value in scheme.items() if key != "description"}
            for name, scheme in source["components"]["securitySchemes"].items()
        },
    },
}
Path(sys.argv[2]).write_text(json.dumps(result, indent=2) + "\n")
print(f"Exported {len(paths)} database lifecycle paths and {len(schemas)} schemas.")
