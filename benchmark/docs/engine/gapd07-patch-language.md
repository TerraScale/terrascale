# GAP-D07: the typed document patch vocabulary (patch-language decision)

Status: decision record for Forgejo ticket TerraBaseV2#60 (GAP-D07), part of
parent adjudication `PD-2026-08-26-14`, recorded 2026-08-27 against the
`fleet/gapd07-2` worktree of `develop` @ `5f8a04b`.

Parity row: PB-2014 `document-patch-operations` (core), currently
`not-implemented` in `docs/parity/ps-01-documents-sweep.md`. This packet adds
the patch language and its engine-seam application; the row's own status
record lives in the parity overlays, which are outside this packet's write
scope. PB-4037 `patch-by-query` is explicitly **not** this packet's: the
parent adjudication routes query-driven patching to GAP-Q10, and this record
builds only document-targeted patch operations.

V1 evidence identity: `fa99b9257776dbe625e6a2388c74c9ce916637ee` under
`.worktrees/terrabase-v1-fa99b925-readonly` (read-only). Every V1 file
reference below is inside that pinned tree.

Test evidence: `src/engine/patch_tests.rs` (the in-crate contract tests:
construction refusals, application semantics, typed refusals, no-partial-write
proof, determinism) and `tests/engine/document_patch_contract.rs` (public
construction and vocabulary; the end-to-end patch-then-read chains live in
`src/engine/patch_tests.rs`). `TenantScope` has no
public constructor by design, so the seam-level proofs run in-crate, following
the convention the engine, query, and session seams already record.

Disposition vocabulary: **closed-here** (implemented and pinned),
**pinned-compatible** (no code change needed; the pin proves V1
compatibility), **deferred** (recorded residual with evidence and a named
gate), **accepted-difference** (a deliberate V2 shape, recorded).

Hard constraint: charter `PD-2026-08-26-08` D2 — no script execution, ever.
V1's primary patch surface is a JavaScript script body; that entire surface is
out before any other consideration, and the vocabulary below is what replaces
it.

## D1 — the patch language is a closed typed operation set

V2's patch language is not a language. It is a closed set of typed,
path-addressed operations — `Add`, `Replace`, `Remove`, `Increment`, and one
compound form `AddOrPatch` — carried as `src/engine/patch.rs` values, applied
by one engine-seam function over one transaction, with every refusal a typed
error and every application deterministic. There is no script body, no
expression evaluation, no string-form path parsing, and no surface grammar in
this packet: a patch is data, checked by construction.

This follows the seam's established shape. The includes clause
(`docs/query/gapq07-includes-contract.md`) and the loads clause are closed
typed vocabularies validated at construction; TQL composes above them. The
patch vocabulary takes the same position under whatever surface spelling a
later wave exposes — host, CLI, MCP (charter `PD-2026-08-26-07` requires every
typed operation be MCP-exposable, which a closed operation set is and a
script body never can be).

## The V1 patch API parity inventory

V1 exposes four distinct patch surfaces plus one session convenience family.
The inventory below is read from the pinned tree.

### V1 surface 1 — scripted single-document patch (the primary surface)

`PatchOperation(id, changeVector, patch, patchIfMissing,
skipPatchIfChangeVectorMismatch)`
(`src/TerraBase.Client/Documents/Operations/PatchOperation.cs:64-88`)
executes a `PatchRequest` against one document. `PatchRequest` is a
JavaScript function body plus a values dictionary
(`src/TerraBase.Client/Documents/Operations/PatchRequest.cs:13-24`); the
constructor refuses a null or whitespace `Script`
(`PatchOperation.cs:76-81`). The server runs the script through its JS engine
(`src/TerraBase.Server/Documents/Patch/`, `ScriptedPatchTests.cs`).

Observable contract parts:

| V1 behavior | Cite | V2 disposition |
| --- | --- | --- |
| Arbitrary JavaScript body, `this.<path>` addressing, `args` values | `PatchRequest.cs:19-24` | **rejected** — charter `PD-2026-08-26-08` D2 bans script execution; replaced by the typed vocabulary (D1) |
| `changeVector` guards the patch against a stale target | `PatchOperation.cs:55-63` | **closed-here** — the engine seam's `ExpectedVersion` (`src/types.rs:382-390`) carries this discipline; stale targets refuse typed before any write (D5) |
| `patchIfMissing` runs when the document does not exist | `PatchOperation.cs:62` | **closed-here** as the `AddOrPatch` compound (D4): patch if present, create with a typed document value if absent |
| `skipPatchIfChangeVectorMismatch` turns a stale target into a skip | `PatchOperation.cs:63`, status `Skipped` (`src/TerraBase.Client/Documents/Operations/PatchResult.cs:24-26`) | **accepted-difference** — V2 refuses stale targets with a typed error instead of silently skipping; a caller wanting skip semantics observes the refusal and decides. A silent skip is a write-path branch that reports success while changing nothing, which is the shape the typed seam exists to avoid |
| Status set `DocumentDoesNotExist` / `Created` / `Patched` / `Skipped` / `NotModified` | `PatchResult.cs:9-31` | **closed-here** as `PatchOutcome::Applied` / `Created` / `NotModified` — the two skipped/missing statuses become typed refusals (D5), so they are outcomes no longer reachable silently |

### V1 surface 2 — JSON-Patch (RFC 6902) on one document

`JsonPatchOperation(id, JsonPatchDocument)`
(`src/TerraBase.Client/Documents/Operations/JsonPatchOperation.cs:86-120`)
posts a list of RFC 6902 operations — `op`/`path`/`from`/`value`, the set
`add`/`remove`/`replace`/`move`/`copy`/`test`
(the command set at `JsonPatchCommand.cs:396-403`, client op-name checks at `JsonPatchOperation.cs:175-177`) — to `/databases/*/json-patch`, which the
server parses (`JsonPatchCommand.Parse`,
`src/TerraBase.Server/Documents/TransactionMerger/Commands/JsonPatchCommand.cs:406-458`)
and applies inside the transaction merger
(`src/TerraBase.Server/Documents/Handlers/JsonPatchHandler.cs:16-31`).

Observable contract parts:

| V1 behavior | Cite | V2 disposition |
| --- | --- | --- |
| Operations execute sequentially, in list order, later ops seeing earlier effects | `JsonPatchOperation.cs:35`/`:48` (client remark), `JsonPatchCommand.cs:44-85` (server loop, re-read after every op) | **pinned-compatible** — deterministic vector-order application is D2 |
| Missing document fails closed | `JsonPatchCommand.cs:37-40` | **pinned-compatible** — `PatchError::MissingTarget` (D5) |
| Path is an escaped string: split on `/`, `~0`→`~`, `~1`→`/` | `JsonPatchCommand.cs:432-458` | **rejected** — string-pointer addressing is precisely what the typed seam refuses (D3, alternative A) |
| Array addressing: integer index, `-` appends, index must be in bounds | `JsonPatchCommand.cs:179-183`, `:345-355` | **closed-here** as typed segments: `Index(u32)` and one `Append` segment (D3); in-bounds rules kept |
| `add` inserts at an index (positional shift), object `add` creates-or-overwrites the member | `JsonPatchCommand.cs:339-366` | **pinned-compatible** — same member semantics, same insert-in-bounds rule (D3) |
| `replace`/`remove` require the target to exist | `JsonPatchCommand.cs:317-320`, `:368-371` (both call `TryGetMember` first, which throws when absent) | **pinned-compatible** — typed `PathNotFound` refusal (D5) |
| `move`/`copy` carry a second `from` path | `JsonPatchCommand.cs:61-66`, `:305-315` | **deferred** — composable from `Add` + `Remove` by the caller; see D6 |
| `test` asserts deep value equality inside the write | `JsonPatchCommand.cs:67-69`, `:198-224` | **rejected** — see D3 alternative A: it duplicates the version guard with a weaker, coercion-prone one |
| One document `Put` after the loop, only when modified | `JsonPatchCommand.cs:87-93` | **pinned-compatible** — the V2 application stages exactly one `Transaction::put` and none when the value is unchanged (D5) |
| No change-vector guard on this surface at all | `JsonPatchCommand.cs:25-31` (constructor takes no change vector, contrast `PatchOperation.cs:64`) | **accepted-difference** — every V2 write carries `ExpectedVersion`; the unguarded shape is not offered |
| Numeric equality across ten CLR numeric shapes under `test` | `JsonPatchCommand.cs:262-303` | moot with `test` rejected; recorded as evidence of the coercion the typed seam avoids |

### V1 surface 3 — patch-by-query

`PatchByQueryOperation(queryToUpdate, options)`
(`src/TerraBase.Client/Documents/Operations/PatchByQueryOperation.cs:19-66`)
patches every document a query matches, with its own option set
(`QueryOperationOptions`). **Not this packet's scope**: the parent
adjudication routes the patch half of GAP-Q10 (#65) through this vocabulary —
query-driven patching will reuse these typed operations as its per-document
body and must not grow a second patch language. The parity row is PB-4037.

### V1 surface 4 — session convenience patches

The session's `Increment` compiles to a one-line script
`this.<path> = this.<path> ? this.<path> + args.val : args.val`
(`src/TerraBase.Client/Documents/Session/InMemoryDocumentSessionOperations.Patch.cs:44-51`)
— that is, increment treats a missing field as zero. `AddOrIncrement` /
`AddOrPatch` attach a `CreateIfMissing` document so a missing *document*
becomes a create (`InMemoryDocumentSessionOperations.Patch.cs:64-111`, and
`PatchCommandData.CreateIfMissing`,
`src/TerraBase.Client/Documents/Commands/Batches/PatchCommandData.cs:44`).
The observable arithmetic — `LoginCount = 1`, increment by 3, result 4 — is
pinned by `test/TerraBase.SlowTests/Core/Session/AddOrPatch.cs:157-196`.

| V1 behavior | Cite | V2 disposition |
| --- | --- | --- |
| Increment on a missing field sets the delta | `InMemoryDocumentSessionOperations.Patch.cs:44-51` | **pinned-compatible** — `Increment` on an absent path writes the delta (D3) |
| Increment on any JS value coerces (`"5" + 3` is `"53"` under the script's `+`) | same script text, `:46` | **accepted-difference** — `Increment` on a non-`Integer` value refuses typed (`PatchError::TypeMismatch`); the string-concatenation outcome is a scripting artifact, not a contract |
| `AddOrPatch`/`AddOrIncrement` create the document when missing | `InMemoryDocumentSessionOperations.Patch.cs:64-111` | **closed-here** as `AddOrPatch` (D4) |

## D2 — deterministic application order

A `DocumentPatch` is a `Vec<PatchOperation>` applied strictly in vector
order, each operation applied to the result of the previous one, exactly as
V1's server loop applies each command and re-reads the document between
commands (`JsonPatchCommand.cs:44-85`). No operation is reordered, deduplicated,
sorted, or merged; two operations touching the same path apply twice in the
given order. Determinism is therefore by construction: one patch value plus
one document value has exactly one result value.

Idempotency follows per operation kind and is tested as such:
`Add`, `Replace`, and `Remove` are idempotent (applying the same patch twice
yields the same document value — `Replace` twice sets the same value,
`Remove` twice refuses on the second application with `PathNotFound`, which
is the fail-closed form of the same determinism). `Increment` is **not
idempotent by design** — it is a signed delta, and applying it twice adds the
delta twice; the contract says so and the test pins it.

## D3 — the path model and the operation semantics

A `PatchPath` is built from typed segments only — there is deliberately no
string form, no parse function, and no escape alphabet at this seam (that is
half of the JSON-Patch rejection below):

| Segment | Meaning | Bounds |
| --- | --- | --- |
| `Field(String)` | one object member by exact name | non-empty, at most `MAX_PATCH_FIELD_BYTES` bytes, no control characters |
| `Index(u32)` | one array position | must be `< len` for insert/replace/remove (V1 parity, `JsonPatchCommand.cs:350-355`); `Add` at an index inserts at that position |
| `Append` | array end | only valid as the last segment of an `Add` (V1's `-`, `JsonPatchCommand.cs:345-349`) |

Closed bounds, validated at construction before any data access:

| Constant | Value | Bounds |
| --- | --- | --- |
| `MAX_PATCH_OPERATIONS` | 32 | operations per patch |
| `MAX_PATCH_PATH_SEGMENTS` | 8 | segments per path, matching `MAX_INCLUDE_PATH_SEGMENTS` |
| `MAX_PATCH_FIELD_BYTES` | 255 | bytes per field segment, matching `MAX_IDENTIFIER_BYTES` |

Operation semantics over a `DocumentValue`:

- **`Add { path, value }`** — create-or-overwrite at `path`. Object member:
  create or overwrite (V1 `Add`, `JsonPatchCommand.cs:358-361`). Array index
  in bounds: insert at the position (V1 `:350-355`). `Append`: add at the
  end. **Nested creation**: `Add` materializes missing intermediate *objects*
  when the next segment is a `Field`; it never materializes an array (an
  `Index`/`Append` segment under a missing or non-array parent refuses with
  `PatchError::TypeMismatch`). Auto-vivifying objects but never arrays keeps
  creation unambiguous — an array has no canonical implicit length — and
  matches the strict walk V1 performs for every non-final segment
  (`JsonPatchCommand.cs:147-165`, which throws when an intermediate is
  absent) in spirit while making the common create-shape expressible without
  a read-modify-write round trip. Recorded as **closed-here** with the
  divergence noted: V1 refuses a missing intermediate; V2 creates objects
  only.
- **`Replace { path, value }`** — the target must exist (`PathNotFound`
  otherwise); sets the value. V1 parity (`JsonPatchCommand.cs:368-371`).
- **`Remove { path }`** — the target must exist; removes the member or the
  array element (shifting later elements down, V1 `:317-337`). Removing the
  root (an empty path) is a construction refusal: removing a document is
  `Transaction::delete`, a different typed operation with its own
  `ExpectedVersion` discipline.
- **`Increment { path, delta: i64 }`** — target absent: writes `delta` (V1
  session parity, `InMemoryDocumentSessionOperations.Patch.cs:44-51`).
  Target `Integer(n)`: `n + delta` under checked arithmetic —
  `PatchError::ArithmeticOverflow` on wrap, never a silent wrapping add.
  Target any other kind (including `Number(f64)`, `String`, `Null`):
  `PatchError::TypeMismatch` (the accepted-difference above).

## D4 — `AddOrPatch`

`AddOrPatch { if_missing: Document, patch: DocumentPatch }` is the one
compound form: when the target document exists it applies the patch; when it
does not, it stores `if_missing` unchanged (V1's `patchIfMissing` /
`CreateIfMissing` shape, `PatchOperation.cs:62`,
`PatchCommandData.cs:44`). The create leg runs under
`ExpectedVersion::New` — it is a create — and the patch leg under the
caller's `ExpectedVersion`. The outcome distinguishes `Created` from
`Applied` (V1 `PatchResult.cs:15-22`). There is no `AddOrIncrement` compound:
`AddOrPatch` with a single `Increment` operation is exactly that, typed.

## D5 — typed errors, the version discipline, and fail-closed application

`apply_document_patch(transaction, collection, id, expected, patch)` is the
seam function. It:

1. reads the target through `Transaction::read_in_collection` — exact-key
   resolution and visibility of this transaction's own staged writes;
2. resolves the `ExpectedVersion` discipline the same way
   `Transaction::put` carries it (`src/engine/mod.rs:1050-1061`): a
   mismatch with the observed version refuses with
   `PatchError::StaleTarget` **before anything is staged**, and the commit
   seam re-checks the expectation against the published view, so a target
   that changes between read and commit is a typed
   `CommitError::Conflict`, never a lost update;
3. applies every operation to a working clone of the document value;
4. stages exactly one `Transaction::put` — inheriting the document
   structural limits and the collection schema enforcement that hook
   already runs (`crate::families::schemas::enforce_put`) — or stages
   nothing when the patched value equals the original value, in which case
   the outcome is `PatchOutcome::NotModified`.

The typed refusal set is closed:

| Error | Meaning |
| --- | --- |
| `PatchError::Construction(RequestValidationError)` | the application-time defense against a malformed shape; construction itself refuses malformed paths and patches as `RequestValidationError` (`EmptyField`, `InvalidPatch`) at the constructors, before any data access |
| `PatchError::MissingTarget` | the document does not exist (and the caller did not use `AddOrPatch`) — V1 parity, `JsonPatchCommand.cs:37-40` |
| `PatchError::StaleTarget` | the observed version contradicts `expected` |
| `PatchError::PathNotFound` | an addressed position does not exist: a `Replace`/`Remove` target, an out-of-bounds array index, or a walk through an absent member |
| `PatchError::TypeMismatch` | a segment addressed a container of the wrong kind (field into a non-object, index into a non-array or a missing array — arrays are never materialized), or `Increment` on a non-integer |
| `PatchError::ArithmeticOverflow` | checked `Increment` overflow |
| `PatchError::Engine(EngineError)` | the underlying transaction read or staging failed, including the document structural limits and collection schema enforcement the write path already runs |

**No partial writes, by construction.** Every operation applies to a
working clone; a refusal at any step abandons the clone and returns before
`put` is ever called, so the transaction's staged write set is untouched —
and the transaction seam itself publishes all staged writes atomically or
not at all (`Transaction::commit`, `src/engine/mod.rs:1081-1119`). A patch
that fails midway leaves the document exactly as it was. The tests prove
both layers: a mid-patch refusal leaves nothing staged, and a staged-then-
rejected commit leaves the published view untouched.

`PatchOutcome::NotModified` is decided by deep value equality
(`PartialEq` on `DocumentValue`), which is V1's *scripted*-patch meaning —
"neither document body nor metadata was changed"
(`PatchResult.cs:27-30`) — not V1's JSON-Patch approximation, which marks
the document modified whenever any non-`test` command ran
(`JsonPatchCommand.cs:77-84`). **accepted-difference** recorded.

## D6 — rejected alternatives

### A. JSON-Patch (RFC 6902) adoption

Rejected on four precise clashes with the typed seam, not on familiarity:

1. **Unbounded path shapes.** A JSON Pointer is an arbitrary-length
   `/`-split string whose `~0`/`~1` escaping lets any segment encode any
   text — including `/` and `~` — and whose empty segments are legal
   under the RFC (V1 splits then unescapes each segment, and its parser
   additionally refuses empty segments of its own accord,
   `JsonPatchCommand.cs:432-458`). The path space is therefore not finitely
   classifiable: every bound this seam requires (`MAX_PATCH_PATH_SEGMENTS`,
   byte bounds, control-character refusal) would have to be re-imposed on an
   escaped string after parsing, and construction-time refusal — the
   property that makes includes and loads clauses decidable before any data
   access — is lost. The typed `PatchPath` segment model gets the same
   addressing power with a finite alphabet.
2. **`test`-op semantics.** RFC 6902's `test` embeds a read-assertion
   language inside a write: a deep value-equality engine with numeric
   coercion (V1's `JsonValuesEqual` coerces across ten CLR numeric shapes
   to compare `1`, `1.0`, and `1m` equal, `JsonPatchCommand.cs:198-303`).
   The typed seam already has the disciplined form of conditional writing —
   `ExpectedVersion` guards on the *version*, is checked at construction of
   the write and again at commit, and fails typed. A content-`test` is a
   second, weaker conditional-write mechanism with coercion-defined
   equality, and it is the one RFC 6902 operation that changes nothing yet
   fails: assertion and mutation in one list.
3. **String-pointer addressing.** Addressing by escaped string means every
   consumer — host, CLI, MCP surface, audit record — must re-parse and
   re-unescape to answer "what does this touch". The typed seam's whole
   shape (closed constructors, `try_new` validation, read-only accessors)
   exists so that question is answered by the type. A string path cannot be
   validated before parse, cannot carry a per-segment bound, and cannot be
   reasoned about by an agent tool without accepting the RFC's escape
   algebra verbatim.
4. **Unguarded writes.** V1's own JSON-Patch command accepts no change
   vector (`JsonPatchCommand.cs:25-31`), unlike its scripted sibling. The
   V2 seam requires the `ExpectedVersion` discipline on every write;
   adopting the RFC surface as-is would either inherit the unguarded shape
   or extend it into a dialect — at which point it is no longer JSON-Patch.

Porting `move`/`copy` (D6 deferred rows) does not rescue adoption: they are
`Add`+`Remove` compositions expressible by the caller, and their `from`
paths would double the string-addressing surface.

### B. JSON Merge Patch (RFC 7386)

Rejected: merge-patch is object-recursive overwrite with a null-deletes rule
and no operation list. It cannot express `Remove` without a null sentinel
(a `Null` document value is data in this engine, not a tombstone), cannot
express `Increment` at all, has no array addressing beyond wholesale
replacement, and its recursive-merge shape makes the no-partial-write proof
a tree-diff problem instead of a clone-and-apply one. It shares JSON-Patch's
string-key addressing without even its positional forms.

### C. Scripted patch with a sandboxed interpreter

Rejected before evaluation: charter `PD-2026-08-26-08` D2 bans script
execution outright — "no arbitrary code execution in the language (typed
expression projections only)". A sandbox is still a script runtime; the
constraint is not a performance or containment preference. This also removes
V1's `PatchByQueryOperation` script body from GAP-Q10's future surface for
the same reason.

### D. Whole-document read-modify-write only (no patch vocabulary)

Rejected as the *only* surface: it forces every field-level change through a
full document transfer, which is the bandwidth cost the sync charter
(`PD-2026-08-26-13`) works against, and it moves conflict resolution to the
caller with no typed refusal vocabulary. `Transaction::put` remains the
mechanism *under* the vocabulary (D5) — the decision is to type the delta,
not to invent a second write path.

## The seam

Everything lives in `src/engine/patch.rs`, exported through the engine
module: `PatchPath` and its segments, `PatchOperation`, `DocumentPatch`,
`AddOrPatch`, `PatchOutcome`, `PatchError`, and
`apply_document_patch(&mut Transaction<'_>, ...)`. The engine write path
needed exactly one minimal hook: `Document::set_value` (`src/types.rs`),
a crate-private in-place value replacement that keeps identity, collection,
and metadata, so a staged patch carries the version its expectation was
checked against. The seam otherwise composes `Transaction::read_in_collection`
and `Transaction::put`, which already enforce limits, schemas, expected
versions, and atomic publication; construction refusals add one
`RequestValidationError::InvalidPatch` variant to the shared closed set,
following the pattern `InvalidFamilyPayload` and `InvalidQuery` set. The
`PublicOperation` registry, host, and MCP surfaces are deliberately
untouched — those are the operations/adapter waves' regions, and this
packet owns the typed seam beneath them.

## V1 differences recorded

- **accepted-difference — no script, no skip.** V1's `Skipped` status and
  `skipPatchIfChangeVectorMismatch` have no V2 form; stale targets refuse
  typed.
- **accepted-difference — increment is integer-typed.** No JS coercion, no
  float increment; non-integer targets refuse.
- **accepted-difference — `NotModified` by deep equality.** V1's JSON-Patch
  marks any non-`test` command list as modified; V2 stages nothing when the
  patched value equals the original.
- **accepted-difference — bounded patches.** V1 accepts unbounded operation
  lists and unbounded path depth; V2 refuses past `MAX_PATCH_OPERATIONS`
  and `MAX_PATCH_PATH_SEGMENTS` at construction.
- **accepted-difference — nested object creation.** V1's JSON-Patch walk
  refuses a missing intermediate; V2's `Add` creates missing intermediate
  objects (never arrays).
- **deferred — `move`/`copy`.** Composable as `Add` + `Remove`; add typed
  forms only if a caller surface demonstrates the need, and only as
  same-patch compositions that preserve the no-partial-write proof.

## Limits

- No query-driven patching here: PB-4037 is GAP-Q10's, and this vocabulary
  is the per-document body that operation must reuse, not a second language.
- No string or wire spelling of `PatchPath`: no parser exists at this seam
  by decision D3/A.3. Surface spelling belongs to the host/CLI/MCP waves.
- Counters remain the counters family's typed increment
  (`CounterOperation::Increment`, `src/families/payloads.rs:459-468`);
  `Increment` here patches *document fields* and does not touch family state.
- Parity status records (`parity/overlays/**`) are outside this packet's
  write scope; this document is the evidence the parent adjudication uses
  for any PB-2014 status change.
- No performance claim is made; nothing here asserts speed, and
  `PD-2026-08-26-03`'s gates were not triggered.
