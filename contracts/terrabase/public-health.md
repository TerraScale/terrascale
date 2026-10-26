# Public health status

Authority: [public-health-provenance.json](public-health-provenance.json), producer
`a3cbf8b1c73d304189a489fa8033d41f3b508de3`. Anonymous client-listener probes have
exactly three required, non-null string fields: `status`, `version`, `region`.

| GET route | Operation | Declared outcomes |
|---|---|---|
| `/healthz` | `healthz` | 200 `ok` |
| `/readyz` | `readyz` | 200 `ready`; 503 `unavailable` |
| `/backupz` | `backupz` | 200 `shipping`; 503 `unavailable` |

No request body, credentials, query parameters, pagination or checkpoint.
Responses use `application/json`. Readiness and backup unavailability are status
bodies, not error envelopes. Existing SDK probe handling preserves 200/503 for
all three paths; liveness 503 preservation is defensive handling, not a newly
declared producer outcome. Other statuses use the existing error path, including
421 for an explicit mismatched region pin. There is no SDK retry, cache or poller.

```csharp
using var handler = TerraBaseHttp.CreateHandler();
using var http = new HttpClient(handler);
var health = new TerraBaseHealthClient(http);
var readiness = await health.GetReadinessAsync(cancellationToken);
// Inspect readiness.StatusCode and readiness.Body.Status together.
```

`TerraBaseStatus(Status, Version, Region)` models the public contract only.
Storage format, revision and machine properties were removed: callers cannot
require internal build metadata on public probes. Unknown response members are
ignored, while missing/null or incorrectly typed public fields fail safely.
Existing source-generated required-constructor and nullable validation remains.
Caller ownership, header refusal, cancellation, deadlines and response limits
are unchanged. This SDK does not acquire peer credentials or reverse redaction.

Producer endpoint metadata, source-generation registration and API-reference
parity tests were read, not built or executed. The retained historical route
inventory uses an older `status` liveness identity and does not define this DTO.
Producer ApiExamples lacks an explicit backupz example; no fresh generated
OpenAPI/full route manifest is claimed by this source-only supplemental pin.
