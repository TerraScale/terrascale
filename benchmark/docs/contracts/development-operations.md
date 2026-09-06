# Cloud branch development operations

The public member API serves development operations only for live registered
branches (`parent` is present). Ordinary database authorization and placement
checks apply. Collection-scoped credentials cannot use these operations.

`GET /api/v1/databases/{name}/dev/logs?limit=100` requires whole-database read
permission and returns `{entries:[{sequence,action,atMillis}],truncated}`.
The response contains the newest bounded index lifecycle audit events, newest
first. It excludes document bodies, query text, field names, index identities,
credentials, and free-form server logs. The limit is 1–100. Polling clients can
deduplicate by sequence; `truncated` means older retained events were omitted.
This is a diagnostic snapshot, not a guaranteed complete event stream.

`POST /api/v1/databases/{name}/dev/faults` requires whole-database admin
permission and this JSON body:

```json
{"confirmBranch":"my-branch","fault":"unavailable","durationMs":30000,"maxOperations":1,"delayMs":0}
```

`confirmBranch` must exactly equal the registered branch name. `fault` is
`unavailable` or `latency`; duration is 1–30,000 ms and the operation budget is
1–100. Latency requires a delay of 1–1,000 ms; unavailable requires zero.
The response is `{active:true,expiresAtMillis:...}`. A new configuration replaces
the previous configuration on that branch. At most 128 branch configurations
can be active on a member.

The configured member applies the fault to subsequent authorized document and
SQL requests, before invoking their handlers. Unavailable returns HTTP 503 with
`error.code=development_fault_active`; latency delays admission. Faults do not
alter storage, replication, durability, or any other branch. Each application
rechecks branch identity and placement. Faults expire automatically, stop when
the operation budget is consumed, and clear on member restart. Configuration is
member-local: clients must target the same regional member to exercise it.

`DELETE /api/v1/databases/{name}/dev/faults` requires whole-database admin
permission and clears the configuration, returning
`{active:false,expiresAtMillis:0}`. An already admitted latency delay completes
within its one-second bound. Neither authentication nor administrative routes
are faulted, so cancellation remains available.

Enable, clear, application, and diagnostic-read operations are durably audited
in the member's internal control engine, with the actor label and bounded
configuration. The latest 100 records per branch are retained in its current
audit document. A failed audit commit refuses the operation. Audit history is
internal and is not included in the customer diagnostic response.
