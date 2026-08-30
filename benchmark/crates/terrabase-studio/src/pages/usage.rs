//! The Usage page: the engine's meter catalog and authoritative durable facts.

use terrabase::usage::{UsageFactId, UsagePageRequest, UsageRecordMeter, UsageRecordRequest};
use terrabase::{MeterCatalog, MeterKind, RegionCode};

use crate::html::{self, ButtonStyle, Tone};
use crate::state::StudioState;

/// The authoritative fact table replaced after a record command.
pub const LEDGER_ID: &str = "usage-ledger";
const PAGE_SIZE: u32 = 256;

/// Renders the Usage page for one tenant.
pub fn render(state: &StudioState, tenant_name: &str, nonce: &str) -> String {
    let body = format!(
        "{heading}\n{truth}\n{catalog}\n{facts}\n{record}",
        heading = html::heading(
            "Usage",
            "Record standalone event meters and inspect the engine's authoritative durable usage facts.",
        ),
        truth = html::alert(
            Tone::Neutral,
            "The engine is authoritative",
            "The table below comes from Engine::usage_page at a stable storage snapshot. Studio does not estimate usage, walk the data directory, or substitute an in-memory ledger when the backend refuses the read.",
        ),
        catalog = catalog_card(),
        facts = authoritative_fragment(state, tenant_name),
        record = record_card(state, tenant_name),
    );
    html::page(nonce, "Usage", "/usage", "", &body)
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
    html::card(
        "Meter catalog",
        &html::table(
            &["meter", "version", "unit", "charge scope", "minting rule", "cadence"],
            &[rows],
            "No meter defined.",
        ),
    )
}

/// Renders the first stable page of authoritative facts for one tenant.
pub fn authoritative_fragment(state: &StudioState, tenant_name: &str) -> String {
    let content = authoritative_table(state, tenant_name).unwrap_or_else(|failure| {
        html::alert(Tone::Bad, "Authoritative usage is unavailable", &failure)
    });
    format!(
        "<div id=\"{LEDGER_ID}\">{card}</div>",
        card = html::card("Authoritative durable facts", &content),
    )
}

fn authoritative_table(state: &StudioState, tenant_name: &str) -> Result<String, String> {
    let tenant = terrabase::TenantId::try_new(tenant_name.trim())
        .map_err(|error| format!("TenantId validation failed: {error:?}"))?;
    let scope = state.engine.scope_for(&tenant);
    let request = UsagePageRequest::try_new(None, PAGE_SIZE, None)
        .map_err(|error| format!("UsagePageRequest validation failed: {error:?}"))?;
    let page = state
        .engine
        .usage_page(&scope, request)
        .map_err(|error| format!("UsageCommandError::{error:?}: {error}"))?;

    let mut rows = String::new();
    for durable in page.facts() {
        let fact = durable.fact();
        let scope = fact.scope();
        let identity = fact.identity();
        let window = fact.window();
        rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        rows.push_str(&html::cell(&durable.sequence().to_string()));
        rows.push_str(&html::cell(&durable.replica_epoch().to_string()));
        rows.push_str(&html::cell(meter_name(fact.meter())));
        rows.push_str(&html::cell(&format!(
            "{}/{}/{}/{}/{}",
            scope.tenant_id().as_str(),
            scope.project_id().as_str(),
            scope.database_id().as_str(),
            scope.partition_id().as_str(),
            scope.region_id().as_str(),
        )));
        rows.push_str(&html::cell(&format!("{:?}:{}", identity.rule(), identity.id())));
        rows.push_str(&html::cell(&format!("{}..{}", window.start_millis(), window.end_millis(),)));
        rows.push_str(&html::cell(&fact.delta().to_string()));
        rows.push_str("</tr>");
    }

    let page_note = if page.continuation().is_some() {
        html::alert(
            Tone::Warn,
            "More facts exist",
            &format!(
                "This view stops at {PAGE_SIZE} facts from snapshot sequence {}. The engine returned a continuation, so this is not the complete tenant history.",
                page.snapshot_sequence().get(),
            ),
        )
    } else {
        html::alert(
            Tone::Neutral,
            "Stable snapshot",
            &format!(
                "The page is complete through storage sequence {} for tenant `{}`.",
                page.snapshot_sequence().get(),
                tenant.as_str(),
            ),
        )
    };

    Ok(format!(
        "{selector}{table}{note}",
        selector = tenant_selector(state, tenant.as_str()),
        table = html::table(
            &["sequence", "replica epoch", "meter", "scope", "identity", "window millis", "delta",],
            &[rows],
            "No durable usage fact has been recorded for this tenant.",
        ),
        note = page_note,
    ))
}

fn tenant_selector(state: &StudioState, selected: &str) -> String {
    let mut tenants = state.tenants();
    if !tenants.iter().any(|tenant| tenant == selected) {
        tenants.push(selected.to_owned());
        tenants.sort();
    }
    let options = tenants
        .iter()
        .map(|tenant| html::option(tenant, tenant, tenant == selected))
        .collect::<Vec<_>>()
        .concat();
    format!(
        "<form class=\"mb-4 flex items-end gap-2\" method=\"get\" action=\"/usage\">{tenant}<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium transition-colors {secondary}\">Read tenant facts</button></form>",
        tenant = html::select("tenant", "Tenant", &options),
        secondary = ButtonStyle::Secondary.classes(),
    )
}

fn record_card(state: &StudioState, selected_tenant: &str) -> String {
    let mut tenant_names = state.tenants();
    if !tenant_names.iter().any(|tenant| tenant == selected_tenant) {
        tenant_names.push(selected_tenant.to_owned());
        tenant_names.sort();
    }
    let tenants = tenant_names
        .iter()
        .map(|name| html::option(name, name, name == selected_tenant))
        .collect::<Vec<_>>()
        .concat();
    let meters = format!(
        "{}{}",
        html::option("admitted-request-units", "admitted request units", true),
        html::option("public-egress-bytes", "public egress bytes", false),
    );
    let regions = [RegionCode::GRU, RegionCode::SFO, RegionCode::FRA]
        .iter()
        .map(|region| html::option(region.as_str(), region.as_str(), *region == RegionCode::GRU))
        .collect::<Vec<_>>()
        .concat();
    let body = format!(
        "<p class=\"mb-4 text-sm text-muted-foreground\">Record one positive, idempotent event fact. Reusing a fact id with the same content returns the original receipt; reusing it for different content fails closed.</p>\n\
<form class=\"grid gap-4\" data-on:submit=\"@post('/usage/record', {{contentType: 'form', retry: 'never'}})\">\n\
<div class=\"grid gap-4 md:grid-cols-3\">{tenant}{meter}{region}</div>\n\
<div class=\"grid gap-4 md:grid-cols-3\">{project}{database}{partition}</div>\n\
<div class=\"grid gap-4 md:grid-cols-2\">{fact_id}{units}</div>\n\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary} w-fit\">Record durable usage fact</button>\n\
</form>\n{receipt}",
        tenant = html::select("tenant", "Tenant", &tenants),
        meter = html::select("meter", "Meter", &meters),
        region = html::select("region", "Origin region", &regions),
        project = html::text_input("project", "Project id", "studio", "project identifier"),
        database = html::text_input("database", "Database id", "studio", "database identifier"),
        partition = html::text_input("partition", "Partition id", "p0", "partition identifier"),
        fact_id = html::text_input("fact_id", "Stable fact id", "", "operator-event-0001"),
        units = html::number_input("integer_units", "Positive integer units", "1"),
        primary = ButtonStyle::Primary.classes(),
        receipt = html::receipt_region_with("usage-receipt"),
    );
    html::card("Record a durable event fact", &body)
}

/// Validates and durably records one standalone usage event.
#[allow(clippy::too_many_arguments)]
pub fn record_fact(
    state: &StudioState,
    tenant_name: &str,
    meter_name: &str,
    project: &str,
    database: &str,
    partition: &str,
    region: &str,
    fact_id: &str,
    integer_units: &str,
    now_millis: u64,
) -> Result<String, String> {
    let tenant = terrabase::TenantId::try_new(tenant_name.trim())
        .map_err(|error| format!("tenant: {error:?}"))?;
    let meter = match meter_name {
        "admitted-request-units" => UsageRecordMeter::AdmittedRequestUnits,
        "public-egress-bytes" => UsageRecordMeter::PublicEgressBytes,
        _ => return Err("meter: expected admitted-request-units or public-egress-bytes".to_owned()),
    };
    let region = RegionCode::from_text(region.trim().to_uppercase().as_str())
        .map_err(|error| format!("origin region: {error:?}"))?;
    let fact_id =
        UsageFactId::try_new(fact_id.trim()).map_err(|error| format!("fact id: {error:?}"))?;
    let integer_units = integer_units
        .trim()
        .parse::<i128>()
        .map_err(|_| "integer units must be a positive whole number".to_owned())?;
    let request = UsageRecordRequest::try_new(
        meter,
        project.trim(),
        database.trim(),
        partition.trim(),
        region,
        fact_id,
        now_millis,
        1,
        integer_units,
    )
    .map_err(|error| format!("UsageRecordRequest validation failed: {error:?}"))?;
    let scope = state.engine.scope_for(&tenant);
    let receipt = state
        .engine
        .record_usage(&scope, request)
        .map_err(|error| format!("UsageCommandError::{error:?}: {error}"))?;
    state.observe(tenant.as_str(), None);
    Ok(format!(
        "fact `{}` is durable at sequence {} in replica epoch {} (already recorded: {})",
        receipt.fact_id(),
        receipt.sequence().get(),
        receipt.replica_epoch(),
        receipt.already_recorded(),
    ))
}
