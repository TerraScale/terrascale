# Document API response documentation

The v1 OpenAPI document includes the existing `413 payload_too_large` and
`415 unsupported_media_type` responses for collection creation, document writes,
queries, transactions, collection JSON Schema, schema preview, TTL and access
rules. These are documentation additions; request limits, storage and runtime
behavior are unchanged.

`GET …/documents/by-key?id=…` documents its optional `If-Match` and
`If-None-Match` headers. `If-Match` is evaluated first with strong comparison;
a failed condition returns `412 precondition_failed`. `If-None-Match` compares
weakly against a live document and returns a bodyless `304` with its `ETag` on
a match. Malformed conditional headers return `400`. A deleted document's `404`
can carry its tombstone's `ETag` for conditional recreation.

Index-list documentation includes both `building` and `active` states: vector
indexes can build in the background; B-tree and text indexes build before their
creation response.

This change adds response declarations and header descriptions without changing
API compatibility, data formats, persistence, authentication or authorization.
The Fast integration suite fetches the real application's OpenAPI document over
HTTP and checks the response paths, media types and optional conditional headers.
