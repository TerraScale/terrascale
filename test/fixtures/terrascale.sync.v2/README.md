# Vendored `terrascale.sync.v2` golden-vector corpus

This is the native `vector-corpus@2` corpus shared with TerraBaseV2. It records
the bounded protocol envelope and native payload codecs (IDs 3 and 4). The
result-view state machine is exercised by the SDK's native result-view tests;
these byte fixtures remain the cross-language wire corpus.

This directory is a **byte-exact pin** of the engine's committed conformance
corpus, vendored by hand with `cp` — never regenerated or edited locally.
`manifest.json` is the engine's own integrity manifest; the conformance run
verifies every vector's SHA-256 and size against it and fails closed on any
drift, so a stale pin is a test failure, not a silent mismatch.

## Source

- Engine repository: TerraBaseV2, `develop` at the revision providing
  `crates/terrabase-sync-core/vectors/terrascale.sync.v2/`.
- Vectors source: `crates/terrabase-sync-core/vectors/terrascale.sync.v2/`.
- Manifest source: `crates/terrabase-sync-conformance/fixtures/manifest.json`
  (itself digest `edb9612a22555eed2283cb8cd60e196253d87bb91ab39db0db0da912222f7673`
  at that revision).

The manifest and every vector are copied from that engine directory. A stale
or hand-edited copy fails the manifest integrity gate.

## Re-vendoring

When the engine corpus legitimately changes, re-vendor the whole directory
from one engine checkout:

    cp <TerraBaseV2>/crates/terrabase-sync-core/vectors/terrascale.sync.v2/*.json vectors/
    cp <TerraBaseV2>/crates/terrabase-sync-conformance/fixtures/manifest.json manifest.json

then run `node --test dist/sync-v2-conformance.test.js`. If any conformance
check fails against the freshly vendored corpus, investigate the codec —
do not tune the vectors or the manifest. Update the pinned revision in this
file together with the copy.
