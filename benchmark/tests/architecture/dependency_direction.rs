//! Gate 1 — dependency-direction architecture gate (GOAL.md P8: "Run
//! dependency-direction ... architecture gates").
//!
//! Authority: GOAL.md 'Dependency direction' — "The engine uses an explicit
//! acyclic dependency graph. Foundational crates know nothing about their
//! consumers" and "Repository checks reject dependency cycles, forbidden
//! imports, reverse-layer references ...".
//!
//! This gate reads the live workspace graph (the root `Cargo.toml` member
//! list plus each member's `[dependencies]`) and compares it with the frozen
//! tables below. The tables record today's truth, not aspiration: any new
//! internal edge, any edge by a crate missing from `LAYERS`, any reverse or
//! equal-layer edge, any cycle, any internal dependency of the foundational
//! or engine-core crates, and any new external dependency of the engine
//! core fails here. Extending a table requires a review note in the same
//! commit that cites the accepting record (an ADR or a charter PD id).
//! Removing a dependency is always allowed; stale allow-list entries are
//! deliberately not errors so cleanup lanes do not fight this gate.
//!
//! Review note (2026-08-30): the product package rename to the canonical
//! `terrabase` identity is complete. The root manifest and workspace
//! dependency keys use that target, and the N-1 dependency-alias window is
//! closed: every consumer declares the canonical package.
//!
//! Review note (2026-08-30): `terrabase-server` is the production composition
//! boundary accepted by GOAL.md PRG-P0-02. It sits above TBEP, depends only on
//! TBEP, cluster composition, and the engine, and is a workspace member so
//! every release gate builds and tests the daemon. The cluster's dependency
//! on `terrabase-member-transport` records ADR 0041's ownership of bounded
//! QUIC replication lanes; both edges point strictly down the frozen layers.

use std::collections::{BTreeMap, BTreeSet};

use crate::fsx;

/// The frozen layer order. Layer 0 is the most foundational crate; every
/// internal dependency edge must point strictly downward (a consumer may
/// import a foundation, never the reverse). The mapping follows GOAL.md
/// 'Dependency direction': identity and canonical codec -> native CRDT
/// algebra -> engine core (storage, families, query, index, reactive) ->
/// sync and transport seams -> replication and conformance -> cluster
/// composition -> the TBEP boundary.
const LAYERS: &[(&str, u32)] = &[
    ("terrabase-causal", 0),
    ("terrabase-crdt", 1),
    ("terrabase", 2),
    ("terrabase-sync-core", 3),
    ("terrabase-member-transport", 3),
    ("terrabase-replication-core", 4),
    ("terrabase-cluster", 5),
    ("terrabase-tbep", 6),
    ("terrabase-server", 7),
    ("terrabase-sim", 8),
];

/// The frozen internal-dependency allow-list: every `(consumer, provider)`
/// edge that existed when this gate landed. A new edge here requires a
/// review note; new members may join the workspace freely but their first
/// internal edge must be added to this list with the layer check satisfied.
///
/// Review note (2026-08-29): the `terrabase-replication-core` edges onto
/// `terrabase-causal` and `terrabase-crdt` record the landed native causal
/// migrations (its manifest replaced `loro` with the foundational crates).
/// Both edges point strictly down the frozen layer order, and GOAL.md
/// (AC-3, "No product crate or supported codec depends on Loro") is the
/// accepting record for that transition.
///
/// Review note (2026-09-02, issue #129): `terrabase-sync-core` lost its
/// `terrabase-causal`/`terrabase-crdt` edges and the
/// `terrabase-sync-conformance` crate was deleted with the S-04/D-38
/// reduction — the per-field CRDT materialization is gone and the crate
/// owns result views only.
///
/// Review note (2026-09-03, issue #129): `terrabase-server` gained its
/// edge onto `terrabase-sync-core` — the S-04 reactive transports
/// (WebSocket + HTTP poll) serve the result-view subscription engine's
/// frames (D-38/D-46). The edge points strictly down the frozen layer
/// order (7 -> 3).
///
/// Review note (2026-09-03, issue #130): the S-05 opening deletion removed
/// three edges. `terrabase-replication-core` lost `terrabase-crdt` and
/// `terrabase` (the per-field LWW replica model and its in-process member
/// stand-ins are deleted; the crate keeps the progress and range-reconciliation
/// kernels over `terrabase-causal` only), and
/// `terrabase-cluster` lost `terrabase-replication-core` (its lease-backed
/// `DurableMember` adapter was the stand-in; the real WAL-frame follower
/// and journal tailer attach through the `ProductionDataPlane` trait).
/// ADR 0041 is the accepting record (GOAL D-35/D-36/D-45).
///
/// Review note (2026-09-03, issue #130 slice 2b): `terrabase-tbep` gained
/// its edge onto `terrabase-member-transport` — the cross-region lanes
/// (`production_causal`) drive the journal tailer's pull/apply path over
/// the fault-injectable `MemberTransport` seam. The edge points strictly
/// down the frozen layer order (6 -> 3), and ADR 0041 (GOAL D-45: pooled
/// QUIC member lanes carry the journal tailer) is the accepting record.
///
/// Review note (2026-09-03, E-6 / ADR 0044): `terrabase-tbep` also depends on
/// `terrabase-replication-core` for the transport-free range-based set
/// reconciliation kernel used by its journal-independent repair path. TBEP
/// owns the member-host and transport integration; the kernel remains below
/// that boundary and contains no host or network code. The edge points
/// strictly down the frozen layer order (6 -> 4), and ADR 0044's range-based
/// anti-entropy decision is the accepting record.
///
/// Review note (E-12 / ADR 0044 §4): `terrabase-sim` exercises the durable
/// Engine, fault-injectable member transport, range reconciliation, and
/// result-view state to check the specified replication and sync invariants.
/// Its four provider edges point down from the qualification layer; production
/// crates never depend on the simulation harness.
///
/// Review note (2026-09-10, dead-code deletion): `terrabase-replication-core`
/// lost its unwired `progress` and `dependency` modules (the crate now owns
/// the range-reconciliation kernel only) and `terrabase-sync-core` lost its
/// superseded pre-MessagePack `protocol` module. No dependency edges
/// changed; ADR 0044 §5 is the accepting record.
const ALLOWED_EDGES: &[(&str, &str)] = &[
    ("terrabase-sim", "terrabase"),
    ("terrabase-sim", "terrabase-member-transport"),
    ("terrabase-sim", "terrabase-replication-core"),
    ("terrabase-sim", "terrabase-sync-core"),
    ("terrabase-crdt", "terrabase-causal"),
    ("terrabase-replication-core", "terrabase-causal"),
    ("terrabase-cluster", "terrabase"),
    ("terrabase-cluster", "terrabase-member-transport"),
    ("terrabase-tbep", "terrabase"),
    ("terrabase-tbep", "terrabase-sync-core"),
    ("terrabase-tbep", "terrabase-cluster"),
    ("terrabase-tbep", "terrabase-member-transport"),
    ("terrabase-tbep", "terrabase-replication-core"),
    ("terrabase-server", "terrabase"),
    ("terrabase-server", "terrabase-cluster"),
    ("terrabase-server", "terrabase-tbep"),
    ("terrabase-server", "terrabase-sync-core"),
];

/// The engine-core package (`terrabase`) must stay dependency-minimal:
/// GOAL.md 'Dependency direction' — "A lower layer cannot import storage,
/// networking, query, Dashboard, SDK, or deployment code" — and async
/// runtimes and networking are confined to the transport and TBEP boundary
/// (`crates/terrabase-tbep/Cargo.toml` cites PD-2026-08-26-05 D1). A new
/// external dependency of the engine core requires a review note here.
///
/// Review note (2026-08-30): `ulid` joined the engine core as the second
/// external dependency. The accepting record is ADR 0027 'ULID object
/// identities', whose 'Libraries and evidence' section mandates the `ulid`
/// crate in `src/identity/`. The dependency
/// enables `std` and its transitive `rand` dependency for OS-seeded
/// cryptographic random generation. Review update (2026-09-05): replace the
/// custom BLAKE3 entropy stream with the crate's Generator and value type.
/// Boundary validation retains strict parsing; shared generation serializes
/// access and retries entropy overflow.
/// Review update (2026-09-05): owner-approved standard-library replacement
/// refactor adds serde/serde_json for persisted JSON syntax, hex for existing
/// digest encodings, and thiserror for typed error derives. SHA-256 generation
/// uses the existing aws-lc-rs dependency. Persisted bytes and public error
/// messages remain covered by the corresponding regression tests.
const ENGINE_CORE_EXTERNAL_DEPS: &[&str] =
    &["blake3", "ulid", "hex", "serde", "serde_json", "thiserror"];

/// The workspace graph as read from the manifests.
struct WorkspaceGraph {
    nodes: BTreeSet<String>,
    internal: BTreeMap<String, Vec<String>>,
    external: BTreeMap<String, Vec<String>>,
}

fn load_graph() -> WorkspaceGraph {
    let root_manifest = fsx::read_repo_file("Cargo.toml");
    let root_package =
        fsx::package_name(&root_manifest).expect("the root manifest declares a package name");
    assert_eq!(
        root_package, "terrabase",
        "the canonical engine-core package identity changed without a review note",
    );

    let mut nodes = BTreeSet::from([root_package]);
    let mut internal = BTreeMap::new();
    let mut external = BTreeMap::new();
    for member in fsx::workspace_members(&root_manifest) {
        let manifest = fsx::read_repo_file(&format!("{member}/Cargo.toml"));
        let name = fsx::package_name(&manifest)
            .unwrap_or_else(|| panic!("member {member} declares no [package] name"));
        assert!(nodes.insert(name.clone()), "duplicate workspace package name {name}",);
        let dependencies = fsx::dependency_package_names(&manifest);
        let (crate_deps, external_deps): (Vec<_>, Vec<_>) =
            dependencies.into_iter().partition(|dependency| dependency.starts_with("terrabase"));
        internal.insert(name.clone(), crate_deps);
        external.insert(name, external_deps);
    }
    WorkspaceGraph { nodes, internal, external }
}

fn layer_of(crate_name: &str) -> Option<u32> {
    LAYERS.iter().find(|(name, _)| *name == crate_name).map(|(_, layer)| *layer)
}

fn is_acyclic(graph: &WorkspaceGraph) -> bool {
    let mut indegree: BTreeMap<&str, usize> =
        graph.nodes.iter().map(|node| (node.as_str(), 0)).collect();
    let mut successors: BTreeMap<&str, Vec<&str>> =
        graph.nodes.iter().map(|node| (node.as_str(), Vec::new())).collect();
    for (consumer, providers) in &graph.internal {
        for provider in providers {
            *indegree.entry(consumer.as_str()).or_default() += 1;
            successors.entry(provider.as_str()).or_default().push(consumer.as_str());
        }
    }
    let mut ready: Vec<&str> =
        indegree.iter().filter(|entry| *entry.1 == 0).map(|(node, _)| *node).collect();
    let mut visited = 0;
    while let Some(node) = ready.pop() {
        visited += 1;
        for successor in &successors[node] {
            let degree = indegree.get_mut(successor).expect("known node");
            *degree -= 1;
            if *degree == 0 {
                ready.push(successor);
            }
        }
    }
    visited == graph.nodes.len()
}

#[test]
fn workspace_crate_dag_matches_the_frozen_allow_list() {
    let graph = load_graph();
    let allowed: BTreeSet<(&str, &str)> = ALLOWED_EDGES.iter().copied().collect();

    let mut unregistered = Vec::new();
    let mut reverse_layer = Vec::new();
    let mut unknown_layer = Vec::new();
    for (consumer, providers) in &graph.internal {
        for provider in providers {
            assert!(
                graph.nodes.contains(provider),
                "{consumer} depends on {provider}, which is not a workspace crate; an \
                 internal-named dependency must target a workspace member",
            );
            let edge = (consumer.as_str(), provider.as_str());
            if !allowed.contains(&edge) {
                unregistered.push(format!("{consumer} -> {provider}"));
            }
            match (layer_of(consumer), layer_of(provider)) {
                (Some(consumer_layer), Some(provider_layer)) if consumer_layer > provider_layer => {
                }
                (None, _) | (_, None) => {
                    unknown_layer.push(format!("{consumer} -> {provider}"));
                }
                _ => reverse_layer.push(format!("{consumer} -> {provider}")),
            }
        }
    }
    assert!(
        unregistered.is_empty(),
        "internal dependency edges missing from the frozen allow-list (each addition \
         requires a review note citing the accepting record): {}",
        unregistered.join(", "),
    );
    assert!(
        graph.internal.get("terrabase-tbep").is_some_and(|providers| providers
            .iter()
            .any(|provider| provider == "terrabase-replication-core")),
        "TBEP must retain its ADR 0044 range-reconciliation kernel dependency",
    );
    assert!(
        unknown_layer.is_empty(),
        "internal edges touching crates absent from the frozen LAYERS table (add both \
         crates to the table with a review note): {}",
        unknown_layer.join(", "),
    );
    assert!(
        reverse_layer.is_empty(),
        "reverse or equal-layer internal edges (GOAL 'Dependency direction': foundational \
         crates know nothing about their consumers): {}",
        reverse_layer.join(", "),
    );
    assert!(is_acyclic(&graph), "the workspace crate graph contains a dependency cycle",);
    for (consumer, provider) in ALLOWED_EDGES {
        assert!(
            layer_of(consumer).is_some() && layer_of(provider).is_some(),
            "allow-list edge {consumer} -> {provider} names a crate missing from LAYERS",
        );
    }
}

#[test]
fn foundational_and_engine_core_crates_have_no_internal_dependencies() {
    let graph = load_graph();
    // terrabase-causal is the deepest foundation and terrabase carries the
    // engine core (storage, families, query, index, reactive state); both must
    // stay free of internal dependencies so nothing can reverse the layering
    // through them.
    let dependency_free = ["terrabase-causal", "terrabase"];
    for crate_name in dependency_free {
        let dependencies = graph.internal.get(crate_name).cloned().unwrap_or_default();
        assert!(
            dependencies.is_empty(),
            "{crate_name} now depends on internal crates {dependencies:?}; GOAL 'Dependency \
             direction' keeps foundational and engine-core crates free of internal consumers",
        );
    }
    let crdt = graph.internal.get("terrabase-crdt").cloned().unwrap_or_default();
    assert!(
        crdt.iter().all(|dependency| dependency == "terrabase-causal"),
        "terrabase-crdt may only build on terrabase-causal; found {crdt:?}",
    );
}

#[test]
fn engine_core_external_dependencies_stay_minimal() {
    let graph = load_graph();
    let external = graph.external.get("terrabase").cloned().unwrap_or_default();
    let unexpected: Vec<&String> = external
        .iter()
        .filter(|dependency| !ENGINE_CORE_EXTERNAL_DEPS.contains(&dependency.as_str()))
        .collect();
    assert!(
        unexpected.is_empty(),
        "the engine-core crate gained external dependencies {unexpected:?} beyond the frozen \
         set {ENGINE_CORE_EXTERNAL_DEPS:?}; async runtimes and networking live at the \
         transport and TBEP boundary, and any addition here requires a review note",
    );
}
