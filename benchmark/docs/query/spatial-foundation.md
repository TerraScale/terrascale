# GAP-Q14: Spatial foundation (decision-first)

- Ticket: TerraBaseV2#68 — Spatial foundation (decision-first)
- Packet: GAP-Q14, branch `fleet/gapq14-2`, base `da0df43`
- V1 source identity (all citations): TerraBase pin
  `fa99b9257776dbe625e6a2388c74c9ce916637ee`
  (`../.worktrees/terrabase-v1-fa99b925-readonly`, read-only)
- Status language: implemented / unrun / failed / inconclusive, exactly.

## What landed

`src/query/spatial.rs` (module root wired by the integrator at `da0df43`;
contents owned by this packet) implements the spatial foundation as typed
values, each with one validated construction path:

- **Coordinate contract**: `Longitude` ([-180, 180] degrees), `Latitude`
  ([-90, 90] degrees), `Wgs84Point` — x = longitude, y = latitude, the
  axis order and domain of V1's geography context. Non-finite and
  out-of-domain coordinates refuse with typed errors.
- **Units and distance arithmetic**: `SpatialUnits {Kilometers, Miles}`.
  `Wgs84Point::distance` reproduces V1's haversine chain exactly
  (nautical miles per degree, then 1.852 / 1.1515 factors);
  `Wgs84Point::apply_rounding` reproduces V1's ordering-distance rounding
  (`distance - distance % round`); `SpatialShape::circle_radius_degrees`
  reproduces V1's circle-radius-to-degrees translation (miles→km,
  ÷ Earth mean radius 6371.0087714, × 180/π).
- **Shape encoding**: `SpatialShape` over the bounded WKT subset V1
  accepts — `POINT`, `LINESTRING`, `POLYGON` (holes included), V1's
  `Circle(x y d=r)` form, and V1's rectangle forms (bare `x1 y1 x2 y2`,
  `BOX(x1 y1, x2 y2)`, and `BOX2D(x1 y1, x2 y2)` — the optional `2D` is a
  tag suffix in V1's pattern, and V1 lowers every match to the bare
  rectangle — corner order normalized). Z/M dimension flags and extra
  ordinates are discarded the way V1's `WktSanitizer` discards them.
  Bounds: `MAX_WKT_BYTES` 65 536, `MAX_SHAPE_POINTS` 256 per
  ring/linestring, `MAX_POLYGON_RINGS` 8; every bound refusal is typed.
  The enum is public, so `SpatialShape::validate` re-checks the parser's
  invariants for hand-assembled variants.
- **Document field encoding**: `SpatialFieldEncoding` — two numeric
  fields (latitude, longitude) or one WKT field — rendered to V1's exact
  dynamic-field tokens `spatial.point(lat, lng)` / `spatial.wkt(field)`.
- **Predicate vocabulary**: `SpatialRelation {Within, Contains, Disjoint,
  Intersects}` and `SpatialPredicate` (encoding + relation + shape +
  distance-error percentage), validated to V1's domain ([0, 0.5], default
  0.025) with bounded point-document evaluation
  (`SpatialPredicate::matches_point`): worst case one pass over at most
  8 rings × 256 positions. Parsed shapes were bounded at parse, and the
  seam re-validates hand-assembled shapes through
  `SpatialShape::validate`, so every accepted predicate is bounded
  however its shape was built.
- **Precision/discretization**: `SpatialPrecision` (0–9 decimals of a
  degree, default 6) and `QuantizedCell` — a monotone, idempotent,
  floor-aligned per-axis integer grid over the WGS84 domain.

Tests: 38 inline unit tests in `src/query/spatial.rs` and 7 public-seam
tests in `tests/query/spatial_foundation.rs`. The original 33 + 5 were
written red-then-green (the suite failed to compile for the right reason
— the vocabulary did not exist — before the implementation landed); the
7 added in repair loop 1 were each proven against the defect they close,
in the repair-loop section below.

## Decision records

### D1 (PROPOSED): the geometry base is V2-internal

**Decision**: the foundation implements its own minimal geometry — typed
coordinates, the V1 distance formulas, a bounded WKT-subset parser, and
planar even-odd containment with exact boundary tests — and adopts **no**
geometry crate. Cargo.toml and Cargo.lock are untouched by this packet.

**Alternatives and evidence**:

- **Adopt `geo` (georust) 0.33.1.** Audited on this host (network cargo
  fetch): a 46-package transitive closure (geo 0.33.1, geo-types 0.7.20,
  plus earcut, i_overlay/i_shape/i_float/i_tree, rstar, spade,
  geographiclib-rs, robust, rayon, crossbeam, foldhash, heapless, ...).
  License closure: 32 × MIT OR Apache-2.0, 7 × MIT, 2 × Apache-2.0 OR
  MIT, 1 × Apache-2.0, 1 × ISC, 1 × Zlib, 1 × Unlicense OR MIT, 1 ×
  (MIT OR Apache-2.0) AND Unicode-3.0 — every expression's components
  are on `deny.toml`'s allowlist (the compound Unicode-3.0 AND expression
  would still need cargo-deny confidence evaluation, which remains unrun
  on this program). No duplicate versions within the closure, and the
  shared crates (serde, smallvec, hashbrown, syn, thiserror, log,
  num-traits, either) resolve to the exact versions already in the
  repo's 191-package lockfile — so adopting `geo` would add roughly 43
  new packages without duplicating any existing one. Maintenance surface:
  georust is an active, well-governed organization; geo 0.33.1 is current.
  **Not adopted** because the parity-required surface at the pin is
  point-vs-shape relate plus two exact V1 distance conversions (see the
  inventory): `geo`'s strengths (polygon–polygon Boolean ops, robust
  predicates, projections, geodesy) are not needed by any open spatial
  parity row's required semantics, while its haversine (Earth-radius
  parameterization, 6371.0 km default) does **not** reproduce V1's
  nautical-mile chain — so V1-exact distance arithmetic had to be
  internal regardless of the decision. Adopting a 43-package closure for
  capability no open row requires, under a pending license gate
  (`Cargo.toml` `license_gate = "pending-before-release"`), is the wrong
  trade for a foundation packet.
- **Adopt `wkt` (georust) alone for parsing.** Rejected for the same
  reason at smaller scale: the accepted grammar is the decimal-only,
  reducer-shaped subset V1's own pre-processing enforces (its rectangle,
  BOX, circle, and 2D-reducer patterns are all decimal-only), and V1's
  own non-standard forms (`Circle(x y d=r)`, bare rectangle, BOX/BOX2D)
  are outside WKT proper, so a conformant parser would still leave
  V1-shaped pre-processing internal. One nuance this packet first got
  wrong and the repair loop corrected: V1's *readers* (spatial4n for
  `POINT`, the NetTopologySuite `WKTReader` for `LINESTRING`/`POLYGON`)
  do accept exponent notation even though its pre-processing patterns do
  not — see the non-claims and finding R4.
- **Adopt a full geospatial stack (proj, GDAL bindings).** Rejected
  without an audit: no parity row names a projection requirement, and
  V1/Raven spatial is WGS84-degrees-only at the query surface.

**Rejected-alternative caveat, recorded honestly**: when the index-side
spatial packet lands (geohash/quad-prefix trees, see D4), the georust
ecosystem (`geohash`, `rstar`, `geo`) is the natural first candidate and
this decision should be revisited there with this audit as its starting
evidence. The typed vocabulary in this module is the swap seam: nothing
outside `spatial.rs` evaluates geometry.

### D2 (PROPOSED): units and coordinate-system contract

WGS84 **degrees**, x = longitude, y = latitude — V1/Raven-compatible:

- V1's geography default bounds are exactly the WGS84 domains
  (`SpatialOptions` defaults `MinX=-180, MaxX=180, MinY=-90, MaxY=90`,
  v1:src/TerraBase.Client/Documents/Indexes/Spatial/SpatialOptions.cs:75-97).
- WKT coordinates are x-first (longitude, latitude);
  V1's `geo:` URI reader is latitude-first and explicitly swaps to x/y on
  read (v1:src/TerraBase.Server/Documents/Indexes/Static/Spatial/ShapeStringConverter.cs:101-107),
  which is the axis-order trap the typed `Longitude`/`Latitude` newtypes
  make unrepresentable — positional swaps cannot compile.
- Units are kilometers/miles exactly as V1 defines them
  (v1:src/TerraBase.Client/Documents/Indexes/Spatial/SpatialOptions.cs:207-211),
  with V1's two distance conversions kept in their V1 roles:

  1. **Distance values** (ordering, projection): haversine in
     international nautical miles (60 per degree) scaled by 1.852 (km) or
     1.1515 (mi) —
     v1:src/TerraBase.Corax/Utils/Spatial/SpatialUtils.cs:50-95.
  2. **Circle radii for matching**: miles → km (×1.60934), then ÷ Earth
     mean radius 6371.0087714, then × 180/π into degrees —
     v1:src/TerraBase.Server/Documents/Indexes/Static/Spatial/ShapeStringReadWriter.cs:56-62
     and v1:src/TerraBase.Client/Constants.cs:649-659.

  **Disclosed V1-internal inconsistency**: conversion 1 gives 111.12 km
  per equator degree and conversion 2 gives 111.195 km per degree —
  about 0.07% apart. V1 uses each in its own role; this foundation
  reproduces each in its role rather than unifying them, because
  unifying would drift distance values from V1's. The divergence is
  pinned by test (`one_equator_degree_is_sixty_nautical_miles_in_v1_units`
  pins conversion 1; `circle_radius_translates_to_degrees_through_the_earth_mean_radius`
  pins conversion 2).
- **Precision/discretization rule**: coordinates are never re-rounded
  for comparison; discretization exists only for key derivation.
  `SpatialPrecision` keeps 0–9 decimals of a degree (default 6 ≈ 0.11 m
  at the equator), strictly finer than V1's coarsest query-visible
  default discretization (geohash level 9 ≈ 4.78 m,
  v1:src/TerraBase.Client/Documents/Indexes/Spatial/SpatialOptions.cs:65-71),
  so quantization can never coarsen a comparison V1 would make. The
  quantizer is monotone (east/north never maps to a smaller cell),
  idempotent within a cell, and floor-aligned to the domain origin.
- **Antimeridian**: `west > east` on an envelope expresses an
  antimeridian-wrapping extent (union of both spans). This is a V2 rule:
  V1 delegates envelope semantics to spatial4n's rectangle, whose
  wraparound representation this record does not claim to reproduce
  bit-for-bit.

### D3: spatial predicate parity inventory against V1 at the pin

What V1 ships at `fa99b925`, with pinned citations:

| V1 capability | Pinned cite | V2 status after this packet |
| --- | --- | --- |
| Four relations: Within, Contains, Disjoint, Intersects | v1:src/TerraBase.Client/Documents/Indexes/Spatial/SpatialOptions.cs:199-205 | implemented as `SpatialRelation` |
| Circle predicate: radius/lat/lng + units + error pct | v1:src/TerraBase.Client/Documents/Queries/Spatial/SpatialCriteriaFactory.cs:63-66, v1:src/TerraBase.Client/Documents/Session/AbstractDocumentQuery.Spatial.cs:10-23 (`WithinRadiusOf`) | implemented as `SpatialShape::Circle` + predicate |
| WKT predicate: `RelatesToShape` + per-relation helpers | v1:src/TerraBase.Client/Documents/Queries/Spatial/SpatialCriteriaFactory.cs:13-61 | implemented for the bounded WKT subset (see non-claims) |
| Distance-error pct, default 0.025, domain [0, 0.5] | v1:src/TerraBase.Client/Constants.cs:650 (default); v1:src/TerraBase.Corax/Utils/Spatial/SpatialUtils.cs:28-38 (domain) | implemented (carried + validated; no approximation applied — see non-claims) |
| Distance ordering asc/desc, point or WKT center, round factor, null placement | v1:src/TerraBase.Client/Documents/Session/AbstractDocumentQuery.Spatial.cs:82-248 | **not implemented**; the arithmetic (distance + rounding) is typed and tested, the ordering integration is deferred to PB-4023 (owner engine/query) |
| WKT reduced to 2D (Z/M discarded) | v1:src/TerraBase.Client/Documents/Queries/Spatial/WktSanitizer.cs:10-37 | implemented (flags and extra ordinates discarded) |
| Rectangle forms: bare `x1 y1 x2 y2`, `BOX(x1 y1, x2 y2)`, `BOX2D(x1 y1, x2 y2)` | v1:src/TerraBase.Server/Documents/Indexes/Static/Spatial/ShapeStringConverter.cs:21-22 (pattern `^BOX(?:2D)?\s*\(...\)$`, `2D` a tag suffix), :49-61 (a match is lowered to the bare `x1 y1 x2 y2` string); v1:src/TerraBase.Client/Documents/Queries/Spatial/WktSanitizer.cs:16-17 (bare-rectangle pattern) | implemented (normalized envelopes) |
| `geo:` URI input (lat-first, with `u=` meters uncertainty) | v1:src/TerraBase.Server/Documents/Indexes/Static/Spatial/ShapeStringConverter.cs:63-107 | **not implemented** (recorded follow-up; input-form convenience, not predicate semantics) |
| Dynamic spatial fields: `spatial.point(lat, lng)` / `spatial.wkt(field)`, round factor | v1:src/TerraBase.Client/Documents/Queries/Spatial/DynamicSpatialField.cs:5-48 | implemented as `SpatialFieldEncoding` + `to_v1_field_token` |
| Spatial index options: geography/cartesian, geohash/quad-prefix/bounding-box, tree level, bounds, units | v1:src/TerraBase.Client/Documents/Indexes/Spatial/SpatialOptions.cs:65-197 | **out of this packet** (index-side; see D4) |
| Corax execution: recursive geohash decomposition, fully-covered cells short-circuit, boundary cells relate point-by-point | v1:src/TerraBase.Corax/Utils/Spatial/SpatialUtils.cs:98-244, v1:src/TerraBase.Corax/Querying/Matches/SpatialMatch/SpatialMatch.cs:39-171 | **out of this packet** (index-side); V2 evaluation is exact per-point |
| Lucene execution: spatial4n `SpatialOperation` per relation | v1:src/TerraBase.Server/Documents/Queries/LuceneQueryBuilder.cs:1166-1180; v1:src/TerraBase.Server/Documents/Indexes/Persistence/Corax/CoraxQueryBuilder.cs:1248-1251 | **out of this packet** (engine selection is PB-4052) |
| Spatial boosting score | v1:src/TerraBase.Corax/Querying/Matches/SpatialMatch/SpatialMatch.cs:145-148,189 | **not implemented**, and see the V1 defect note below |

**V1 fidelity notes found during the inventory** (recorded for
ratification, not reproduced):

1. **V1's Corax evaluator indexes points only.** Document spatial
   entries are `CoraxSpatialPointEntry` values and matching resets a
   point for relate (v1:src/TerraBase.Corax/Indexing/IndexWriter.IndexEntryBuilder.cs:374,
   v1:src/TerraBase.Corax/Querying/Matches/SpatialMatch/SpatialMatch.cs:135-145).
   V1's Lucene side does index full WKT document shapes. V2's foundation
   defines point-document evaluation only — matching its Corax, not its
   Lucene, engine.
2. **V1's Corax collapses Contains onto Within for point documents.** A
   relate answer of Within or Contains satisfies a query of either
   relation (v1:src/TerraBase.Corax/Querying/Matches/SpatialMatch/SpatialMatch.cs:159-171),
   so `Contains(circle)` matches any point inside the circle in Corax,
   while Lucene's `SpatialOperation.Contains` would not. V2's typed
   `Contains` is honest instead: it holds only when the query shape is
   the very same point. **This is a deliberate, disclosed divergence for
   the parent to ratify or override.**
3. **V1's spatial boosting score path passes longitude and latitude
   swapped** into its haversine relative to the filter path
   (v1:src/TerraBase.Corax/Querying/Matches/SpatialMatch/SpatialMatch.cs:145-148:
   `HaverstineDistance(_yShapeCenter, _xShapeCenter, termsReader.Longitude, termsReader.Latitude)`),
   so score distances and filter distances disagree for the same entry.
   Not mirrored; recorded as a V1 defect for the parity adjudication.
4. **V1's two degree→distance conversions disagree** (D2 above).

### D4: index-side spatial structures are out of this packet

Geohash/quad-prefix trees, spatial index field options (PB-4050), the
Corax-style covering-cell short-circuit, and spatial index selection are
**deferred**, owner `engine/index` (per parity inventory PB-4050's owner)
in coordination with `engine/query`. Nothing in this packet composes
trivially from the existing index definitions seam: spatial keys are not
terms and the current catalog has no cell-encoded fields, so a
non-trivial design is required (which `SpatialPrecision`/
`QuantizedCell` exist to serve). This is a scoping decision, not a
non-claim of feasibility.

## WS-PARITY proposals (spatial rows)

Builders do not flip parity rows; the following are PROPOSED
dispositions for the parent to ratify through WS-PARITY.

- **PB-4022 spatial-predicates** — PROPOSED: keep `unresolved`, with the
  foundation recorded: the typed predicate vocabulary (relations, units,
  coordinate contract, shape encoding, bounded point-document
  evaluation) is implemented and tested (inline + public seam), but
  execution against the query seam (spatial clauses inside `QuerySpec`,
  evaluation over `ReadSnapshot` documents, extraction from document
  fields per `SpatialFieldEncoding`) is not wired, and shape-document
  evaluation (V1 Lucene parity) is not implemented. Row should move to
  `partial` (or stay `unresolved` with this packet as Proof ID) only
  after the parent accepts the D3 divergence note 2 (Contains) and the
  point-document-only scope.
- **PB-4023 spatial-distance-ordering** — PROPOSED: keep `unresolved`,
  with the arithmetic landed: V1's distance values and rounding rule are
  typed and pinned by tests (`Wgs84Point::distance`,
  `apply_rounding`); ordering integration (asc/desc, WKT centers, null
  placement, mixed orderings) is deferred to engine/query once the
  query seam's ordering vocabulary grows a spatial key. Recommend the
  follow-up also adjudicate V1 fidelity note 3 (score-path axis swap).
- **PB-4050 spatial-index-options** — PROPOSED: keep `unresolved`,
  owner `engine/index`; this packet contributes the coordinate/precision
  contract (`SpatialPrecision`, `QuantizedCell`) any spatial index
  structure must quantize with, and D4's deferral. The
  geography-vs-cartesian and strategy options remain wholly open.
- PB-4032 (LINQ translation), PB-4045 (auto-map), PB-4047, PB-4052,
  PB-4060 mention spatial only adjacently; no proposal from this packet.

## Proposed collision actions

One collision commit (`collision: expose the GAP-Q14 spatial vocabulary
at the query module root and wire its integration test`, `fb00f8b`), two
minimal shared-file edits, for integrator ratification at merge:

1. `src/query/mod.rs`: `pub use spatial::{...}` — the module itself is
   private by the integrator's own wiring (`mod spatial;` at `da0df43`),
   so this re-export is the only way the vocabulary becomes the seam's
   public surface. 22 names, no behavior.
2. `tests/query/main.rs`: one `mod spatial_foundation;` declaration.

No Cargo.toml, Cargo.lock, CI, parity/**, or other src/** edits.

## Repair loop 1 (gate findings)

Inspector verdict GO; warden verdict FAIL on one MAJOR plus four MINORs
and four NOTEs. Loop 1 closed the MAJOR and all four MINORs. The two
NOTEs that named code-level quality items (`MAX_WKT_BYTES`' doc claim
holding only for realistic coordinate widths; `apply_rounding` being an
associated function rather than a method) are recorded below as accepted,
not changed. Everything here is red-then-green: each behavior fix shows
the failing run before it and the passing run after.

### R1 (MAJOR): hand-built shapes reached evaluation unvalidated

**Finding.** `SpatialPredicate::try_new` validated only the
distance-error percentage. `SpatialShape` is a public enum, so
`LineString { points: vec![] }`, `Circle { radius: NaN | <= 0.0 }`, a
linestring over [`MAX_SHAPE_POINTS`], and an envelope with `south > north`
could all be assembled by hand with contents `SpatialShape::parse`
refuses, and nothing re-checked them at the seam where they reach
evaluation. The consequence was profile-dependent, exactly as the warden
probed: in debug an empty linestring panicked in the segment loop
(`points.len() - 1` underflow), and in release the same predicate under
`Disjoint` matched every document — fail-open. NaN and non-positive circle
radii behaved identically. This contradicted the module's own contract
("typed values with one validated construction path each") on the exact
primitive handed to engine/query.

**Fix** (`c476dd1`). `SpatialShape::validate` is public (the enum is
public, so callers need the check too) and re-establishes exactly the
parser's invariants: 2 to `MAX_SHAPE_POINTS` linestring positions, a
finite strictly-positive circle radius, and `south` never above `north`
(`west > east` stays valid — that is the antimeridian wrap). Point and
polygon variants hold types validated at their own constructors and
always pass. `SpatialPredicate::try_new` — and therefore
`with_default_error_pct` — now calls it. `contains_point` additionally
stays total on an empty hand-built linestring rather than underflowing,
so evaluation no longer depends on the build profile even before
validation runs.

**Tests.** `the_predicate_seam_validates_hand_built_shapes` (inline:
seven invalid shapes refuse, four valid hand-built ones construct,
including a full-cap linestring and an antimeridian envelope) and
`a_spatial_predicate_refuses_an_unvalidated_hand_built_shape` (public
seam), plus `a_hand_built_empty_linestring_does_not_panic_evaluation` for
totality.

**Red before / green after.** Red on the pre-fix tree:
`cargo test --lib query::spatial` 3 failed / 35 passed — the
shape-validation test, and the totality test panicking with
`attempt to subtract with overflow` at the exact site the warden named —
and `cargo test --test query spatial_foundation` 2 failed / 5 passed
(`unwrap_err()` on an `Ok` predicate carrying `LineString { points: [] }`).
Green after: 38/38 and 7/7.

### R2 (MINOR): three contracts no test could detect losing

**Finding.** The warden's mutations M2 (circle boundary `<=` → `<`), M4
(deleting the linestring point cap), and M8 (deleting the identical-point
short-circuit) each left the 33-test suite green. Those three contracts
were therefore unpinned: the documented boundary-inclusive circle
comparison, the `MAX_SHAPE_POINTS`-per-linestring bound behind the
"worst case 8 rings × 256 positions" evaluation claim, and V1's
identical-coordinate exact zero.

**Fix/tests** (`51a7586`). Three additions. (a)
`the_circle_boundary_is_inclusive_at_the_translated_radius` derives the
radius as the exact inverse of the boundary point's own angular distance
and asserts the bitwise equality of the two compared doubles *first*, so
the input provably sits on the boundary and the test cannot silently
degrade into a near-boundary one. (b)
`wkt_inputs_beyond_the_bounds_fail_with_typed_errors` gained a 257-position
linestring, the over-cap input for its own arm of the bound. (c)
`identical_points_are_exactly_zero_even_where_the_unguarded_formula_is_not`
pins latitude 10.0, where the unguarded law of cosines yields
`sin² + cos²` one ulp below one and `acos` returns 8.5e-7 degrees instead
of zero — unlike the latitude the original test happened to use.

**Red/green.** These are coverage defects, not behavior defects: the code
under test was already correct, so the honest red proof is the mutation
itself. In a throwaway `/tmp` copy of this worktree with M2, M4, and M8
re-applied verbatim, `cargo test --lib query::spatial` fails exactly
three tests — the three above:
`the_circle_boundary_is_inclusive_at_the_translated_radius` (boundary
point no longer contained), `wkt_inputs_beyond_the_bounds_fail_with_typed_errors`
(the 257-position linestring parses `Ok`), and
`identical_points_are_exactly_zero_even_where_the_unguarded_formula_is_not`
(`8.537736462515939e-7 != 0.0`, the magnitude the warden's sampling
predicted). The pre-existing `identical_points_are_exactly_zero_distance`
still passes under M8 — that is precisely the gap the new test closes.
Unmutated tree: 38/38. The scratch copy was deleted after the run.

### R3 (MINOR): the record and the module docs named a BOX form V1 never accepted

**Finding.** The packet documented and cited a `BOX(2D)(x1 y1, x2 y2)`
rectangle form. V1's pattern is `^BOX(?:2D)?\s*\(...\)$`
(v1:src/TerraBase.Server/Documents/Indexes/Static/Spatial/ShapeStringConverter.cs:21-22)
— the optional `2D` is a *suffix on the tag*, so V1's forms are
`BOX2D(0 0, 10 10)` and `BOX(0 0, 10 10)`, and V1 lowers both to the
bare `x1 y1 x2 y2` rectangle string
(v1:...ShapeStringConverter.cs:49-61). V2's behavior was already correct
(both real forms parse to the normalized envelope; `BOX(2D)(...)` refuses
as Malformed); the *statements* were wrong, in the module doc, the
`SpatialShape` and `parse` documentation, one test comment, and this
record.

**Fix** (`34f5cc6`). All five statements corrected to the real grammar,
with the pattern cited. `parses_the_v1_rectangle_shorthand_as_an_envelope`
now also pins the untagged `BOX(...)`, whitespace-padded `BOX (...)`, and
lowercased `box2d(...)` forms, so the accepted surface is pinned rather
than described.

**Red/green.** Record-accuracy defect with no behavior change to make
red: the corrected tests pass on both the old and new statements (they
assert behavior that already held), and the correction is verified by the
cited pattern above. `cargo test --lib query::spatial` 38/38 before and
after.

### R4 (MINOR): the exponent-grammar premise was false

**Finding.** The parser refuses exponent notation and the record asserted
V1's grammar is decimal-only. V1's *pre-processing* patterns are
decimal-only, but its readers are not: spatial4n reads `POINT (1.5E2 2)`
as x=150, and the NetTopologySuite `WKTReader` V1 uses for
`LINESTRING`/`POLYGON`
(v1:src/TerraBase.Server/Documents/Indexes/Static/Spatial/ShapeStringReadWriter.cs:29-43,94-103)
parses exponent ordinates. So V1 accepts scientific notation on its
tagged-WKT path and V2 refuses it — a real, undisclosed, fail-closed
divergence.

**Fix** (`34f5cc6`, record side). Disclosed rather than silently
implemented, for two reasons: blanket exponent acceptance would *create*
a new divergence on the forms V1's decimal-only gates really do cover
(BOX, bare rectangle, `Circle` — their patterns are decimal-only, so V1
rejects exponents there), and the choice between "accept for tagged forms
only" and "refuse everywhere, fail-closed" is a grammar-scope decision
this decision-first packet should put to the parent rather than take. The
divergence is now in the non-claims with the corrected premise and pinned
citations, `SpatialShape::parse`'s documentation states it, and the
refusal is pinned by test across all three tagged forms (`POINT`,
`LINESTRING`, `POLYGON`), not just `POINT`.

**Red/green.** Same shape as R3: the behavior is deliberately unchanged,
so the new assertions pass on both trees (they pin existing behavior);
38/38. The parent ratification item is in the deferred table.

### R5 (MINOR): field names containing the token's delimiters rendered ambiguous tokens

**Finding.** `validate_field_name` (src/query/spec.rs) rejects only
empty, oversized, control-character, and whitespace-padded names, so
`try_new_point("a, b", "c")` and `try_new_point("a", "b, c")` were both
accepted and rendered the identical token `spatial.point(a, b, c)`;
`"x)"` rendered a structurally broken one. V1 avoids this by passing each
name through `EnsureValidFieldName` before interpolation
(v1:src/TerraBase.Client/Documents/Queries/Spatial/DynamicSpatialField.cs:16-18,31-33
through v1:src/TerraBase.Client/Documents/Session/AbstractDocumentQuery.cs:1946-1962
into v1:src/TerraBase.Client/Documents/Queries/QueryFieldUtil.cs:11-67,
which quotes names outside the plain identifier set). The accessor's doc
claimed cross-read, which held only for delimiter-free names. Nothing in
V2 consumes the token yet, so this was a latent contract defect.

**Fix** (`c476dd1`). The constructors refuse the three token delimiters
`,` `(` `)` after the existing `validate_field_name` check — fail-closed,
matching how this packet treats the other unreproduced V1 behaviors. The
escaping itself is not reproduced and is recorded as a divergence for
ratification, because V2's query grammar has no quoted-field-name notion
yet and inventing one is engine/query's call, not this packet's.

**Tests.** `spatial_field_encodings_refuse_token_delimiter_field_names`
(inline, both constructors and both point fields, plus a plain-name
positive that renders unambiguously) and
`spatial_encodings_refuse_field_names_that_break_the_token` (public seam).

**Red before / green after.** Red: inline 1 of the 3 failing tests
(accepted `Point { latitude_field: "a, b", ... }`), seam 1 of the 2
failing (same, `unwrap_err()` on `Ok`). Green: 38/38 and 7/7.

### Notes accepted, not changed

- `MAX_WKT_BYTES`' doc claim that the largest accepted polygon always
  fits holds for realistic coordinate widths; the decimal grammar permits
  arbitrarily long literals, so a pathological 256-position ring can
  still refuse as `TooLarge`. A bounds interaction, disclosed here.
- `Wgs84Point::apply_rounding` takes its point-independent arguments as
  an associated function. Quality call, left to the warden's lane.

## Receipts

All commands run in the packet worktree
(`/home/mariogk/Projects/TerraScale/TerraBaseV2/.worktrees/terrabasev2-gapq14`)
after `unset RUSTUP_TOOLCHAIN`, on branch `fleet/gapq14-2`. The host runs
concurrent fleet builds; every cargo step below ran to completion under
`timeout` guards.

| Command | Exit | Notes |
| --- | --- | --- |
| `cargo test --lib query::spatial` (red run, test suite written before the implementation) | 101 (compile failure: `no DEFAULT_DISTANCE_ERROR_PCT in query::spatial` etc.) | the tests failed for the right reason: the vocabulary did not exist |
| `cargo test --lib query::spatial` (green run, final tree) | 0 | 33 passed, 0 failed |
| `cargo test --test query spatial_foundation` | 0 | 5 passed, 0 failed |
| `cargo fmt --all -- --check` | 0 | after one intermediate fmt fix committed separately |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` | 0 | clean, run individually and inside every check.sh run |
| `cargo check --workspace --locked` | 0 | inside every check.sh run below |
| `cargo test --workspace --locked` (dev profile, required) | 0 | inside every check.sh run below |
| `TERRABASE_V1_ROOT=... ./scripts/check.sh` (run 1) | 1 | every gate inside it passed (fmt, check, dev + release workspace tests, clippy, cargo doc, parity tooling, secret scans — zero failures in the log); the exit 1 is the script's own final workspace-root resolution, which tries only `../..` and so cannot find the TerraScale root from a worktree nested under `TerraBaseV2/.worktrees/`; fix candidate for the integrator: one more `../..` candidate or require `TERRASCALE_WORKSPACE_ROOT` in worktrees |
| `TERRASCALE_WORKSPACE_ROOT=... TERRABASE_V1_ROOT=... ./scripts/check.sh` (run 2) | 101 | one failure in the release-profile test phase: `benchmarks::tests::a_self_comparison_reads_as_parity`, `left: Better, right: Stable` — a wall-clock throughput self-comparison that reads Better when the host's concurrent fleet load shifts between its two sampled lanes; 840 passed / 1 failed, dev phase fully green |
| `TERRASCALE_WORKSPACE_ROOT=... TERRABASE_V1_ROOT=... ./scripts/check.sh` (run 3, the flake re-run) | 0 | full strict gate green, zero failures across dev + release tests, clippy, cargo doc, parity tooling, strict V1 citation audit, secret scans; cargo-deny recorded as unrun by the script (tool not installed on this host) — the license audit in D1 is therefore a recorded manual audit, not a cargo-deny run |

### Repair loop 1 receipts

Red runs on the pre-fix tree (commit `51a7586`, tests only), green runs on
the final tree; all in the packet worktree after `unset RUSTUP_TOOLCHAIN`.

| Command | Exit | Notes |
| --- | --- | --- |
| `cargo test --lib query::spatial` (red, pre-fix) | 101 | 3 failed / 35 passed: `the_predicate_seam_validates_hand_built_shapes`, `spatial_field_encodings_refuse_token_delimiter_field_names`, and `a_hand_built_empty_linestring_does_not_panic_evaluation`, the last panicking `attempt to subtract with overflow` at the segment loop the warden named |
| `cargo test --test query spatial_foundation` (red, pre-fix) | 101 | 2 failed / 5 passed: `a_spatial_predicate_refuses_an_unvalidated_hand_built_shape` and `spatial_encodings_refuse_field_names_that_break_the_token`, both `unwrap_err()` on `Ok` values |
| `cargo test --lib query::spatial` (mutation red, R2) | 101 | in a `/tmp` copy of the worktree with the warden's M2/M4/M8 re-applied: 3 failed / 35 passed — the three discrimination tests of R2 and no others; copy deleted after the run |
| `cargo test --lib query::spatial` (green, final tree) | 0 | 38 passed, 0 failed |
| `cargo test --test query spatial_foundation` (green, final tree) | 0 | 7 passed, 0 failed |
| `cargo fmt --all -- --check` (final tree) | 0 | after one `cargo fmt --all` pass over the repair edits |
| `cargo check --workspace --locked` (final tree) | 0 | |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` (run 1) | 1 | `doc_markdown` on the un-backticked `NetTopologySuite` in the new `parse` documentation; fixed in `86d55de` |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` (run 2, final tree) | 0 | clean |
| `cargo test --workspace --locked` (final tree, dev profile) | 0 | 1 248 passed / 0 failed across 46 test binaries |
| `TERRASCALE_WORKSPACE_ROOT=... TERRABASE_V1_ROOT=... ./scripts/check.sh` (repair run 1) | 101 | one failure in the release-profile phase: `benchmarks::tests::a_self_comparison_reads_as_parity`, `left: Better, right: Stable` — the same wall-clock throughput self-comparison that flaked in this packet's original run 2 (recorded above), in `src/benchmarks/tests.rs`, which this packet does not touch; every other phase green |
| `TERRASCALE_WORKSPACE_ROOT=... TERRABASE_V1_ROOT=... ./scripts/check.sh` (repair run 2, the flake re-run) | 0 | full strict gate green: 2 496 tests passed / 0 failed across 90 green test-result lines, dev + release phases, clippy, cargo doc, strict V1 qualification (258 citation rows), secret scans; cargo-deny still recorded unrun by the script (tool absent on this host) |

Dependency-audit artifacts (scratch project `/tmp/geo-audit`, not part of
the repo): `cargo add geo` resolved geo 0.33.1 with a 46-package closure;
`cargo metadata --format-version 1` exit 0; license and duplicate-version
analysis in D1.

## Non-claims (explicit)

- **No spatial query execution.** Nothing in this packet runs a spatial
  clause against a `ReadSnapshot`; `SpatialPredicate::matches_point` is
  the evaluation primitive, not a wired query path. PB-4022 stays
  unresolved.
- **Point documents only.** Evaluation is defined for point documents;
  polygon/linestring *document* geometry (V1 Lucene parity) is not
  evaluated. Query shapes, not document shapes, may be polygons.
- **`distanceErrorPct` is carried, not applied.** Exact evaluation has
  no error budget; V1's parameter (an index-approximation tolerance) has
  no effect to mirror until index-side structures exist.
- **No spatial index structures, strategies, or options** (D4): geohash
  and quad-prefix trees, geography/cartesian typing, bounding-box
  strategy, spatial boosting are all unimplemented.
- **No `geo:` URI input form**, no WKT output, no multipoint/multiline/
  multipolygon/geodesic edges, no projection. Polygon edges are straight
  in degree space and do not wrap the antimeridian; envelopes do.
- **Exponent notation is refused (disclosed divergence, finding R4).**
  V1's shape *pre-processing* patterns are decimal-only — the bare
  rectangle (v1:src/TerraBase.Client/Documents/Queries/Spatial/WktSanitizer.cs:16-17),
  BOX (v1:src/TerraBase.Server/Documents/Indexes/Static/Spatial/ShapeStringConverter.cs:21-22),
  and circle (v1:src/TerraBase.Server/Documents/Indexes/Static/Spatial/ShapeStringReadWriter.cs:113)
  patterns all accept `[+-]?(?:\d+\.?\d*|\d*\.?\d+)` with no exponent —
  but the readers V1 hands off to after that pre-processing do accept
  scientific notation: spatial4n reads `POINT (1.5E2 2)` as x=150, and
  the NetTopologySuite `WKTReader` V1 uses for `LINESTRING`/`POLYGON`
  (v1:src/TerraBase.Server/Documents/Indexes/Static/Spatial/ShapeStringReadWriter.cs:29-43,94-103)
  parses exponent ordinates. V2's parser is decimal-only across all
  forms, so an exponent literal in a tagged WKT shape refuses here where
  V1 would read it — fail-closed, pinned by test, and left for the parent
  to ratify or reverse (accepting exponents for the tagged forms only
  would be the faithful shape of the fix, since V1's BOX/rectangle/circle
  gates stay decimal-only either way).
- **No distance-ordering integration** (PB-4023 arithmetic only).
- The internal-geometry decision (D1) and the units contract (D2) are
  labeled PROPOSED for parent ratification; the license audit is recorded
  even though no dependency was adopted.

## Deferred items (with owners)

| Item | Owner |
| --- | --- |
| Spatial index structures/options (PB-4050), covering-cell evaluation, spatial strategies | engine/index (D4) |
| Spatial clauses in `QuerySpec` + execution over snapshots + field extraction per encoding | engine/query |
| Distance-ordering integration (PB-4023) + V1 score-path defect adjudication | engine/query |
| Shape-document evaluation (V1 Lucene parity) | engine/query |
| `geo:` URI input form | engine/query (convenience; lowest priority) |
| Ratify D1/D2 and the D3 divergence note 2 (Contains semantics) | parent (WS-PARITY + integrator) |
| Ratify R4: exponent notation refused for tagged WKT (V1's readers accept it; its pre-processing gates do not) — accept-for-tagged-forms-only is the faithful fix shape | parent (WS-PARITY) + engine/query |
| Ratify R5: spatial field names carrying `,` `(` `)` refuse construction instead of V1's quoting escape | parent (WS-PARITY) + engine/query |
| Ratify R1's contract: the predicate seam validates shapes, so `SpatialShape::validate` is the admission point for hand-assembled shapes | parent (integrator) |

## Repair-loop scope note

Repair loop 1 touched only files already in this packet's write scope
(`src/query/spatial.rs`, `tests/query/spatial_foundation.rs`, and this
record). No new shared-file need arose: the field-name delimiter check
lives beside the spatial constructors rather than in
`src/query/spec.rs`'s `validate_field_name`, deliberately — the
restriction exists to protect the *spatial token's* structure, not query
field names in general, so widening the shared validator would have been
the wrong altitude. No collision actions were added.
