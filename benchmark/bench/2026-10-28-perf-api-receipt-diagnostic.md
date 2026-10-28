# Retained tenant receipt diagnostic, 2026-10-28

Read-only follow-up; no new receipt request, deletion continuation, tenant, latency
measurement or production change. The retained tenant is not reconciled.

At 08:07:12 UTC, an admitted ts-orch NetBird SSH inspection read the existing
gru auth task's bounded stdout/stderr files. Filtering occurred on-host for the
two owned failed request IDs; only fixed metadata, type/frame presence and
allowlisted codes were exported. No headers, hashes, user IDs, receipt capabilities,
server exception messages or credentials were exported.

| Historical receipt time UTC | Auth Code | SDK unavailable type present | SDK OpenAsync frame present | Known cause code in exception text |
| --- | --- | --- | --- | --- |
| 06:57:52.121602 | temporarily_unavailable | true | true | none observed |
| 06:58:12.090154 | temporarily_unavailable | true | true | none observed |

The [sanitized raw observation](2026-10-28-perf-api-receipt-fields-0807.json)
retains image identity, observation time and fields inspected. It does not retain
the underlying refusal code. `AccountDeletionStatusEndpoint` maps HTTP/timeout
exceptions to the generic auth 503. `ErrorEnvelopeMiddleware` already logs 5xx
exceptions through `SafeLogging`'s type/frame scrubber; a second log is not needed
merely to establish that a storage exception occurred.

In SDK source `36d114877a69d18fba33b27ad362bd3e68e9e7ff`, the unavailable
exception is constructed for HTTP 503 responses. The deployed image's SDK commit
is unverified, so this is a source-backed interpretation, not exact deployed-line
attribution or proof of a particular TerraBase refusal. No transport, capacity,
catalog, auth or replication cause is established. Preserve the recovery state;
do not use this unchanged historical evidence to justify another receipt retry.
