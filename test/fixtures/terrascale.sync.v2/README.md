# Vendored `terrascale.sync.v2` golden-vector corpus

This is the native `vector-corpus@2` corpus shared with TerraBase. It records
the bounded protocol envelope and native payload codecs (IDs 3 and 4). The
result-view state machine is exercised by the SDK's native result-view tests;
these byte fixtures remain the cross-language wire corpus.

This directory is a **byte-exact pin** of the engine's committed conformance
corpus, vendored by hand with `cp` — never regenerated or edited locally.
`manifest.json` is the engine's own integrity manifest; the conformance run
verifies every vector's SHA-256 and size against it and fails closed on any
drift, so a stale pin is a test failure, not a silent mismatch.

## Source

- Engine repository: TerraBase, `develop` at the revision providing
  `crates/terrabase-sync-core/vectors/terrascale.sync.v2/`.
- Vectors source: `crates/terrabase-sync-core/vectors/terrascale.sync.v2/`.
- Manifest source: `crates/terrabase-sync-conformance/fixtures/manifest.json`
  (itself digest `edb9612a22555eed2283cb8cd60e196253d87bb91ab39db0db0da912222f7673`
  at that revision).

The manifest and every vector are copied from that engine directory. A stale
or hand-edited copy fails the manifest integrity gate.

## Engine reference report

`engine-reference-report.json` is the report the engine's own Rust reference
harness produced over this committed corpus (the arbiter of "running the
corpus"). It is not part of the corpus pin and the conformance run never
reads it as data; `test/sync-v2-native-corpus-view.test.mjs` compares the
TypeScript harness report against it — every check name both contracts
define must execute the same number of times per vector.

Provenance:

- Engine revision `d67c4c4` ("Migrate sync core and corpus to native
  state"), the last commit touching the corpus and the conformance crate,
  an ancestor of the engine checkout used.
- Command: `cargo run -p terrabase-sync-conformance -- --manifest
  crates/terrabase-sync-conformance/fixtures/manifest.json --report <file>`
  from the TerraBase root.
- Corpus manifest digest (sha256) at that revision:
  `dd21e1dd21f3ed8c73250a6dd2c345fde206ba8ddd9606e4e05eae353744f599`.
- Reference result: 9/9 vectors, 257/257 checks, status `pass`.

Regenerate it from one engine checkout together with a re-vendor.

## Re-vendoring

When the engine corpus legitimately changes, re-vendor the whole directory
from one engine checkout:

    cp <TerraBase>/crates/terrabase-sync-core/vectors/terrascale.sync.v2/*.json vectors/
    cp <TerraBase>/crates/terrabase-sync-conformance/fixtures/manifest.json manifest.json

then run `node --test dist/sync-v2-conformance.test.js`. If any conformance
check fails against the freshly vendored corpus, investigate the codec —
do not tune the vectors or the manifest. Update the pinned revision in this
file together with the copy.

## The client-side native-corpus suite

`test/sync-v2-native-corpus-view.test.mjs` runs the client codec, the client
delivery store, and the authorized result-view engine against every vendored
vector (checkpoint round-trip, patch application, reset, and
authorization-generation change), verifies the SHA-256 pin independently,
and diffs the TypeScript harness report against the engine reference report.
Run it after a build:

    npm run build && node --test test/sync-v2-native-corpus-view.test.mjs
