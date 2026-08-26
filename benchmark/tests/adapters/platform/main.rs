// Why no integration test target lives here.
//
// Cargo auto-discovers integration test targets only as `tests/*.rs` files
// and `tests/*/main.rs` directories one level below `tests/`. This directory
// is two levels deep, so `tests/adapters/platform/main.rs` is not compiled
// as a target unless the package manifest gains an explicit entry such as:
//
//     [[test]]
//     name = "adapters-platform"
//     path = "tests/adapters/platform/main.rs"
//
// `Cargo.toml` is outside this packet's write scope, so no such entry was
// added and the W5-E dispatch tests live in-crate at
// `src/adapters/platform/tests.rs` instead. That placement is also required
// on its own: the dispatch proof needs the crate-private adapter wiring —
// `PlatformAdapter::new` takes the crate-private `TrustedAuthorizer` and
// the crate-private `EnginePort`, and the fixtures read the engine identity
// through its crate-only test accessor — none of which is visible to an
// integration test.
//
// If the integrator later wires the `[[test]]` entry above, only the
// public-surface cases can live here: typed request construction through
// `DocumentReadRequest::try_new`, `DocumentWriteRequest::try_new`, and
// `QueryOperation::try_new`, and the redaction and closed-registry proofs
// the protocol adapter already owns; everything that touches the adapter
// wiring must stay in-crate.
