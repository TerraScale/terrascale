//! The pinned PostgreSQL reference identity.
//!
//! The PostgreSQL baseline is a decision plus a
//! configuration**: the major series is decided here, and the observed
//! identity slot stays empty until an operator captures the server's own
//! `version()` output at a lane run. No `version()` string is invented in
//! this file, because none has ever been observed from a reference server.
//!
//! The full prose record of the pin, including every fairness assumption
//! and the reproduction steps, lives in
//! `docs/benchmarks/se15-postgresql-reference-pin.md`; this module is the
//! machine-checked summary a receipt cites.

use super::vocabulary::ReferenceRun;

/// The owner accountable for providing and running the pinned references.
pub(crate) const REFERENCE_OWNER: &str = "TerraScale benchmark lane owner (SE-15)";

/// The environment variable that names a pinned PostgreSQL reference
/// endpoint at a lane run. Only the *name* is recorded here; no endpoint
/// value, connection string, or credential belongs in source.
pub(crate) const POSTGRESQL_ENDPOINT_VARIABLE: &str = "TERRABASE_PG_REFERENCE_ENDPOINT";

/// One pinned reference baseline: what was decided, how it is configured,
/// and — separately and honestly — what has actually been observed.
#[derive(Clone, Copy, Debug)]
pub(crate) struct ReferencePin {
    /// The reference's short name.
    pub name: &'static str,
    /// The decided identity of the pin (a major series, or an exact commit).
    pub decided_version: &'static str,
    /// The exact identity observed at a lane run, if one ever was.
    ///
    /// `None` is a fact, not a gap in the documentation: it means no
    /// reference of this pin has executed under this lane, so no number
    /// from this side may appear in any comparison.
    pub observed_identity: Option<&'static str>,
    /// The server-side configuration the reference runs with, as
    /// key/value pairs, so both sides of a comparison are documented and no
    /// tuning asymmetry can hide.
    pub server_configuration: &'static [(&'static str, &'static str)],
    /// The client-side configuration the reference is driven with.
    pub client_configuration: &'static [(&'static str, &'static str)],
    /// Who owns keeping the pin current and closing its run gap.
    pub owner: &'static str,
}

impl ReferencePin {
    /// The one-line receipt form: the pin's name, its decided identity, and
    /// whether that identity has ever been observed at a run.
    pub(crate) fn describe(&self) -> String {
        let observed = match self.observed_identity {
            Some(identity) => format!("observed: {identity}"),
            None => "observed identity: none captured yet".to_owned(),
        };
        format!("{} — decided {} ({observed})", self.name, self.decided_version)
    }
}

/// The pinned PostgreSQL reference baseline (PD-2026-08-26-03 D2).
///
/// Decided: the newest fully-supported stable major series, run at that
/// series' then-current minor release, with the documented
/// default-plus-durability configuration below. **Not** decided by anyone
/// here: the exact point release, the server's own `version()` output, and
/// the artifact digest — those are observations only the reference server
/// can make, and the slot stays empty until an operator makes them at the
/// first lane run.
pub(crate) const POSTGRESQL_REFERENCE: ReferencePin = ReferencePin {
    name: "postgresql",
    decided_version: "18 (exact point release captured at the first lane run)",
    observed_identity: None,
    server_configuration: &[
        (
            "server_version",
            "PostgreSQL 18, the newest fully-supported stable major series; \
                            the minor release is whatever the pinned artifact ships, recorded \
                            verbatim from SELECT version() at the run",
        ),
        (
            "synchronous_commit",
            "on — the acknowledgement level that matches the engine's \
                                fsync-per-commit commit path",
        ),
        ("fsync", "on"),
        ("full_page_writes", "on"),
        (
            "data_directory",
            "fresh empty directory per run, on the same storage class as the \
                            TerraBaseV2 engine location, discarded afterwards",
        ),
        ("logging_collector", "off — no query logging during measured samples"),
        (
            "planner",
            "every planner default; no query hints, no auto-explain, no per-run \
                     ANALYZE beyond what warm-up naturally produces",
        ),
        (
            "shared_buffers / work_mem",
            "the pinned artifact's shipped defaults, recorded \
                                       verbatim at the run",
        ),
        (
            "schema",
            "one table users(tenant text, id text, name text, \"group\" int, payload \
                    text, PRIMARY KEY (tenant, id)) with a btree index on (tenant, name), \
                    created before warm-up",
        ),
        (
            "listen / authentication",
            "loopback-only socket, trust authentication for the \
                                     benchmark role only",
        ),
        (
            "resource limits",
            "none — no container CPU or memory cap, matching the \
                             TerraBaseV2 side",
        ),
    ],
    client_configuration: &[
        (
            "driver",
            "one connection per concurrency slot, prepared statements, no \
                    application-level cache",
        ),
        ("concurrency", "16 in-flight operations, matching the TerraBaseV2 side"),
        (
            "warmup",
            "1000 operations per workload before any measured sample, excluded from \
                    every measured sample",
        ),
        ("order", "PostgreSQL and TerraBaseV2 alternate which side runs first each sample"),
        (
            "isolation",
            "each statement runs in its own transaction (autocommit), matching \
                       the engine's per-operation acknowledgement",
        ),
    ],
    owner: REFERENCE_OWNER,
};

/// The PostgreSQL reference's run status on this host.
///
/// Fails closed: this adapter build carries no PostgreSQL client (the
/// engine workspace adds no external dependency), and no PostgreSQL server
/// exists on this host to point one at. The status therefore names the
/// concrete environment gap and the owner who must provide the pinned
/// endpoint — it never substitutes a number, a default, or a silent skip.
pub(crate) fn postgresql_reference_run() -> ReferenceRun {
    let endpoint_configured = std::env::var_os(POSTGRESQL_ENDPOINT_VARIABLE).is_some();
    let reason = if endpoint_configured {
        format!(
            "an endpoint is configured through {POSTGRESQL_ENDPOINT_VARIABLE}, but this adapter \
             build carries no PostgreSQL client — the engine workspace adds no external \
             dependency — so the reference side still cannot execute; the lane run needs the \
             operator to drive the pinned server recorded in \
             docs/benchmarks/se15-postgresql-reference-pin.md"
        )
    } else {
        "no pinned PostgreSQL reference exists on this host: no postgres, psql, or initdb \
         binary is installed and the postgresql service is inactive, and no \
         TERRABASE_PG_REFERENCE_ENDPOINT is configured; the operator must provide the pinned \
         PostgreSQL 18 endpoint and capture its SELECT version() output"
            .to_owned()
    };
    ReferenceRun::Unrun { reason, owner: REFERENCE_OWNER }
}
