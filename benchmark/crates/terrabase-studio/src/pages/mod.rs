//! The studio's server-rendered pages.
//!
//! Every page is one function returning a complete HTML document, and every
//! action endpoint returns Datastar events that patch one region of one of
//! these pages. Nothing renders without the typed outcome of what the engine
//! actually did.

pub mod backups;
pub mod documents;
pub mod indexes;
pub mod metrics_page;
pub mod overview;
pub mod query;
pub mod replication;
pub mod surfaces;
pub mod tiers;
pub mod usage;

#[cfg(test)]
mod tests {
    use super::*;
    use crate::state::{Config, StudioState};

    const TEST_NONCE: &str = "00112233445566778899aabbccddeeff";

    fn studio(name: &str) -> StudioState {
        let directory = std::env::temp_dir().join(format!(
            "terrabase-studio-pages-{name}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        StudioState::open(Config {
            data_dir: directory,
            addr: crate::state::DEFAULT_ADDR.to_owned(),
        })
        .expect("the scratch studio opens")
    }

    fn with_samples(name: &str) -> StudioState {
        let state = studio(name);
        crate::routes::test_seed_sample(&state).expect("the samples write");
        state
    }

    #[test]
    fn overview_page_carries_the_live_stream_marker_and_metrics_panel() {
        let page = overview::render(&studio("overview"), TEST_NONCE);
        assert!(page.contains("<!DOCTYPE html>"));
        assert!(page.contains("TerraBase Studio"));
        assert!(page.contains("data-init=\"@get('/events/metrics'"));
        assert!(page.contains("id=\"metrics-panel\""));
        assert!(page.contains("published sequence"));
        assert!(page.contains("Index catalog"));
        assert!(page.contains("Storage tier mover"));
        assert!(page.contains("/static/basecoat.css"), "the vendored stylesheet is linked");
        assert!(page.contains("/static/datastar-1.0.3.js"), "official Datastar is linked");
        assert!(page.contains("/static/studio-monaco.js"), "the Monaco-only glue is linked");
        assert!(page.contains(&format!("data-nonce=\"{TEST_NONCE}\"")));
        assert!(page.contains("loopback"), "the lane limit is stated in the shell");
    }

    #[test]
    fn documents_page_carries_the_list_the_editor_and_the_honest_limit() {
        let page = documents::render(
            &with_samples("documents"),
            &documents::Selection {
                tenant: "demo".to_owned(),
                collection: "users".to_owned(),
                page: 0,
            },
            TEST_NONCE,
        );
        assert!(page.contains("id=\"document-rows\""));
        assert!(page.contains("id=\"document-view\""));
        assert!(page.contains("id=\"editor-receipt\""));
        assert!(page.contains("id=\"delete-receipt\""));
        assert!(page.contains("@post('/documents/put'"));
        assert!(page.contains("@post('/documents/delete'"));
        assert!(page.contains("data-monaco-json"));
        assert!(!page.contains("data-monaco-value"));
        assert!(page.contains("data-monaco-fallback"));
        assert!(page.contains("confirm("), "the delete asks for confirmation");
        assert!(page.contains("no tenant or collection enumeration"), "the honest limit is stated");
        assert!(page.contains("sample-000"), "the sample rows render");
    }

    #[test]
    fn documents_rows_fragment_reports_the_pager_counts() {
        let state = with_samples("rows");
        let fragment = documents::rows_fragment(
            &state,
            &documents::Selection {
                tenant: "demo".to_owned(),
                collection: "users".to_owned(),
                page: 0,
            },
        );
        assert!(fragment.contains("document(s) matched"));
        assert!(fragment.contains("page 1 of"));
        assert!(fragment.contains("Previous"));
        assert!(fragment.contains("Next"));
    }

    #[test]
    fn query_page_carries_the_builder_and_every_condition_kind() {
        let page = query::render(&studio("query"), &None, TEST_NONCE);
        assert!(page.contains("id=\"query-result\""));
        assert!(page.contains("@post('/query/run'"));
        for (_, label) in query::CONDITION_KINDS {
            assert!(page.contains(&format!(">{label}</option>")), "missing {label}");
        }
        assert!(page.contains("Index selection"), "the plan panel is announced");
    }

    #[test]
    fn replication_page_names_what_needs_a_cluster() {
        let page = replication::render(&studio("replication"), TEST_NONCE);
        assert!(page.contains("Region catalog"));
        assert!(page.contains("Topology view"));
        assert!(page.contains("Not exposed in-process"));
        assert!(page.contains("AcknowledgementReport"), "the omitted types are named");
        assert!(page.contains("No replication members are running"));
        assert!(page.contains("gru"), "the seeded topology renders");
        assert!(page.contains("submit durability"));
        assert!(page.contains("toggle inactive voter"));
    }

    #[test]
    fn tiers_page_carries_every_control_and_the_dry_run_note() {
        let page = tiers::render(&studio("tiers"), TEST_NONCE);
        assert!(page.contains("@post('/tiers/configure'"));
        assert!(page.contains("@post('/tiers/pause'"));
        assert!(page.contains("@post('/tiers/resume'"));
        assert!(page.contains("@post('/tiers/dry-run'"));
        assert!(page.contains("@post('/tiers/run'"));
        assert!(page.contains("No continuous scheduler"), "the honest limit is stated");
        assert!(page.contains("is the explain control"));
    }

    #[test]
    fn usage_page_reads_the_authoritative_durable_ledger() {
        let page = usage::render(&studio("usage"), "demo", TEST_NONCE);
        assert!(page.contains("Meter catalog"));
        assert!(page.contains("stored byte hours"));
        assert!(page.contains("id=\"usage-ledger\""));
        assert!(page.contains("Authoritative durable facts"));
        assert!(page.contains("The engine is authoritative"));
        assert!(page.contains("record usage fact"));
        assert!(!page.contains("studio-local and in-memory"));
    }

    #[test]
    fn usage_page_renders_a_durable_idempotent_fact() {
        let studio = studio("usage-durable");
        let first = usage::record_fact(
            &studio,
            "demo",
            "admitted-request-units",
            "studio",
            "studio",
            "p0",
            "GRU",
            "usage-page-test",
            "7",
            1_700_000_000_000,
        )
        .expect("durable usage record");
        assert!(first.contains("already recorded: false"));
        let replay = usage::record_fact(
            &studio,
            "demo",
            "admitted-request-units",
            "studio",
            "studio",
            "p0",
            "GRU",
            "usage-page-test",
            "7",
            1_700_000_000_000,
        )
        .expect("idempotent durable replay");
        assert!(replay.contains("already recorded: true"));

        let page = usage::render(&studio, "demo", TEST_NONCE);
        assert!(page.contains("StableAdmittedRequest:usage-page-test"));
        assert!(page.contains("demo/studio/studio/p0/GRU"));
        assert!(page.contains(">7</td>"));
    }

    #[test]
    fn backups_page_carries_backup_restore_and_the_deletion_workflow() {
        let studio = studio("backups");
        let page = backups::render(&studio, Ok(&[]), TEST_NONCE);
        assert!(page.contains("@post('/backup/configure'"));
        assert!(page.contains("@post('/backup/run'"));
        assert!(page.contains("@post('/backup/restore'"));
        assert!(page.contains("id=\"backup-chain\""));
        assert!(page.contains("id=\"deletion-state\""));
        assert!(page.contains("@post('/deletion/request'"));
        assert!(page.contains("verify backup chain"));
        assert!(page.contains("Requested"), "the workflow states are explained");
        assert!(page.contains("Scheduler lifetime"), "the honest limit is stated");
    }

    #[test]
    fn surfaces_page_exposes_real_local_family_stream_and_health_lanes() {
        let page = surfaces::render(&studio("surfaces"), TEST_NONCE);
        assert!(page.contains("Engine surfaces"));
        assert!(page.contains("/families/validation"));
        assert!(page.contains("/streams/documents"));
        assert!(page.contains("healthz"));
        assert!(page.contains("Relationships"));
        assert!(page.contains("never fabricates"));
        assert!(page.contains("bounded schema editor"));
        assert!(!page.contains("future schema editor"));
    }
}
