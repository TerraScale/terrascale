//! The Usage page: the meter catalog, and the segment ledger's summary.

use std::collections::BTreeMap;

use terrabase::{MeterCatalog, MeterKind, SegmentId, SequenceInterval, UsageScope, UsageSegment};

use crate::html::{self, ButtonStyle, Tone};
use crate::state::StudioState;

/// The identifier the ledger summary is merged into.
pub const LEDGER_ID: &str = "usage-ledger";

/// Renders the Usage page.
pub fn render(state: &StudioState) -> String {
    let body = format!(
        "{heading}\n\
{honest}\n\
{catalog}\n\
{ledger}\n\
{record}",
        heading = html::heading(
            "Usage",
            "The engine's meter vocabulary, and the folded summary of the segments the studio has recorded.",
        ),
        honest = html::alert(
            Tone::Warn,
            "There is no durable usage ledger bound to this engine location",
            "The engine's usage domain is a pure value domain: UsageSegmentLedger is constructed with `default()`, holds its segments in memory, has no open path, and no aggregate read. Nothing in the engine's public API attaches a usage ledger to a location. The ledger below is therefore studio-local and in-memory — it is lost when the studio stops, and the page says so rather than presenting it as engine state.",
        ),
        catalog = catalog_card(),
        ledger = ledger_card(state),
        record = record_card(state),
    );
    html::page("Usage", "/usage", "", &body)
}

fn meter_name(kind: MeterKind) -> &'static str {
    match kind {
        MeterKind::StoredByteHours => "stored byte hours",
        MeterKind::AdmittedRequestUnits => "admitted request units",
        MeterKind::PublicEgressBytes => "public egress bytes",
        MeterKind::ActiveRegionSeats => "active region seats",
        MeterKind::BackupByteHours => "backup byte hours",
        MeterKind::RestoreWork => "restore work",
    }
}

fn catalog_card() -> String {
    let catalog = MeterCatalog::initial();
    let mut rows = String::new();
    for definition in catalog.definitions() {
        rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        rows.push_str(&html::cell(meter_name(definition.kind())));
        rows.push_str(&html::cell(&format!("v{}", definition.version())));
        rows.push_str(&html::cell(&format!("{:?}", definition.unit())));
        rows.push_str(&html::cell(&format!("{:?}", definition.charge_scope())));
        rows.push_str(&html::cell(&format!("{:?}", definition.minting())));
        rows.push_str(&html::cell(&format!("{:?}", definition.cadence())));
        rows.push_str("</tr>");
    }
    let body = format!(
        "{}\n{}",
        html::table(
            &["meter", "version", "unit", "charge scope", "minting rule", "cadence"],
            &[rows],
            "No meter defined.",
        ),
        html::alert(
            Tone::Neutral,
            "The meter table is closed",
            &format!(
                "A meter definition is only valid when its unit, charge scope, minting rule, cadence, and correction procedure match the engine's fixed table for that kind, so there is no custom meter. The format version is {} and a segment may not exceed {} bytes or span more than {} sequences.",
                terrabase::USAGE_FORMAT_VERSION,
                terrabase::usage::MAX_SEGMENT_BYTES,
                terrabase::usage::MAX_SEQUENCE_SPAN,
            ),
        ),
    );
    html::card("Meter catalog", &body)
}

/// One folded meter total.
pub struct MeterTotal {
    /// The meter kind.
    pub kind: MeterKind,
    /// The net total, corrections included.
    pub total: i128,
    /// How many segments contributed, and how many were corrections.
    pub segments: usize,
    pub corrections: usize,
}

/// Folds the ledger into per-meter net totals.
///
/// Corrections are sign-bearing, so a naive sum is the corrected net; the
/// counts beside it make that visible instead of hiding it.
pub fn fold(ledger: &terrabase::UsageSegmentLedger) -> Vec<MeterTotal> {
    let mut folded: BTreeMap<MeterKind, (i128, usize, usize)> = BTreeMap::new();
    for segment in ledger.segments() {
        let entry = folded.entry(segment.meter()).or_insert((0, 0, 0));
        entry.0 += segment.integer_total();
        entry.1 += 1;
        if segment.correction_of().is_some() {
            entry.2 += 1;
        }
    }
    folded
        .into_iter()
        .map(|(kind, (total, segments, corrections))| MeterTotal {
            kind,
            total,
            segments,
            corrections,
        })
        .collect()
}

fn ledger_card(state: &StudioState) -> String {
    let ledger = state.usage.lock().unwrap_or_else(|poison| poison.into_inner());
    let mut summary = String::new();
    let totals = fold(&ledger);
    for total in &totals {
        summary.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        summary.push_str(&html::cell(meter_name(total.kind)));
        summary.push_str(&html::cell(&total.total.to_string()));
        summary.push_str(&html::cell(&total.segments.to_string()));
        summary.push_str(&html::cell(&total.corrections.to_string()));
        summary.push_str("</tr>");
    }
    let mut segments = String::new();
    for segment in ledger.segments() {
        segments.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        segments.push_str(&html::cell(segment.segment_id().as_str()));
        segments.push_str(&html::cell(meter_name(segment.meter())));
        segments.push_str(&html::cell(&segment.integer_total().to_string()));
        segments.push_str(&html::cell(&format!(
            "{}–{}",
            segment.interval().first(),
            segment.interval().last()
        )));
        segments.push_str(&html::cell(
            &segment
                .correction_of()
                .map(|id| id.as_str().to_owned())
                .unwrap_or_else(|| "—".to_owned()),
        ));
        segments.push_str("</tr>");
    }
    let body = format!(
        "<div id=\"{LEDGER_ID}\" class=\"grid gap-6\">{summary_card}{segments_card}</div>",
        LEDGER_ID = LEDGER_ID,
        summary_card = html::card(
            "Folded totals",
            &html::table(
                &["meter", "net total", "segments", "of which corrections"],
                &[summary],
                "No segment recorded yet."
            )
        ),
        segments_card = html::card(
            "Segments",
            &html::table(
                &["segment", "meter", "total", "sequences", "corrects"],
                &[segments],
                "No segment recorded yet."
            )
        ),
    );
    html::card("Studio-local segment ledger", &body)
}

fn record_card(state: &StudioState) -> String {
    let mut tenant_names = state.tenants();
    if tenant_names.is_empty() {
        tenant_names.push("demo".to_owned());
    }
    let options =
        tenant_names.iter().map(|name| html::option(name, name, true)).collect::<Vec<_>>().concat();
    let body = format!(
        "<p class=\"mb-4 text-sm text-muted-foreground\">Record one stored-byte-hour segment for the selected tenant, measured from the engine's own published facts: the studio counts the tenant's documents across its registered collections and uses the engine location's byte count as the measured quantity. The segment is built with the engine's own validators and inserted into the studio's ledger, so a malformed window or a zero delta reports the typed usage error.</p>\n\
<form class=\"grid gap-4\" data-on-submit=\"$$submit('/usage/record')\">\n{tenant}{project}{partition}\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary} w-fit\">Record a stored-byte-hour segment</button>\n\
</form>\n{receipt}",
        tenant = html::select("tenant", "Tenant", &options),
        project = html::text_input("project", "Project id", "studio", "studio"),
        partition = html::text_input("partition", "Partition id", "p0", "p0"),
        primary = ButtonStyle::Primary.classes(),
        receipt = html::receipt_region_with("usage-receipt"),
    );
    html::card("Record a measurement", &body)
}

/// Builds and inserts one stored-byte-hour segment from the engine's facts.
///
/// The measurement window is the hour that just closed, the sequence interval
/// spans the committed sequences the studio can observe, and the region is the
/// first region of the operator's topology — every input is read from a real
/// surface, never invented.
pub fn record_segment(
    state: &StudioState,
    tenant_name: &str,
    project: &str,
    partition: &str,
    now_millis: u64,
) -> Result<String, String> {
    let tenant = terrabase::TenantId::try_new(tenant_name.trim())
        .map_err(|error| format!("tenant: {error:?}"))?;
    let region = state
        .topology()
        .regions
        .first()
        .map(|region| region.name.clone())
        .ok_or_else(|| "the topology has no region to attribute the segment to".to_owned())?;
    let region_id = terrabase::RegionCode::from_text(&region.to_uppercase())
        .map_err(|error| format!("region: {error:?}"))?;

    let published = crate::engine_ops::published_sequence(state.engine.as_ref())
        .map_err(|error| error.to_string())?
        .get();
    let documents = count_documents(state, tenant_name);

    let scope = UsageScope::try_new(tenant, project.trim(), "studio", partition.trim(), region_id)
        .map_err(|error| format!("usage scope: {error:?}"))?;
    let definition = MeterCatalog::initial()
        .get(MeterKind::StoredByteHours, 1)
        .copied()
        .ok_or_else(|| "the stored-byte-hour meter is not in the catalog".to_owned())?;
    let hour_start = now_millis.saturating_sub(3_600_000);
    let segment = UsageSegment::try_new(
        SegmentId::try_new(&format!("seg-{now_millis}-{published}"))
            .map_err(|error| format!("segment id: {error:?}"))?,
        definition,
        scope,
        1,
        SequenceInterval::try_new(published.max(1), published.max(1))
            .map_err(|error| format!("sequence interval: {error:?}"))?,
        terrabase::MeasurementWindow::interval(hour_start, now_millis)
            .map_err(|error| format!("measurement window: {error:?}"))?,
        measured_total(state),
        None,
    )
    .map_err(|error| format!("the segment is invalid: {error:?}"))?;
    let mut ledger = state.usage.lock().unwrap_or_else(|poison| poison.into_inner());
    let outcome = ledger
        .insert(segment)
        .map_err(|error| format!("the ledger refused the segment: {error:?}"))?;
    Ok(format!(
        "segment inserted ({outcome:?}) at generation 1: {documents} document(s) attributed to region {region}",
        outcome = outcome,
    ))
}

/// The measured quantity.
///
/// The meter's unit is the byte-hour and the window above is exactly one
/// hour, so the integer total is the byte count itself. The byte count is the
/// studio's own directory walk: the engine's public in-process surface
/// exposes no byte metric.
fn measured_total(state: &StudioState) -> i128 {
    i128::from(location_bytes(state))
}

fn location_bytes(state: &StudioState) -> u64 {
    let mut bytes = 0;
    let mut stack = vec![state.config.data_dir.clone()];
    while let Some(directory) = stack.pop() {
        let Ok(entries) = std::fs::read_dir(&directory) else { continue };
        for entry in entries.flatten() {
            if entry.file_type().map(|kind| kind.is_dir()).unwrap_or(false) {
                stack.push(entry.path());
            } else {
                bytes += entry.metadata().map(|meta| meta.len()).unwrap_or_default();
            }
        }
    }
    bytes
}

fn count_documents(state: &StudioState, tenant_name: &str) -> u64 {
    let tenant = match terrabase::TenantId::try_new(tenant_name) {
        Ok(tenant) => tenant,
        Err(_) => return 0,
    };
    state
        .collections_of(tenant_name)
        .iter()
        .filter_map(|collection| {
            terrabase::types::CollectionName::try_new(collection)
                .ok()
                .and_then(|collection| {
                    crate::engine_ops::list_documents(
                        state.engine.as_ref(),
                        &tenant,
                        &collection,
                        0,
                        1,
                    )
                    .ok()
                })
                .map(|page| page.matched)
        })
        .sum()
}
