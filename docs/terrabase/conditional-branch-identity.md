# Conditional branch source identity

`POST /v1/projects/{project}/databases/{database}/branches/if-id/{expectedId}`
creates a branch only when the database resolved by the source **name** has the
expected immutable ULID. Its OpenAPI operation ID is `branchDatabaseIfId`.
The body is the existing `NameRequest`: `{"name":"preview"}`. Success is
`201 Created` with the existing managed database body and a `Location` naming
the child. `branchedFrom` is the admitted source ULID; later writes are independent.

Project authentication, `databases:write`, project binding and signed database
restrictions apply before the identity comparison. A database-restricted token
cannot create a sibling database and receives an opaque `404`. An authorized
request with a malformed expected ID receives `400 invalid_request`; IDs must
be canonical uppercase ULIDs. A different admitted source ID receives
`409 database_identity_changed`, message `the database identity changed`,
without disclosing either ID. This refusal happens before placement checks,
storage preparation or copying. Errors are uncached.

The handler carries the admitted ULID by value into the existing branch service;
it never resolves the source name again. If that source disappears before the
engine copies it, the operation can fail with an opaque `404 unknown_route`.
It cannot switch to a replacement database that reuses the name. This is an
identity precondition, not a guarantee that concurrent deletion or other failures
cannot interrupt copying. Existing quota, conflict, unavailable and
`421 wrong_region` responses, including home-region headers, still apply.

This is an additive v1 route. Existing `POST …/branches` behavior is unchanged.
An older server returns `404` for the conditional route: SDKs, CLI and preview
automation must fail closed, never retry through the unconditional route.
Consumers must carry the selected source ULID into this request, rather than
checking it in a separate GET before branching by name. SDK/CLI support must
ship before preview actions adopt it. Expected-ID DELETE support is a separate
SDK/CLI prerequisite for safe preview cleanup; this route does not implement it.
No generic rename change, storage-format change or migration is required.
