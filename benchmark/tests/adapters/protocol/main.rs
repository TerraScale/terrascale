// Why no integration test target lives here.
//
// Cargo auto-discovers integration test targets only as `tests/*.rs` files
// and `tests/*/main.rs` directories one level below `tests/`. This directory
// is two levels deep, so `tests/adapters/protocol/main.rs` is not compiled
// as a target unless the package manifest gains an explicit entry such as:
//
//     [[test]]
//     name = "adapters-protocol"
//     path = "tests/adapters/protocol/main.rs"
//
// `Cargo.toml` is outside this packet's write scope, so no such entry was
// added and the W3-3 tracer tests live in-crate at
// `src/adapters/protocol/tests.rs` instead. That placement is also required
// on its own: the forwarding proof needs the crate-private
// `PlatformPort` double (`ProtocolAdapter::new_with_platform_port` exists
// only under `cfg(test)`) and the `TenantScope::from_parts` fixture, neither
// of which is visible to an integration test.
//
// If the integrator later wires the `[[test]]` entry above, only the
// public-surface cases (caller-context structural validation and redaction,
// closed registry construction) can live here; everything that touches the
// port double must stay in-crate.
