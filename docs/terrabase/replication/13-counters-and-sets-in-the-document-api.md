# 13. Counters and sets in the document API

`PATCH …/documents/by-key?…` and transaction `patch` ops, `Content-Type: application/vnd.terrabase.ops+json`, as
today, extended. Every op names its target with `path`, a JSON Pointer, as `set`, `unset` and `append` always did:

```json
{"ops": [
  {"op": "inc",    "path": "/failed_logins", "by": 1},
  {"op": "add",    "path": "/consumed",      "value": "01JA…"},
  {"op": "remove", "path": "/roles",         "value": "admin"},
  {"op": "set",    "path": "/failed_logins", "value": 0}
]}
```

- `inc` (`by`: a non-zero int64) works on an absent or integer field of an existing document; a result outside the
  int64 range is a 409 `patch_conflict`. `add` and `remove` work on an absent or array field, with JSON scalar
  elements of at most 1 KiB (canonical JSON; `1` and `1.0` are different elements). All three take top-level fields
  only (400 `nested_lattice_path`). A target of the wrong type is a 409 `patch_conflict`. A set is bounded by the
  document limits, checked on the patched document: at most 4096 JSON values in a document, so the 10,000 elements
  of the design never bind.
- Reads are plain JSON: a counter is a number and a set is a sorted array. Queries, indexes and SQL see the same
  values.
- The response carries the materialized document after the operation (`document` in the write body and in each
  transaction `patch` result), so a caller can see a local reuse at once (section 17). Its ETag is computed from that
  stored document, not from the request.

**R5 as built.** The endpoint applies the ops in order to its local view of the document, which checks types and
limits and gives the value later JSON Pointer ops of the same request see. It hands the engine the list of entries
the ops write (`FieldOperation`): `inc`, `add` and `remove` as counter and dot entries, and each top-level field a
merge key or a plain op changed as one base with its value at that point (before the next `inc`, `add` or `remove` of
that field, otherwise at the end). The engine applies the list to the stored `DocumentState` at the request's stamp.
A merge-patch key and a plain op write their field even when the value does not change, so they take part in
last-writer-wins. The system database (not a lattice database until R10a) stores the endpoint's view.
