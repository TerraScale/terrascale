//! The live metrics snapshot and the panel fragment it renders.
//!
//! The panel is rendered here rather than in a page module because two callers
//! need byte-identical markup: the Overview page's first paint, and the
//! long-lived SSE connection that replaces it every two seconds.

use std::collections::BTreeSet;
use std::fmt::Write;
use std::path::Path;
use std::time::Duration;

use terrabase::query::optimizer::IndexKind;
use terrabase::{Engine, TenantId};

use crate::engine_ops::{self, OpError};
use crate::html::{self, Tone};
use crate::state::StudioState;

/// How often the live connection pushes a refreshed panel.
pub const REFRESH: Duration = Duration::from_secs(2);

/// The identifier the panel fragment is merged into.
pub const PANEL_ID: &str = "metrics-panel";

/// Hard limits for the best-effort directory walk. A damaged or unexpectedly
/// large location must not monopolize the blocking worker used by the UI.
const MAX_MEASURED_FILES: u64 = 100_000;
const MAX_MEASURED_BYTES: u64 = 1 << 50;

/// One tenant/collection count row.
pub struct CollectionCount {
    /// The tenant name.
    pub tenant: String,
    /// The collection name.
    pub collection: String,
    /// How many documents the collection holds, or the failure that stopped
    /// the count.
    pub documents: Result<u64, String>,
}

/// One row of the index catalog table.
pub struct IndexRow {
    /// The index name, for example `ByLevel` or an `Auto/…` namespace.
    pub name: String,
    /// Whether the engine created it automatically.
    pub automatic: bool,
    /// The collection it covers.
    pub collection: String,
    /// Its operational or lifecycle state, rendered with its tone.
    pub state: (Tone, String),
    /// Its entry count, when the catalog reports one.
    pub entries: Option<usize>,
    /// The generation it is published at, when known.
    pub published: Option<u64>,
}

/// Everything the overview panel shows, gathered in one pass.
pub struct Snapshot {
    /// The crate version of the engine library.
    pub engine_version: &'static str,
    /// The engine's repository status marker.
    pub engine_status: &'static str,
    /// The published storage sequence.
    pub published_sequence: u64,
    /// Bytes on disk under the engine location, measured by the studio.
    pub location_bytes: u64,
    /// Files on disk under the engine location, measured by the studio.
    pub location_files: u64,
    /// Whether the directory walk reached one of its safety limits.
    pub location_measurement_capped: bool,
    /// Document counts per registered tenant and collection.
    pub counts: Vec<CollectionCount>,
    /// The explicit and automatic indexes the catalog reports.
    pub indexes: Vec<IndexRow>,
    /// The tier mover's state view.
    pub tiers: terrabase::tiers::TierStateView,
    /// The configured tier policy, summarised.
    pub tier_policy: Option<String>,
    /// Whether Studio's S3 scheduler is enabled.
    pub backup_scheduler_enabled: bool,
    /// Successful S3 backup attempts in this process.
    pub backup_successes: u64,
    /// Failed S3 backup attempts in this process.
    pub backup_failures: u64,
    /// Latest successful S3 backup time as Unix milliseconds, or zero.
    pub backup_last_success_millis: u64,
}

/// Gathers the snapshot by probing the engine through its public surface.
///
/// Every count is a real typed query against a real snapshot, so a tenant the
/// registry names but the engine never stored reports zero rather than an
/// error; a probe that genuinely fails reports the failure in place.
pub fn gather(state: &StudioState) -> Snapshot {
    let engine = state.engine.as_ref();
    let sequence =
        engine_ops::published_sequence(engine).map(|value| value.get()).unwrap_or_default();
    let (location_bytes, location_files, location_measurement_capped) =
        measure(state.config.data_dir.as_path());
    let counts = collection_counts(state, engine);
    let indexes = index_rows(state, engine);

    let administration = engine.tiers();
    let tier_policy = administration.policy().map(|policy| {
        format!(
            "{} v{} · budget {} · pinned {} segment(s)",
            policy.policy_id(),
            policy.version(),
            html::human_bytes(policy.nvme_budget_bytes()),
            policy.pinned_segment_ids().len()
        )
    });

    Snapshot {
        engine_version: terrabase::VERSION,
        engine_status: terrabase::STATUS,
        published_sequence: sequence,
        location_bytes,
        location_files,
        location_measurement_capped,
        counts,
        indexes,
        tiers: administration.inspect(),
        tier_policy,
        backup_scheduler_enabled: state.backup_settings().enabled,
        backup_successes: state.backup_successes(),
        backup_failures: state.backup_failures(),
        backup_last_success_millis: state.backup_last_success_millis(),
    }
}

/// Renders the private Prometheus text exposition for one snapshot.
///
/// Labels are deliberately limited to fixed engine vocabularies. Tenant,
/// collection, document, query, path, operation, and error text never appear
/// in the output.
#[must_use]
pub fn render_prometheus(snapshot: &Snapshot) -> String {
    let mut output = String::new();
    let _ = writeln!(output, "# HELP terrabase_build_info Static engine build information.");
    let _ = writeln!(output, "# TYPE terrabase_build_info gauge");
    let _ = writeln!(
        output,
        "terrabase_build_info{{version=\"{}\"}} 1",
        prometheus_label(snapshot.engine_version)
    );
    metric(
        &mut output,
        "terrabase_storage_published_sequence",
        "Latest published storage sequence.",
        snapshot.published_sequence,
    );
    metric(
        &mut output,
        "terrabase_storage_location_bytes",
        "Bytes under the configured engine location.",
        snapshot.location_bytes,
    );
    metric(
        &mut output,
        "terrabase_storage_location_files",
        "Files under the configured engine location.",
        snapshot.location_files,
    );

    let mut documents = 0_u64;
    let mut failed_collection_probes = 0_u64;
    for count in &snapshot.counts {
        match &count.documents {
            Ok(value) => documents = documents.saturating_add(*value),
            Err(_) => failed_collection_probes = failed_collection_probes.saturating_add(1),
        }
    }
    metric(
        &mut output,
        "terrabase_documents_total",
        "Documents observed through successful bounded Studio probes.",
        documents,
    );
    metric(
        &mut output,
        "terrabase_metrics_probe_failures",
        "Collection probes that failed while assembling this scrape.",
        failed_collection_probes,
    );

    let automatic = snapshot.indexes.iter().filter(|index| index.automatic).count();
    let explicit = snapshot.indexes.len().saturating_sub(automatic);
    let _ = writeln!(output, "# HELP terrabase_indexes Catalog indexes by fixed kind.");
    let _ = writeln!(output, "# TYPE terrabase_indexes gauge");
    let _ = writeln!(output, "terrabase_indexes{{kind=\"automatic\"}} {automatic}");
    let _ = writeln!(output, "terrabase_indexes{{kind=\"explicit\"}} {explicit}");

    let _ = writeln!(output, "# HELP terrabase_tier_state Storage-tier control flags.");
    let _ = writeln!(output, "# TYPE terrabase_tier_state gauge");
    let _ = writeln!(
        output,
        "terrabase_tier_state{{state=\"configured\"}} {}",
        u8::from(snapshot.tiers.configured())
    );
    let _ = writeln!(
        output,
        "terrabase_tier_state{{state=\"paused\"}} {}",
        u8::from(snapshot.tiers.paused())
    );
    metric(
        &mut output,
        "terrabase_backup_scheduler_enabled",
        "Whether Studio's S3 backup scheduler is enabled.",
        u64::from(snapshot.backup_scheduler_enabled),
    );
    let _ = writeln!(output, "# HELP terrabase_backup_runs_total S3 backup attempts by outcome.");
    let _ = writeln!(output, "# TYPE terrabase_backup_runs_total counter");
    let _ = writeln!(
        output,
        "terrabase_backup_runs_total{{outcome=\"success\"}} {}",
        snapshot.backup_successes
    );
    let _ = writeln!(
        output,
        "terrabase_backup_runs_total{{outcome=\"failure\"}} {}",
        snapshot.backup_failures
    );
    metric(
        &mut output,
        "terrabase_backup_last_success_timestamp_seconds",
        "Unix time of the latest successful S3 backup in seconds, or zero.",
        snapshot.backup_last_success_millis / 1_000,
    );
    output
}

fn metric(output: &mut String, name: &str, help: &str, value: u64) {
    let _ = writeln!(output, "# HELP {name} {help}");
    let _ = writeln!(output, "# TYPE {name} gauge");
    let _ = writeln!(output, "{name} {value}");
}

fn prometheus_label(value: &str) -> String {
    value.replace('\\', "\\\\").replace('"', "\\\"").replace('\n', "\\n")
}

fn collection_counts(state: &StudioState, engine: &Engine) -> Vec<CollectionCount> {
    let mut counts = Vec::new();
    for tenant_name in state.tenants() {
        let Ok(tenant) = TenantId::try_new(&tenant_name) else { continue };
        for collection_name in state.collections_of(&tenant_name) {
            let documents = count_documents(engine, &tenant, &collection_name);
            counts.push(CollectionCount {
                tenant: tenant_name.clone(),
                collection: collection_name,
                documents,
            });
        }
    }
    counts
}

fn count_documents(engine: &Engine, tenant: &TenantId, collection: &str) -> Result<u64, String> {
    let name = terrabase::types::CollectionName::try_new(collection)
        .map_err(|error| format!("unusable collection name: {error:?}"))?;
    let page = engine_ops::list_documents(engine, tenant, &name, 0, 1)
        .map_err(|error| error.to_string())?;
    Ok(page.matched)
}

/// Builds the catalog rows for every tenant the studio has observed.
///
/// Explicit indexes answer through [`terrabase::IndexStore::statistics`],
/// which carries the entry count and operational state; automatic indexes are
/// not addressable through the explicit lifecycle seam (the store refuses an
/// `Auto/` namespace with `NotExplicitIndex`), so they answer through
/// `registered_indexes` and its lifecycle state instead.
fn index_rows(state: &StudioState, engine: &Engine) -> Vec<IndexRow> {
    let store = engine.queries();
    let indexes = engine.indexes();
    let mut rows: Vec<IndexRow> = Vec::new();
    let mut seen: BTreeSet<(String, String)> = BTreeSet::new();

    for tenant_name in state.tenants() {
        let Ok(tenant) = TenantId::try_new(&tenant_name) else { continue };
        for statistics in indexes.statistics(&tenant) {
            let name = statistics.name().to_owned();
            seen.insert((tenant_name.clone(), name.clone()));
            let state = match statistics.state() {
                terrabase::IndexOperationalState::Normal => (Tone::Good, "normal".to_owned()),
                terrabase::IndexOperationalState::Paused => (Tone::Warn, "paused".to_owned()),
                terrabase::IndexOperationalState::Disabled => {
                    (Tone::Neutral, "disabled".to_owned())
                }
                terrabase::IndexOperationalState::Error => (Tone::Bad, "error".to_owned()),
            };
            rows.push(IndexRow {
                name,
                automatic: false,
                collection: statistics_name_of(&store, &tenant, statistics.name()),
                state,
                entries: Some(statistics.entries()),
                published: statistics.published().map(|generation| generation.generation()),
            });
        }
    }

    for tenant_name in state.tenants() {
        let Ok(tenant) = TenantId::try_new(&tenant_name) else { continue };
        for registered in store.registered_indexes(&tenant) {
            if registered.kind() != IndexKind::Auto {
                continue;
            }
            let name = registered.name().to_owned();
            if !seen.insert((tenant_name.clone(), name.clone())) {
                continue;
            }
            let state = match registered.state() {
                terrabase::query::optimizer::IndexLifecycleState::Active => {
                    (Tone::Good, "active".to_owned())
                }
                terrabase::query::optimizer::IndexLifecycleState::Idle => {
                    (Tone::Warn, "idle".to_owned())
                }
                terrabase::query::optimizer::IndexLifecycleState::Retired => {
                    (Tone::Neutral, "retired".to_owned())
                }
            };
            rows.push(IndexRow {
                name,
                automatic: true,
                collection: registered.collection().to_owned(),
                state,
                entries: None,
                published: registered.published().map(|generation| generation.generation()),
            });
        }
    }

    rows.sort_by(|left, right| left.name.cmp(&right.name));
    rows
}

/// The collection an explicit index covers, read back from the registry.
fn statistics_name_of(store: &terrabase::QueryStore<'_>, tenant: &TenantId, name: &str) -> String {
    store
        .registered_indexes(tenant)
        .into_iter()
        .find(|registered| registered.name() == name)
        .map(|registered| registered.collection().to_owned())
        .unwrap_or_else(|| "—".to_owned())
}

fn measure(root: &Path) -> (u64, u64, bool) {
    let mut bytes = 0;
    let mut files = 0;
    let mut stack = vec![root.to_owned()];
    let mut capped = false;
    while let Some(directory) = stack.pop() {
        let Ok(entries) = std::fs::read_dir(&directory) else { continue };
        for entry in entries.flatten() {
            let Ok(kind) = entry.file_type() else { continue };
            if kind.is_dir() {
                stack.push(entry.path());
            } else {
                if files >= MAX_MEASURED_FILES {
                    capped = true;
                    return (bytes, files, capped);
                }
                files += 1;
                bytes = bytes
                    .saturating_add(entry.metadata().map(|meta| meta.len()).unwrap_or_default());
                if bytes >= MAX_MEASURED_BYTES {
                    capped = true;
                    return (MAX_MEASURED_BYTES, files, capped);
                }
            }
        }
    }
    (bytes, files, capped)
}

/// Renders the metrics panel fragment.
///
/// The root carries [`PANEL_ID`] so both a full-page render and an SSE `outer`
/// merge can target it by identifier.
pub fn render_panel(snapshot: &Snapshot) -> String {
    format!(
        "<div id=\"{PANEL_ID}\" class=\"grid gap-6\">\n\
<div class=\"grid gap-6 lg:grid-cols-2\">{identity}{durability}</div>\n\
{counts_card}\n\
{index_card}\n\
{tier_card}\n\
</div>\n",
        PANEL_ID = PANEL_ID,
        identity = html::card(
            "Engine",
            &html::definition(&[
                ("engine version", snapshot.engine_version.to_owned()),
                ("status marker", snapshot.engine_status.to_owned()),
                ("published sequence", snapshot.published_sequence.to_string()),
            ])
        ),
        durability = html::card(
            "Durable location",
            &html::definition(&[
                ("bytes on disk (studio-measured)", html::human_bytes(snapshot.location_bytes)),
                ("files on disk", snapshot.location_files.to_string()),
                (
                    "measurement",
                    if snapshot.location_measurement_capped {
                        "capped at the Studio safety limit".to_owned()
                    } else {
                        "complete directory walk".to_owned()
                    },
                ),
                (
                    "note",
                    "byte counts are the studio's own directory walk: the engine's public in-process surface exposes no byte metric".to_owned(),
                ),
            ])
        ),
        counts_card = html::card(
            "Documents per tenant and collection",
            &html::table(&["tenant", "collection", "documents"], &[counts_table(snapshot)], "No documents yet.")
        ),
        index_card = html::card(
            "Index catalog",
            &html::table(
                &["index", "kind", "collection", "state", "entries", "generation"],
                &[indexes_table(snapshot)],
                "No index registered."
            )
        ),
        tier_card = render_tier_card(snapshot),
    )
}

fn counts_table(snapshot: &Snapshot) -> String {
    if snapshot.counts.is_empty() {
        return String::new();
    }
    let mut rows = String::new();
    for count in &snapshot.counts {
        let value = match &count.documents {
            Ok(number) => html::cell(&number.to_string()),
            Err(failure) => html::cell_raw(&html::badge(Tone::Bad, failure)),
        };
        rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        rows.push_str(&html::cell(&count.tenant));
        rows.push_str(&html::cell(&count.collection));
        rows.push_str(&value);
        rows.push_str("</tr>");
    }
    rows
}

fn indexes_table(snapshot: &Snapshot) -> String {
    let mut rows = String::new();
    for row in &snapshot.indexes {
        let kind = if row.automatic {
            html::badge(Tone::Neutral, "auto")
        } else {
            html::badge(Tone::Neutral, "explicit")
        };
        rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        rows.push_str(&html::cell(&row.name));
        rows.push_str(&html::cell_raw(&kind));
        rows.push_str(&html::cell(&row.collection));
        rows.push_str(&html::cell_raw(&html::badge(row.state.0, &row.state.1)));
        rows.push_str(&html::cell(
            &row.entries.map(|value| value.to_string()).unwrap_or_else(|| "—".to_owned()),
        ));
        rows.push_str(&html::cell(
            &row.published.map(|value| value.to_string()).unwrap_or_else(|| "—".to_owned()),
        ));
        rows.push_str("</tr>");
    }
    rows
}

fn render_tier_card(snapshot: &Snapshot) -> String {
    let (tone, label) = if snapshot.tiers.paused() {
        (Tone::Warn, "paused")
    } else if snapshot.tiers.configured() {
        (Tone::Good, "configured")
    } else {
        (Tone::Neutral, "unconfigured")
    };
    let body = format!(
        "<div class=\"grid gap-4\"><div>{}</div>{}</div>",
        html::badge(tone, label),
        html::definition(&[
            (
                "policy",
                snapshot.tier_policy.clone().unwrap_or_else(|| "none configured".to_owned())
            ),
            ("manifest epoch", snapshot.tiers.manifest_epoch().to_string()),
            ("objects", snapshot.tiers.object_count().to_string()),
            ("NVMe resident", html::human_bytes(snapshot.tiers.nvme_bytes())),
        ])
    );
    html::card("Storage tier mover", &body)
}

/// The signals payload that keeps the sidebar sequence live.
pub fn sequence_signals(snapshot: &Snapshot) -> String {
    format!("{{\"engine_sequence\": \"seq {}\"}}", snapshot.published_sequence)
}

/// Runs one index lifecycle action through the explicit-index seam.
pub fn index_action(
    state: &StudioState,
    tenant_name: &str,
    name: &str,
    action: &str,
) -> Result<String, OpError> {
    let tenant = engine_ops::tenant(tenant_name)?;
    let namespace = terrabase::query::IndexNamespace::try_new(tenant, name)
        .map_err(|error| engine_ops::validation_error("index namespace", error))?;
    let store = state.engine.indexes();
    let outcome = match action {
        "start" => store.start(&namespace).map(|value| format!("transition {value:?}")),
        "stop" => store.stop(&namespace).map(|value| format!("transition {value:?}")),
        "disable" => store.disable(&namespace).map(|value| format!("transition {value:?}")),
        "enable" => store.enable(&namespace).map(|value| format!("transition {value:?}")),
        "rebuild" => store.rebuild(&namespace).map(|value| format!("step {value:?}")),
        "reset-side-by-side" => store
            .reset(&namespace, terrabase::IndexResetMode::SideBySide)
            .map(|value| format!("transition {value:?}")),
        "clear-errors" => {
            store.clear_errors(&namespace).map(|value| format!("transition {value:?}"))
        }
        "wait" => store
            .wait_for_non_stale(&namespace, terrabase::WaitForNonStale::default())
            .map(|value| format!("wait {value:?}")),
        other => return Err(OpError::new(format!("unknown index action `{other}`"))),
    };
    outcome.map_err(|error| OpError(format!("index action refused: {error:?}")))
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicU64, Ordering};

    use crate::s3_backup::S3BackupSettings;
    use crate::state::{Config, DEFAULT_ADDR};

    use super::*;

    #[test]
    fn prometheus_exposes_bounded_s3_scheduler_outcomes() {
        static COUNTER: AtomicU64 = AtomicU64::new(0);
        let index = COUNTER.fetch_add(1, Ordering::Relaxed);
        let state = StudioState::open(Config {
            data_dir: std::env::temp_dir()
                .join(format!("terrabase-studio-backup-metrics-{}-{index}", std::process::id(),)),
            addr: DEFAULT_ADDR.to_owned(),
        })
        .unwrap();
        state
            .set_backup_settings(S3BackupSettings {
                enabled: true,
                bucket: "backups".to_owned(),
                ..S3BackupSettings::default()
            })
            .unwrap();
        state.record_backup_run(true);
        state.record_backup_run(false);
        state.record_backup_run(false);

        let output = render_prometheus(&gather(&state));
        assert!(output.contains("terrabase_backup_scheduler_enabled 1\n"));
        assert!(output.contains("terrabase_backup_runs_total{outcome=\"success\"} 1\n"));
        assert!(output.contains("terrabase_backup_runs_total{outcome=\"failure\"} 2\n"));
        let last_success = output
            .lines()
            .find(|line| line.starts_with("terrabase_backup_last_success_timestamp_seconds "))
            .unwrap();
        assert_ne!(last_success, "terrabase_backup_last_success_timestamp_seconds 0");
    }
}
