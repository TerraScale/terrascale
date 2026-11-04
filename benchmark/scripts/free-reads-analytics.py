import json
import sys
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

token = sys.stdin.read().strip()
if not token:
    raise SystemExit("Analytics credential missing")

rows = [json.loads(line) for line in Path(sys.argv[1]).read_text().splitlines()]
query = """query($account: String!, $from: Time!, $to: Time!) {
  viewer { accounts(filter: {accountTag: $account}) {
    r2OperationsAdaptiveGroups(limit: 100, filter: {
      bucketName: "ts-terrabase-shapes-prod", datetime_geq: $from, datetime_leq: $to
    }) { dimensions { actionType } sum { requests } }
  } }
}"""
for row in rows:
    if row.get("type") != "cell":
        continue
    start = datetime.fromisoformat(row["start"]).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    end = datetime.fromisoformat(row["end"])
    end = datetime.fromtimestamp(int(end.timestamp()) + 1, timezone.utc).isoformat().replace("+00:00", "Z")
    payload = {"query": query, "variables": {"account": "6cd3f3639e928077593d9b470765efeb", "from": start, "to": end}}
    request = urllib.request.Request("https://api.cloudflare.com/client/v4/graphql", data=json.dumps(payload).encode(),
                                    headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"})
    record = {"type": "r2", "region": row["region"], "shape": row["shape"], "readers": row["readers"], "repeat": row["repeat"], "from": start, "to": end}
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            result = json.load(response)
        if result.get("errors"):
            record["status"] = "graphql_error"
        else:
            groups = result["data"]["viewer"]["accounts"][0]["r2OperationsAdaptiveGroups"]
            actions = {item["dimensions"]["actionType"]: item["sum"]["requests"] for item in groups}
            record.update(status="available" if groups else "no_analytics_rows", actions=actions,
                          get_object=actions.get("GetObject", 0) if groups else None)
    except (OSError, ValueError, KeyError, IndexError):
        record["status"] = "unavailable"
    print(json.dumps(record), flush=True)
