# Public TerraBase health probes

`v1.NewProbes(v1.Config{})` reads the anonymous public listener. `ProbeStatus` exposes exactly three required, non-null JSON string members: `status`, `version`, `region`. Private `storageFormat`, `revision` and `machine` properties have been removed from the Go DTO. Unknown response members are ignored. Required member names are case-sensitive; no closed status vocabulary, region catalog membership, or minimum string length is imposed. Invalid required fields return a bounded generic decoding error and never partially replace a decoded ProbeStatus.

| Go method | Method/path | Operation | Declared response |
|---|---|---|---|
| `Health(ctx)` | `GET /healthz` | `healthz` | 200, status body (`ok`) |
| `Ready(ctx)` | `GET /readyz` | `readyz` | 200 (`ready`) or 503 (`unavailable`), status body |
| `Backup(ctx)` | `GET /backupz` | `backupz` | 200 (`shipping`) or 503 (`unavailable`), status body; `Cache-Control: no-store` |

All three have no request body, parameters, credential scope or pagination and return `application/json`. The SDK sends JSON Accept. On a valid 503 body the SDK returns both `*ProbeStatus` and `*APIError`; malformed 503 status bodies return a nil result with the original APIError, whose Body preserves the response according to the existing transport contract. The existing generic probe path also handles liveness503 defensively; that is not a newly declared producer outcome. Other HTTP failures keep the existing errors. Ordinary status503 maps to `http_503` and is sent once. Shared transport transient-envelope retries, cancellation/deadlines, response bounds, redirects and caller HTTP-client ownership are unchanged. NewProbes still discards configured Token/TokenSource; this patch adds no credential behavior, caching or background work.

```go
probes, err := v1.NewProbes(v1.Config{})
if err != nil { return err }
status, err := probes.Ready(ctx)
// status may accompany a 503 APIError; inspect err before treating readiness as success.
```

Producer authority is `TerraScale/terrascale` revision `a3cbf8b1c73d304189a489fa8033d41f3b508de3`. Source SHA-256 pins:

| Source | SHA-256 |
|---|---|
| `src/TerraBase/Features/Health/HealthzEndpoint.cs` | `98edd92dee709421daa9323ac49e17f8b7400c06643334d2881a48519b9accf7` |
| `src/TerraBase/Features/Health/ReadyzEndpoint.cs` | `ba821267100d7ea6ee71d9873ec8aa3f9437bdde3652f59c2b4afd3ddee317dc` |
| `src/TerraBase/Features/Backup/BackupHealthEndpoint.cs` | `12dac1a307d1c08bf48323df210d886b76470470a14344a685bb317b0a31533a` |

HealthzEndpoint StatusBody.For emits only the public three fields on the client listener; its additional nullable metadata is restricted to the credentialed internal listener. The three endpoint Configure/Handle methods own paths, operation names, anonymous security, status outcomes and cache behavior. At additional immutable revision `a095400a19bcc432ac27d0afaca4eba042b636a7`, the same HealthzEndpoint.cs is SHA-256 `afe88c688f0f605069dac490edb22b2c2155cadc0204e454bcabe28521c276ac`, and its StatusBody contains only those three required strings. Local source snapshots were read only after matching these accepted hashes; no checkout revision was assumed authoritative.

These are supplemental source contracts. No generated OpenAPI/route manifest was regenerated or executed. Historical SDK manifests are not evidence of fresh generated parity. Focused fake-HTTP tests do not establish live Go qualification; coordinator structural evidence of three public string keys is prior observation, not a live test of this patch.
