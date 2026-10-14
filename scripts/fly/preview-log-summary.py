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
for line in sys.stdin:
    try:
        record = json.loads(line)
        if isinstance(record, dict) and isinstance(record.get("message"), str):
            record = json.loads(record["message"])
    except (ValueError, TypeError):
        continue
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
    print(json.dumps(summary))
