---
title: Schema preview and request diagnostics
---

# Schema preview and request diagnostics

> **Pending rollout.** This guide describes the contract being implemented. Production availability and installed SDK support have not yet been verified; this is not a release notice.

## Preview existing documents

Preview checks existing documents against a proposed JSON Schema without changing documents or the active schema. Use project-scoped credentials with both `documents:read` and `documents:write` for the target resource; end-user credentials are not accepted. The dashboard's Schema preview UI is owner-only and protects submissions against cross-site request forgery (CSRF).

Send `POST /v1/projects/{project}/databases/{database}/collections/{collection}/schema/preview` with `Content-Type: application/json`. The body is the raw proposed schema, at most 64 KiB, for example `{"type":"object","required":["quantity"],"properties":{"quantity":{"type":"integer","minimum":0}}}`. Do not wrap it in a `schema` property. See the supported [schema rules](/guides/schema-validation).

The snake_case response is `{schema_hash, frontier, examined, valid, invalid, complete, next_cursor, diagnostics: [{id_hash, path_hash, keyword}]}`. `schema_hash` and `frontier` are SHA-256 digests encoded as 64 lowercase hex characters, binding the preview to the proposed schema and observed data frontier. The frontier conservatively covers the whole database; writes to another collection can also invalidate the preview.

Each page examines at most 100 documents and 1 MiB of document data; one oversized document may be admitted as a singleton page. `examined`, `valid` and `invalid` count documents on this page only. Accumulate counts across pages within the same preview. An invalid document can produce multiple diagnostics. At most 16 diagnostics appear per page, so they are not an exhaustive list of invalid documents. Diagnostics contain hashed document IDs, hashed paths and validation keywords, with no document values.

## Continue or restart

When `complete` is false, repeat the POST with `?cursor={next_cursor}`, URL-encoding the opaque cursor. Resend identical raw schema bytes; retain the same project, database, collection and region. Do not reformat the schema. Protected cursors are tied to the serving process/copy and cannot move between copies, even within one region, or survive a restart.

On `409 preview_stale` (intervening writes or changed schema), discard accumulated counts and restart without a cursor. On `400 invalid_cursor` (wrong process/copy, restart or tampering), also restart without a cursor. Stop after `complete: true`. The completed result is observational and may become stale after later writes; it does not lock data or reserve a schema change. Repair historical documents and preview again as needed.

[Applying a schema](/guides/schema-validation#attach-a-json-schema) remains separate, with prospective, local validation. Applying does not scan or rewrite historical documents, and preview does not create a globally serialized apply gate.

## Request IDs for support

The public trusted edge generates a canonical ULID (26 uppercase Crockford base32 characters), returning it as `X-Request-ID` on success and error responses. Keep the response ID for support; a client-supplied ID is not authoritative. That identity follows downstream calls and edge replay to retain the causal link. SDK errors expose it as JavaScript `requestId`, Rust `request_id()`, and Go v1's existing `RequestID`. Check the installed artifact's support; release availability is not established here. The .NET `RequestId` addition is in pending [SDK PR #64](https://github.com/TerraScale/ts-dotnet/pull/64), with release version still to be determined; do not assume installed .NET packages expose it.

Authorized dashboard Logs use `requestId` for correlation, distinct from the stored event `Id`. Logs show a best-effort view of the newest 100 records within 24 hours; `recordingLimited` and `truncated` disclose recording and result limits. A missing row does not prove a request never ran. An ID is support context, not lookup authority: access remains scoped to authorized tenants and resources, and knowing an ID grants no access to another tenant's diagnostics. Share the response ID and a brief description, keeping tokens, query values and document bodies out of support messages. For uncertain writes, follow [errors and safe retries](/guides/errors).
