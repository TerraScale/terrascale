import json
import sys

METHODS = ("get", "put", "post", "delete", "patch", "head", "options", "trace")


def operations(document):
    found = {}
    for path, item in document.get("paths", {}).items():
        for method in METHODS:
            if method in item:
                found[(method, path)] = item[method]
    return found


def served_path(path):
    return "/{slug}/" + path.removeprefix("/terrascale/") if path.startswith("/terrascale/oauth/") else path


def main(served_file, contract_file):
    with open(served_file, encoding="utf-8") as handle:
        served = operations(json.load(handle))
    with open(contract_file, encoding="utf-8") as handle:
        contract = operations(json.load(handle))

    problems = []
    for (method, path), operation in sorted(contract.items()):
        if operation.get("x-planned"):
            continue
        actual = served.get((method, served_path(path)))
        if actual is None:
            problems.append(f"documented but not served: {method.upper()} {path} ({operation.get('operationId')})")
        elif actual.get("operationId") != operation.get("operationId"):
            problems.append(f"operationId differs: {method.upper()} {path} documents {operation.get('operationId')}, serves {actual.get('operationId')}")

    documented = {(method, served_path(path)) for method, path in contract}
    for method, path in sorted(served):
        if path.startswith("/v1/terrascale/") and (method, path) not in documented:
            problems.append(f"served but not documented: {method.upper()} {path} ({served[(method, path)].get('operationId')})")

    for problem in problems:
        print(problem, file=sys.stderr)
    if problems:
        print(f"{len(problems)} OpenAPI contract drift problem(s); update src/TerraScale.Authentication/platform-api.openapi.yaml", file=sys.stderr)
        return 1

    print(f"OpenAPI contract matches the served document ({len(contract)} documented operations)")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print("usage: check-openapi-drift.py <served-openapi.json> <bundled-contract.json>", file=sys.stderr)
        sys.exit(2)
    sys.exit(main(sys.argv[1], sys.argv[2]))
