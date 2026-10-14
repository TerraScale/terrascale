#!/usr/bin/env python3
"""Print only fixed diagnostic labels from buffered preview startup logs."""
import json
import re
import sys

events = {
    "Readiness check failed: {Cause}": "readiness failed",
    "Readiness check still failing: {Cause}": "readiness failing",
    "Storage and signing key bootstrap failed; retrying": "bootstrap failed",
}
causes = {
    "timed out", "storage reported unhealthy", "an earlier check is still running", "readiness evaluation is stale",
    "no signing keys: startup bootstrap pending (docs/auth/signing-keys.md)",
}
exceptions = {
    "System.Net.Http.HttpRequestException", "System.Net.Sockets.SocketException", "System.TimeoutException",
    "System.OperationCanceledException", "System.Threading.Tasks.TaskCanceledException",
    "TerraScale.Authentication.Contracts.ApiException",
    "TerraScale.DotNet.TerraBase.TerraBaseException", "TerraScale.DotNet.TerraBase.TerraBaseValidationException",
    "TerraScale.DotNet.TerraBase.TerraBaseAuthenticationException", "TerraScale.DotNet.TerraBase.TerraBaseForbiddenException",
    "TerraScale.DotNet.TerraBase.TerraBaseNotFoundException", "TerraScale.DotNet.TerraBase.TerraBaseConflictException",
    "TerraScale.DotNet.TerraBase.TerraBasePreconditionFailedException", "TerraScale.DotNet.TerraBase.TerraBaseUnavailableException",
}
sys.stdin.reconfigure(errors="replace")
# flyctl renders each buffered entry as indented JSON, so decode consecutive values rather than lines.
data, decoder, pos = sys.stdin.read(), json.JSONDecoder(), 0
counts = {"entries": 0, "unparsed": 0, "app_records": 0, "matched": 0}
while pos < len(data):
    while pos < len(data) and data[pos].isspace():
        pos += 1
    if pos == len(data):
        break
    try:
        record, pos = decoder.raw_decode(data, pos)
    except (ValueError, RecursionError):
        counts["unparsed"] += 1
        # Skip indented fragments and closing delimiters when recovering the next top-level entry.
        resync = re.compile(r"\n(?=[^\s}\]])").search(data, pos)
        pos = len(data) if resync is None else resync.end()
        continue
    counts["entries"] += 1
    try:
        if isinstance(record, dict) and isinstance(record.get("message"), str):
            record = json.loads(record["message"])
    except (ValueError, TypeError, RecursionError):
        continue
    counts["app_records"] += isinstance(record, dict) and isinstance(record.get("msg"), str)
    if not isinstance(record, dict) or not isinstance(record.get("msg"), str) or record["msg"] not in events:
        continue
    component = record.get("component")
    if not isinstance(component, str) or component not in {
        "TerraScale.Authentication.Features.Observability.ReadinessProbe",
        "TerraScale.Authentication.Features.SigningKeys.SigningKeyRotation",
    }:
        continue
    text = "\n".join(value for key in ("Cause", "error", "stack") if isinstance(value := record.get(key), str))
    summary = {
        "event": events[record["msg"]],
        "exceptions": sorted(set(re.findall(r"\b(?:System|TerraScale)\.[A-Za-z0-9_.]*Exception\b", text)) & exceptions),
        "http_status": sorted(set(re.findall(r"\b(?:HTTP\s+|[Ss]tatus(?: code)?[:=]?\s*)([45][0-9]{2})\b", text))),
    }
    if isinstance(record.get("Cause"), str) and record["Cause"] in causes:
        summary["cause"] = record["Cause"]
    counts["matched"] += 1
    print(json.dumps(summary))
print(json.dumps({"log_counts": counts}))
