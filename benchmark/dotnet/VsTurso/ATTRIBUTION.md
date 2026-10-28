# Server attribution in VsTurso

The original target remains server overhead <10% over the engine. Client round trips and health subtraction
cannot isolate it. The benchmark-only document-service decorator and ASP.NET Core activity listener report:

- **server**: hosting's HttpRequestIn activity duration. Header parsing and socket work before hosting starts are excluded.
- **engine**: the request's GetAsync and WriteConfirmedAsync wall times added together.
- **overhead**: server minus engine for each request, with its own independent p50/p99 sorting.
  The overhead/engine p50 column uses engine time as denominator. It is not a per-request ratio quantile.

All times are wall time, including waits. The in-process engine lane unwraps the decorator.
The capture uses the client's monotonic measurement window after a fixed 10 s warmup. Requests hosting starts before the
window, or finishes after it, are excluded. Missing HTTP attribution fails the run. Non-HTTP lanes, the health
floor and the separate three-node cluster have no server attribution, represented by null rather than zero.
A uniform reservoir keeps at most 16,384 paired full-width timings (256 KiB) per cell. Catalog hits are not measured.

Instrumentation perturbs results: activity/sample creation, engine wrappers, timestamps and recording consume CPU
and allocations. The stop callback records after Activity.Duration is fixed; its recording cost therefore affects
round trips or subsequent scheduling but is not all inside server duration. No strict uninstrumented upper bound
is claimed. Before/after comparisons must use identical instrumentation and token paths.

The synthetic API-key token includes key_id/key_gen and is registered with TestIssuer. This exercises TerraBase's
key authority and in-flight key watch against a test-only issuer; production key status remains cached for at most
2 seconds from the question's send time. The token has a 12-hour benchmark lifetime and project-scoped permissions;
no token or key is logged. Earlier keyless-token runs are diagnostic, not production-equivalent auth evidence.
The api-keyless lane mints a second token in the same run with identical subject, project, org, scope and expiry,
omitting only key_id/key_gen; the keyed token still performs setup. It is a keyed-minus-keyless diagnostic for the
key path's attributed cost (the header is also shorter), never production auth evidence or a production speedup.

The <10% target is unproven. Qualification requires three before/after repeats on the same quiet host through
`ts-heavy-any nice -n 19`, plus live gru/sjc/fra evidence. Hosting attribution excludes some Kestrel cost, so it
cannot by itself prove the complete transport-inclusive target. Preserve client round-trip and wire controls.

Only the point-document `api`, `api-exceptions`, `api-keyless`, `api-json` and `sdk` lanes receive attribution.
Query, batch, SQL and wire lanes do not time equivalent engine work through
this decorator and report no attribution.

The api-exceptions lane adds process-wide first-chance OperationCanceledException counts inside each monotonic
measurement window as Cell.CanceledExceptions. Counts include background work and repeated throws; they are not
assumed to belong to KeyWatch. The normal api lane has no exception handler and reports null for that count.
Use the same lane in both comparison arms; exception-event instrumentation itself perturbs the diagnostic lane.
