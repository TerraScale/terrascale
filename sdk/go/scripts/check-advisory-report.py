#!/usr/bin/env python3
"""Reject every govulncheck SARIF finding, including module-only notes."""
import json
from pathlib import Path
import sys


def unique_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def check(path):
    report = json.loads(Path(path).read_text(), object_pairs_hook=unique_keys)
    if report["version"] != "2.1.0" or len(report["runs"]) != 1:
        raise ValueError("Expected one complete SARIF 2.1.0 run")
    run = report["runs"][0]
    driver = run["tool"]["driver"]
    config = driver["properties"]
    if (driver["name"] != "govulncheck" or not driver["semanticVersion"]
            or config["scan_mode"] != "source" or config["scan_level"] != "symbol"
            or not config["go_version"] or not config["db_last_modified"]):
        raise ValueError("Missing or unexpected scanner configuration")
    rules, findings = driver["rules"], run["results"]
    if not isinstance(rules, list) or not isinstance(findings, list):
        raise ValueError("Missing rule or result arrays")
    rule_ids = {rule["id"] for rule in rules}
    finding_ids = {finding["ruleId"] for finding in findings}
    if rule_ids != finding_ids or len(rule_ids) != len(rules):
        raise ValueError("Incomplete advisory results")
    for finding in findings:
        # SARIF level describes reachability, not vulnerability severity.
        print(f'{finding["ruleId"]}: {finding["message"]["text"]}', file=sys.stderr)
    if findings:
        raise ValueError(f"{len(findings)} affected advisories; packaging/checks blocked")
    print("No affected advisories in the complete source and test graph.")


if __name__ == "__main__":
    try:
        check(sys.argv[1])
    except (OSError, ValueError, KeyError, TypeError, IndexError) as error:
        sys.exit(f"Advisory gate failed: {error}")
