# 4. Document lattice

```text
DocumentState = { clear: Version+deleted?,  fields: name -> FieldState }
FieldState    = { base:     Version, value (raw JSON) | tombstone       -- LWW, also the field's epoch
                  counters: node -> (epoch: Version, inc: u64, dec: u64) -- PN-counter
                  dots:     (element, Version) -> removedAt: Version?   -- add-wins set }
Version       = (stamp: Hlc, node: ReplicaId)
```

**Join** (all associative, commutative, idempotent):

| Entry | Join |
| --- | --- |
| `clear` | larger version, with its `deleted` bit |
| `base` | larger version, with its value |
| `counters[node]` | larger `epoch`. On an equal epoch, pointwise max of `inc` and `dec` |
| `dots[(element, version)]` | union. `removedAt` = the larger of the two (absent < any) |

**Visibility and value.**

- A field's **epoch** is `base.version` when it has a base, otherwise `clear.version`.
- A field is **live** when `base.version ≥ clear.version` (or it has no base), and one of these holds:
  - its base is not a tombstone;
  - it has a live counter entry (`epoch` equal to the field's epoch);
  - it has a live dot (version ≥ epoch, not removed).
- The **document exists** when `clear` is present and not deleted, or when a live base or live dot has a version above
  `clear.version`. A write after a delete brings the document back with only the fields written after the delete.
  This is last-writer-wins at document level. It also stops a stale concurrent PATCH from bringing old fields back.
- **Value of a live field**, in order:
  1. If live dots exist, or the base is an array and the field has dots: a set. Its elements are the base's elements
     without an explicit dot of their own, plus every explicit dot not removed, distinct and sorted by ordinal
     canonical JSON. Removing every element of an array-based set therefore leaves `[]`, not the old array.
  2. Otherwise, if live counter entries exist: the base number (0 if the base is absent or a tombstone) plus
     Σ(inc − dec). This is a 64-bit integer: increments are integers.
  3. Otherwise: the base value.

  Concurrent `inc` and `add` on the same absent field therefore make a set. Local validation rejects `inc` on a
  non-number and `add` on a non-array base.
- **Fields are top-level keys.** A nested path (merge-patch into an object, `address.city` in SQL) is a
  read-modify-write of its top-level field: the whole top-level value is LWW. `// anna-full`: nested maps as nested
  `DocumentState`s.

**Operations:**

| API | Entries written at `(S, self)` |
| --- | --- |
| `PUT` / create | `clear = (S, live)`, then `base` for every key of the body |
| `DELETE` | `clear = (S, deleted)` |
| PATCH, merge-patch key = value | `base` = value |
| PATCH, merge-patch key = null; ops `unset` | `base` = tombstone |
| ops `set`, `append` | `base` = the new value. `append` is a read-modify-write of the whole array, so it is LWW |
| ops `inc n` (an int64, may be negative) | own `counters[self]`: reset to `(epoch, 0, 0)` when its epoch is old, then `inc += n` or `dec += −n` |
| ops `add e` / `remove e` | `add`: new dot `(e, (S, self))`. `remove`: `removedAt = S` on every dot of `e` this node sees. The elements of an array base are implicit dots at `base.version`: nothing is copied, and removing one writes the explicit dot `(e, base.version)` with `removedAt = S`. Every node reads the same implicit dots from the same base, so this is deterministic, even on a node whose first set op was not the conversion |

Every PATCH, including `inc`, `add` and `remove`, needs a document that exists locally (404 `not_found`, as today).
A concurrent `inc` against a delete therefore lands in the old epoch, and the delete wins. JSON Pointer paths keep
working for `set`, `unset` and `append`, as a read-modify-write of their top-level field. `inc`, `add` and `remove`
take top-level fields only. `inc` becomes integer-only (today it uses exact decimal arithmetic).

**Normalization.** After every write or join, entries the state already dominates are dropped:

- base entries below `clear`;
- counter entries of an older epoch;
- dots below their field's epoch.

Dominance only grows under join, so normalization commutes with join. Tombstoned bases, removed dots and deleted
`clear`s are not dominated. They wait for GC (section 11).

**Delta of an operation** = the entries with `changed = S`, plus the values of plain fields. `changed` is local
bookkeeping: it is encoded but not merged and not hashed. A later operation in the same transaction re-sends what it
overwrote, so nothing is lost. Several operations of one request on one document share `S` and apply in order, the
later one replacing what the earlier one wrote at `S` (a `set` after an `add` drops that dot). So the lane writes the
document's rows once per request, after its last operation: an intermediate image would ship an entry at `S` that the
final state no longer holds, and no join could take it back.

**Materialization** is a pure function of state plus values. Fields are sorted by ordinal UTF-8 name. Values are
compact canonical JSON (re-serialized once at write time), counters are integers and sets are sorted arrays. Every
replica therefore holds the same bytes for the same state. Key order of a PUT body is not kept (JSON objects are
unordered).

**Encoding** (`Engine/Lattices/DocumentStateEncoding`) follows the `LatticeEncoding` rules: one format byte (1),
canonical order, minimal LEB128 and bounded counts. It is a trust boundary and accepts canonical bytes only. A per-
state node dictionary keeps it small. It is stored as base64url TEXT. It has two forms: the full form carries every
value (deltas), and the stored form leaves out a plain field's value, which the collection row's `doc` already holds
(`_tb_state.state`); decoding the stored form needs that `doc` and checks that it is the state's materialization. The
digest is the first 128 bits of SHA-256 over the collection and id (each UTF-8 with a minimal LEB128 byte length),
then the stored form with `changed` zeroed, then `doc`. A version is the type `EntryVersion` (the
name `Version` would collide with `System.Version`). `GCounter` and the old `AwSet` went with R1; `MvRegister`,
`CausalContext`, `Dot` and `DocumentWrite` go with R2a, which removes their last callers (section 19). `Hlc`,
`ReplicaId`, `ILattice`, `MaxLattice`, `LwwRegister`, `MapLattice` and `SetLattice` stay.
