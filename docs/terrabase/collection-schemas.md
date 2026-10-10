# Optional collection validation

Collections start schemaless. A caller with `documents:write` can attach, replace,
or detach a declared JSON Schema on the client TLS listener. These routes use v1:

| Method | Route | Behavior |
| --- | --- | --- |
| PUT | `/v1/projects/{project}/databases/{database}/collections/{collection}/schema` | Attach or replace; the body is the schema itself |
| GET | Same route | Read the schema (`documents:read`) |
| DELETE | Same route | Detach without removing documents |

Route decision (owner instruction, 2026-10-09): public APIs use v1. Breaking
changes edit v1 in place without compatibility aliases.
Schema clients use the paths published in OpenAPI and shown above. Tenant
resolution, project/database restrictions, authorization scopes, and listener
checks are shared with document operations and do not depend on the prefix.

Responses are `200 {"schema": …, "dialect":"https://json-schema.org/draft/2020-12/schema"}`,
or `200 {"schema":null}` when detached. Boolean schemas are accepted: `true`
accepts every otherwise valid document; `false` rejects every document write.
The existing collection creation request still accepts only `name`.

For example, PUT this schema to require a nonnegative integer quantity:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "required": ["quantity"],
  "properties": {"quantity": {"type": "integer", "minimum": 0}}
}
```

POST, PUT, both PATCH formats, and atomic document transactions validate the
complete resulting document. A rejected write changes neither the document nor
its commit offset. An invalid operation rolls back the entire client transaction.
Deletes and assertions do not evaluate schemas. Schema changes and document writes
share the database writer, and validation reads metadata within that transaction.

A document failing validation answers `400 schema_validation_failed`, for example:

```json
{
  "error": {
    "code": "schema_validation_failed",
    "message": "the document does not satisfy the collection schema",
    "details": [{"path": "/quantity", "keyword": "minimum"}]
  }
}
```

`path` is an escaped JSON Pointer, with the empty string identifying the root.
Errors in transactions also include the zero-based `error.op`. Responses retain
at most 16 distinct failures and 8 KiB of UTF-8 paths. Messages never include
document values. `terrabase_schema_validation_failures_total` counts rejected
local writes without tenant labels.

## Schema admission

The supported dialect is Draft 2020-12, also selected when `$schema` is absent.
Schemas must satisfy its meta-schema. Schema bodies are limited to 64 KiB,
15 nesting levels (leaving one level for the replicated metadata envelope),
4093 JSON values (reserving three for metadata), and 128 subschemas including reference expansion. The existing
document size, depth, value-count, and numeric admission limits still apply.

References must be acyclic local JSON Pointer fragments, such as `#/$defs/item`.
External references, `$id`, anchors, dynamic/recursive references, custom
vocabularies, and other dialects are rejected. Deprecated `definitions` and
`dependencies` are rejected; use `$defs`, `dependentSchemas`, or `dependentRequired`.
`pattern` and `patternProperties`
are also rejected because the selected library does not bound regex execution.
`format` remains an annotation, as permitted by the selected dialect. Restrictions
inspect schema locations; keyword-looking members inside `const`, `enum`, or
annotation values remain ordinary data. An invalid attachment answers
`400 invalid_schema` and leaves the previous schema unchanged. Oversized request
bodies answer `413 payload_too_large`.

Validation uses the MIT `JsonSchema.Net` 8.0.5 package with a private registry per
build, no tenant registrations in the global registry, and no remote schema
fetching. The newer 9.x NuGet binaries introduce commercial maintenance terms.
Private keyword handlers preserve exact JSON numeric equality, integer checks,
Unicode code-point lengths, and array error indices.
Schemas are compiled per operation; no schema cache is introduced. Evaluation
is synchronous on the database's existing writer thread; cancellation is checked
before and after each registered keyword, during schema/value/error traversal,
and inside the private keyword loops. Stock handlers still contain bounded inner
work between checkpoints. The host-scoped `terrabase.schema.keyword_evaluations`
counter records started evaluations by registered keyword name. Array uniqueness uses
exact hashes with deep equality for collisions. The admission ceilings bound inputs and reference expansion,
but do not promise a hard evaluation deadline.
Metadata decoding, compilation, and evaluation reserve process memory first.
Evaluation admission reserves potentially applied schema/instance pairs and their
actual pointer sizes, including boolean schemas. Unrelated document values do not
multiply selective property constraints. Control branches and unevaluated children
are reserved conservatively; handlers that materialize equality, numeric, or string
data also reserve for those values. Unused definitions are reserved for compilation.
Schema attachment preflight runs before opening the write transaction. Both
preflight and storage admission bound all referenced graphs before compiling or
evaluating private cancellation-aware meta-schemas. Meta evaluation reserves for
the root and distinct reference targets, including annotation values and copied
instance pointers;
`terrabase.schema.admission_keyword_evaluations` records preflight work. The storage
admission uses the local-write keyword counter. Insufficient capacity answers `503 storage_busy`
without committing the write.

## Persistence and replication

The collection's existing reserved `_collections` document stores `created_at`,
optional `schema`, and `schema_dialect`. Attachment and detachment update this
document atomically and preserve the collection's generation. Collection drops
remove the declared schema; recreation starts schemaless. No relational schema
migration or separate store is required. Backups, database copies, and collection
metadata replication carry the schema through the existing storage paths.

Validation is prospective at the local write boundary. Attaching a schema does
not scan or rewrite existing documents. Replicas replay committed bodies without
checking their current declared schema: an older accepted body must not block an
ordered journal when a newer schema arrived first. Collection schema metadata
merges under the existing metadata conflict rules. Concurrent schema changes and
writes on different members do not establish a globally serialized schema gate.

Deploy schema-capable binaries to every writer before enabling validation.
Earlier binaries ignore the new metadata fields and cannot enforce a schema;
there is no mixed-version enforcement shim. The Management API's existing
inferred-field `/schema` response continues to describe observed documents,
independently of declared validation schemas.
