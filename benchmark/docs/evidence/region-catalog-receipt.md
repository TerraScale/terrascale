# Canonical `RegionCode` type and production catalog (gap-sweep row 35)

Status: **implemented** (engine type + catalog + parser semantics + unit
tests), recorded 2026-08-29. Not qualified. This receipt implements the
engine slice of gap-sweep row 35 ("adding a production Region",
`docs/evidence/qualification-gap-sweep.md`); the row's admission
qualification remains open — see "Not claimed" below.

## Scope

`src/regions.rs` (new engine module), registered in `src/lib.rs`
(`pub mod regions;` plus the re-export of `RegionCode`, `RegionCatalog`,
`RegionCodeError`, `RegionAdmissionError`, `REGION_CEILING`). Charters:
GOAL.md "Canonical Region identity and OCI mapping" and "Selected active
Regions"; ADR 0033 "Canonical Region identity".

Implemented surface:

- `RegionCode` — a newtype over `[u8; 3]` that can hold only three strict
  uppercase ASCII letters (`A`..=`Z`). Fixed-width, `Copy`, ordered,
  hashable. The engine and private TBEP wire form is
  `RegionCode::as_bytes()`. Operator display uses `as_str()`/`Display`
  with the same three characters. `FromStr` parses against the production
  catalog.
- `RegionCode::GRU`, `SFO`, `FRA` and
  `RegionCatalog::INITIAL_ACTIVE` — the initial production catalog is
  exactly these three, in catalog order (`production()`, also `Default`).
- `REGION_CEILING = 20` — the architecture ceiling on **active** Regions
  per catalog generation. Reserved (retired) codes are not Regions and do
  not count against it.
- `RegionCodeError` — one variant per rejection class: `WrongLength`
  (carries the observed byte length), `NonAscii` (first byte ≥ `0x80`),
  `NotUppercaseLetter` (first ASCII byte outside `A`..=`Z` — lowercase,
  digit, punctuation, whitespace, control), `Unregistered` (carries the
  refused code), `Retired` (carries the retired code). No variant carries
  a repaired value: parsers never normalize input.
- `RegionCatalog::parse` (text) and `parse_wire_bytes` (fixed three-byte
  form) — shape first, then registration. `RegionCode::from_text` /
  `from_wire_bytes` validate shape only; the catalog layer is what refuses
  unregistered codes, so callers that accept Region identities parse
  through a catalog.
- `RegionCatalog::admit` / `retire` — the engine-side record of a catalog
  decision, enforcing: no duplicate active code, a retired code can never
  be readmitted (`Reserved`), and the twenty-Region ceiling
  (`CeilingExceeded`). The ADR 0033 admission qualification (availability,
  capacity, price, benchmark evidence, product lanes) happens before
  `admit` and outside the engine.

## Focused check

```text
cargo test -p terrabase-v2 regions::
```

Result at recording time: **12 passed, 0 failed** (`regions::tests`,
lib target). `cargo clippy -p terrabase-v2 --lib`: no warnings.

Test map (every charter rejection class has a dedicated test):

| Behavior | Test |
| --- | --- |
| Catalog is exactly GRU, SFO, FRA; nothing reserved | `the_production_catalog_is_exactly_the_three_initial_regions` |
| Constants round-trip text, wire bytes, `Display` | `the_region_constants_round_trip_text_and_wire_bytes` |
| Parse accepts each registered code, text and wire form | `parse_accepts_each_registered_code_in_both_forms` |
| Wrong lengths (empty, 1, 2, 4, padded, newline, 9-byte fullwidth) | `wrong_lengths_are_refused_including_padded_and_multibyte_blowup` |
| Lowercase refused, never normalized (byte-exact first-offender index) | `lowercase_text_is_refused_and_never_normalized` |
| ASCII non-letters (digit, `-`, space, tab, NUL, comma) | `ascii_bytes_outside_strict_uppercase_letters_are_refused` |
| Non-ASCII (3-byte CJK, 2-byte accent, invalid-UTF-8 wire bytes) | `non_ascii_text_is_refused` |
| Unregistered codes (`JFK`, `ABC`, `ZZZ`, `GRW`, `XXX`); shape-only constructor accepts what the catalog refuses | `unregistered_codes_are_refused` |
| `SJC` refused in both forms, resolves to nothing, `SFO` unaffected — never an alias (ADR 0033) | `sjc_is_refused_and_is_never_an_alias_of_sfo` |
| Retired code: parse says `Retired`, readmission says `Reserved` forever | `a_retired_code_stays_reserved_and_can_never_be_readmitted` |
| Admission refuses duplicates; retirement requires an active Region | `admission_refuses_duplicates_and_never_admitted_codes_cannot_retire` |
| `REGION_CEILING == 20` pinned; 21st active Region refused; retirement frees a seat the retired code itself can never retake | `the_catalog_enforces_the_twenty_region_ceiling` |

Full-crate context: `cargo test -p terrabase-v2` at this packet's exact
commit snapshot (run in a throwaway worktree, isolated from the concurrent
lanes editing the shared tree) recorded **1097 passed / 1 failed /
3 ignored** across the lib target, with all 12 `regions::tests` green.
The one failure — `benchmarks::tests::a_receipt_renders_the_discipline_lines`
(`src/benchmarks/tests.rs:362`, the receipt renderer omits the asserted
`terrabase v1 pin:` line) — is pre-existing: it fails identically at this
packet's parent commit and touches none of this packet's paths. It belongs
to the SE-15 benchmark lane; CI owns the full gate.

## Boundary properties stated by the module

- No OCI Region identifier, realm, availability/fault domain, OCID,
  compartment, or VCN string appears in `src/regions.rs` — the signed,
  versioned provider mapping lives only in the Orchestrator provider
  adapter (ADR 0033). The module deliberately contains no geography
  beyond the three airport codes themselves.
- There is no alias table anywhere in the engine. `SJC` is refused as
  plain unregistered text and the refusal is pinned by test; the engine
  cannot map a second code onto a cataloged Region.
- One catalog generation assigns a code to at most one active Region
  (`AlreadyActive`); the provider-side uniqueness rule ("one generation
  cannot map one code to two active provider Regions") is Orchestrator
  configuration, outside this crate.

## Not claimed (row 35 remainder)

- **Admission qualification** — the Dashboard, Database API,
  system-Project replica, routing, identity, and recovery gates that must
  pass before the catalog exposes a new Region: not implemented, not run.
  Row 35 moves from "missing" to "partial" only.
- **No rewiring of existing lanes** — `src/replication` lanes and
  `terrabase-cluster` tests still use synthetic `eu-1`/`us-1`/
  `r-alpha`-family `RegionId` values (gap row 6's note). Rewiring them to
  `RegionCode` is a later packet; this packet adds the type without
  touching those tests.
- **TBEP plumbing** — `from_wire_bytes`/`parse_wire_bytes` and the display
  methods are the seams, but no private gRPC message carries `RegionCode`
  yet. Public API mapping belongs to the Go gateway.
- **Twenty-Region capacity/fault qualification** (gap row 7) — only the
  admission ceiling constant and its typed refusal exist; no capacity or
  fault gate was run.
- **Retirement** — no production code is retired. The reserved-forever
  semantics are proven with a hypothetical retirement in unit tests.
- No placement, routing, durability, or performance behavior changed or
  was measured; nothing in this receipt is a production or timing claim.
