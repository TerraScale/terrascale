import json
import sys

METHODS = ("get", "put", "post", "delete", "patch", "head", "options", "trace")
SLUG = "/terrascale/oauth/"


def load(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def operations(spec):
    found = {}
    for path, item in spec.get("paths", {}).items():
        for method in METHODS:
            if method in item:
                found[(method, path)] = (item, item[method])
    return found


def parameters(spec, item, operation):
    named = set()
    for parameter in item.get("parameters", []) + operation.get("parameters", []):
        if "$ref" in parameter:
            parameter = spec["components"]["parameters"][parameter["$ref"].rsplit("/", 1)[1]]
        named.add((parameter["name"], parameter["in"]))
    return named


def served_path(path):
    return "/{slug}/" + path.removeprefix("/terrascale/") if path.startswith(SLUG) else path


def main(served_file, contract_file):
    served_spec = load(served_file)
    contract_spec = load(contract_file)
    served = operations(served_spec)
    contract = operations(contract_spec)

    problems = []
    for (method, path), (item, operation) in sorted(contract.items()):
        if operation.get("x-planned"):
            continue
        label = f"{method.upper()} {path} ({operation.get('operationId')})"
        actual = served.get((method, served_path(path)))
        if actual is None:
            problems.append(f"in the contract but not served: {label}")
            continue
        if actual[1].get("operationId") != operation.get("operationId"):
            problems.append(f"operationId differs: {label} is served as {actual[1].get('operationId')}")
        promised = parameters(contract_spec, item, operation)
        offered = parameters(served_spec, *actual)
        if path.startswith(SLUG):
            offered.discard(("slug", "path"))
        for name, location in sorted(offered - promised):
            problems.append(f"served parameter missing from the contract: {label} {location} {name}")
        for name, location in sorted(promised - offered):
            problems.append(f"contract parameter not served: {label} {location} {name}")

    promised_paths = {(method, served_path(path)) for method, path in contract}
    for method, path in sorted(served):
        if path.startswith("/v1/terrascale/") and (method, path) not in promised_paths:
            problems.append(f"served but missing from the contract: {method.upper()} {path} ({served[(method, path)][1].get('operationId')})")

    for problem in problems:
        print(problem, file=sys.stderr)
    if problems:
        print(f"{len(problems)} OpenAPI contract drift problem(s); update src/TerraScale.Authentication/platform-api.openapi.yaml", file=sys.stderr)
        return 1

    print(f"OpenAPI contract matches the served operations ({len(contract)} in the contract)")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print("usage: check-openapi-drift.py <served-openapi.json> <bundled-contract.json>", file=sys.stderr)
        sys.exit(2)
    sys.exit(main(sys.argv[1], sys.argv[2]))
