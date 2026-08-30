//! Explicit-index authoring, inspection, and bounded lifecycle controls.

use std::collections::BTreeSet;
use std::time::Duration;

use terrabase::query::IndexNamespace;
use terrabase::query::optimizer::{IndexCapabilities, IndexDefinition, IndexField, IndexKind};
use terrabase::{IndexDeployment, IndexLockMode, IndexPriority, IndexResetMode, WaitForNonStale};

use crate::engine_ops;
use crate::html::{self, ButtonStyle, Tone};
use crate::metrics;
use crate::state::StudioState;

/// The fragment replaced after each index operation.
pub const SURFACE_ID: &str = "index-surface";
/// The catalog fragment refreshed after a lifecycle operation.
pub const CATALOG_ID: &str = "index-catalog";
/// The operation receipt target.
pub const RECEIPT_ID: &str = "index-receipt";

/// Renders the full index administration page.
pub fn render(state: &StudioState, nonce: &str) -> String {
    let body = format!(
        "{heading}\n{scope}\n<div id=\"{SURFACE_ID}\">{surface}</div>",
        heading = html::heading(
            "Indexes",
            "Create explicit indexes, inspect their bounded statistics, and drive every public lifecycle operation.",
        ),
        scope = html::alert(
            Tone::Neutral,
            "Ownership",
            "Explicit indexes accept operator actions here. Automatic indexes remain optimizer-owned and are read-only in Studio.",
        ),
        surface = surface_fragment(state),
    );
    html::page(nonce, "Indexes", "/indexes", "", &body)
}

/// Renders the replaceable page body after an action.
pub fn surface_fragment(state: &StudioState) -> String {
    format!(
        "<div class=\"grid gap-6\">{create}<div id=\"{CATALOG_ID}\">{catalog}</div>{receipt}</div>",
        create = create_card(state),
        catalog = catalog_card(state),
        receipt = html::receipt_region_with(RECEIPT_ID),
    )
}

/// Renders the index catalog inside its stable refresh target.
pub fn catalog_fragment(state: &StudioState) -> String {
    format!("<div id=\"{CATALOG_ID}\">{}</div>", catalog_card(state))
}

fn create_card(state: &StudioState) -> String {
    let tenant = state.tenants().first().cloned().unwrap_or_else(|| "demo".to_owned());
    let collection =
        state.collections_of(&tenant).first().cloned().unwrap_or_else(|| "users".to_owned());
    let body = format!(
        "<form class=\"grid gap-4\" data-on:submit=\"if (!confirm('Create or update this index? Updating an existing definition can replace its generation.')) return; @post('/indexes/create', {{contentType: 'form', retry: 'never'}})\">\n\
<div class=\"grid gap-4 md:grid-cols-2\">{tenant}{name}{collection}{fields}</div>\n\
<div class=\"grid gap-4 md:grid-cols-3\">{capabilities}{priority}{lock}</div>\n\
{confirmation}\n\
<button type=\"submit\" class=\"inline-flex w-fit items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\">Create or update index</button>\n\
</form>\n\
<p class=\"mt-4 text-xs text-muted-foreground\">Fields are a comma-separated list. The engine validates names, duplicate fields, capability shape, namespace bounds, and deployment locks.</p>",
        tenant = html::text_input("tenant", "Tenant", &tenant, "tenant identifier"),
        name = html::text_input("name", "Index name", "Users/ByName", "explicit namespace"),
        collection =
            html::text_input("collection", "Collection", &collection, "collection identifier",),
        fields = html::text_input("fields", "Fields", "name", "name, created_at"),
        confirmation = html::confirmation_fields(
            "<tenant entered above>/<index name entered above>",
            "create or update index",
        ),
        capabilities = html::select(
            "capabilities",
            "Field capabilities",
            &format!(
                "{}{}{}",
                html::option("full", "equality + ordering", true),
                html::option("equality", "equality", false),
                html::option("ordering", "ordering", false),
            ),
        ),
        priority = html::select(
            "priority",
            "Priority",
            &format!(
                "{}{}{}",
                html::option("normal", "normal", true),
                html::option("low", "low", false),
                html::option("high", "high", false),
            ),
        ),
        lock = html::select(
            "lock",
            "Definition lock",
            &format!(
                "{}{}{}",
                html::option("unlock", "unlocked", true),
                html::option("locked-ignore", "locked, ignore changes", false),
                html::option("locked-error", "locked, reject changes", false),
            ),
        ),
        primary = ButtonStyle::Primary.classes(),
    );
    html::card("Create or update an explicit index", &body)
}

fn catalog_card(state: &StudioState) -> String {
    let tenants = known_tenants(state);
    let mut rows = String::new();
    for tenant_name in tenants {
        let Ok(tenant) = terrabase::TenantId::try_new(&tenant_name) else { continue };
        for statistics in state.engine.indexes().statistics(&tenant) {
            let generation = statistics
                .published()
                .map(|value| value.generation().to_string())
                .unwrap_or_else(|| "-".to_owned());
            let facts = format!(
                "state={:?}; priority={:?}; lock={:?}; stale={}; entries={}; generation={}; covered={}; attempts={}; successes={}; failures={}; errors={}",
                statistics.state(),
                statistics.priority(),
                statistics.lock_mode(),
                statistics.staleness().stale(),
                statistics.entries(),
                generation,
                statistics
                    .covered_sequence()
                    .map(|value| value.to_string())
                    .unwrap_or_else(|| "-".to_owned()),
                statistics.build_attempts(),
                statistics.build_successes(),
                statistics.build_failures(),
                statistics.recorded_errors(),
            );
            rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
            rows.push_str(&html::cell(&tenant_name));
            rows.push_str(&html::cell(statistics.name()));
            rows.push_str(&html::cell_raw(&html::badge(Tone::Neutral, "explicit")));
            rows.push_str(&html::cell(&facts));
            rows.push_str(&html::cell_raw(&action_form(&tenant_name, statistics.name())));
            rows.push_str("</tr>");
        }

        for registered in state.engine.queries().registered_indexes(&tenant) {
            if registered.kind() != IndexKind::Auto {
                continue;
            }
            let fields = registered
                .fields()
                .iter()
                .map(|field| format!("{} ({})", field.name(), field.capabilities().as_tag()))
                .collect::<Vec<_>>()
                .join(", ");
            let facts = format!(
                "collection={}; fields={}; lifecycle={:?}; published={}",
                registered.collection(),
                fields,
                registered.state(),
                registered
                    .published()
                    .map(|value| value.generation().to_string())
                    .unwrap_or_else(|| "-".to_owned()),
            );
            rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
            rows.push_str(&html::cell(&tenant_name));
            rows.push_str(&html::cell(registered.name()));
            rows.push_str(&html::cell_raw(&html::badge(Tone::Neutral, "automatic")));
            rows.push_str(&html::cell(&facts));
            rows.push_str(&html::cell("optimizer-owned"));
            rows.push_str("</tr>");
        }
    }

    html::card(
        "Index catalog and lifecycle",
        &html::table(
            &["tenant", "index", "kind", "facts", "bounded action"],
            &[rows],
            "No index has been registered for a tenant this Studio has observed.",
        ),
    )
}

fn known_tenants(state: &StudioState) -> BTreeSet<String> {
    let mut tenants: BTreeSet<String> = state.tenants().into_iter().collect();
    for statistics in state.engine.indexes().engine_statistics() {
        tenants.insert(statistics.tenant().as_str().to_owned());
    }
    tenants
}

fn action_form(tenant: &str, name: &str) -> String {
    let confirmation_target = format!("{tenant}/{name}");
    format!(
        "<form class=\"flex min-w-64 items-end gap-2\" data-on:submit=\"if (!confirm('Run this index action? Reset and delete operations discard the current index generation.')) return; @post('/indexes/action', {{contentType: 'form', retry: 'never'}})\">\
<input type=\"hidden\" name=\"tenant\" value=\"{tenant}\">\
<input type=\"hidden\" name=\"name\" value=\"{name}\">\
{action}\
{confirm_target}{confirm_phrase}\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium transition-colors {secondary}\">Run</button>\
</form>",
        tenant = html::attribute(tenant),
        name = html::attribute(name),
        action = html::select("action", "Action", &action_options()),
        confirm_target =
            html::text_input("confirm_target", "Type target exactly", "", &confirmation_target,),
        confirm_phrase =
            html::text_input("confirm_phrase", "Type action phrase", "", "run index action",),
        secondary = ButtonStyle::Secondary.classes(),
    )
}

fn action_options() -> String {
    [
        ("inspect-staleness", "inspect staleness"),
        ("inspect-errors", "inspect errors"),
        ("rebuild", "rebuild one step"),
        ("wait", "wait up to 2 seconds"),
        ("stop", "pause"),
        ("start", "resume"),
        ("disable", "disable"),
        ("enable", "enable"),
        ("reset-in-place", "reset in place"),
        ("reset-side-by-side", "reset side by side"),
        ("clear-errors", "clear errors"),
        ("lock-unlock", "unlock definition"),
        ("lock-ignore", "lock and ignore changes"),
        ("lock-error", "lock and reject changes"),
        ("delete", "delete index"),
    ]
    .iter()
    .enumerate()
    .map(|(index, (value, label))| html::option(value, label, index == 0))
    .collect::<Vec<_>>()
    .concat()
}

/// Creates or updates one explicit index through the public engine seam.
#[allow(clippy::too_many_arguments, reason = "fields mirror the bounded HTML form")]
pub fn create_index(
    state: &StudioState,
    tenant_name: &str,
    name: &str,
    collection_name: &str,
    field_names: &str,
    capability_name: &str,
    priority_name: &str,
    lock_name: &str,
) -> Result<String, String> {
    let tenant = engine_ops::tenant(tenant_name).map_err(String::from)?;
    let collection = engine_ops::collection(collection_name).map_err(String::from)?;
    let namespace = IndexNamespace::try_new(tenant.clone(), name.trim())
        .map_err(|error| format!("index namespace refused: {error:?}"))?;
    let capabilities = match capability_name {
        "equality" => IndexCapabilities::EQUALITY,
        "ordering" => IndexCapabilities::ORDERING,
        "full" => IndexCapabilities::FULL,
        other => return Err(format!("unknown field capability `{other}`")),
    };
    let mut fields = Vec::new();
    for name in field_names.split(',').map(str::trim).filter(|value| !value.is_empty()) {
        fields.push(
            IndexField::try_new(name, capabilities)
                .map_err(|error| format!("index field `{name}` refused: {error:?}"))?,
        );
    }
    let definition = IndexDefinition::try_new(collection, fields, IndexKind::Explicit)
        .map_err(|error| format!("index definition refused: {error:?}"))?;
    let priority = match priority_name {
        "low" => IndexPriority::Low,
        "normal" => IndexPriority::Normal,
        "high" => IndexPriority::High,
        other => return Err(format!("unknown index priority `{other}`")),
    };
    let lock = parse_lock(lock_name)?;
    let outcome = state
        .engine
        .indexes()
        .create(&namespace, &definition, IndexDeployment::new(priority, lock))
        .map_err(|error| format!("index creation refused: {error:?}"))?;
    state.observe(tenant.as_str(), Some(collection_name.trim()));
    Ok(format!("index `{}` answered {outcome:?} at epoch {}", namespace.name(), outcome.epoch(),))
}

/// Runs one bounded public lifecycle or inspection action.
pub fn run_action(
    state: &StudioState,
    tenant_name: &str,
    name: &str,
    action: &str,
) -> Result<String, String> {
    if matches!(
        action,
        "start" | "stop" | "disable" | "enable" | "rebuild" | "reset-side-by-side" | "clear-errors"
    ) {
        return metrics::index_action(state, tenant_name, name, action).map_err(String::from);
    }

    let tenant = engine_ops::tenant(tenant_name).map_err(String::from)?;
    let namespace = IndexNamespace::try_new(tenant.clone(), name.trim())
        .map_err(|error| format!("index namespace refused: {error:?}"))?;
    let indexes = state.engine.indexes();
    match action {
        "delete" => indexes
            .delete(&namespace)
            .map(|()| format!("index `{}` deleted", namespace.name()))
            .map_err(|error| format!("index deletion refused: {error:?}")),
        "reset-in-place" => indexes
            .reset(&namespace, IndexResetMode::InPlace)
            .map(|value| format!("reset transition {value:?}"))
            .map_err(|error| format!("index reset refused: {error:?}")),
        "lock-unlock" | "lock-ignore" | "lock-error" => {
            let mode = match action {
                "lock-unlock" => IndexLockMode::Unlock,
                "lock-ignore" => IndexLockMode::LockedIgnore,
                _ => IndexLockMode::LockedError,
            };
            indexes
                .set_lock(&namespace, mode)
                .map(|()| format!("definition lock set to {mode:?}"))
                .map_err(|error| format!("definition lock refused: {error:?}"))
        }
        "inspect-staleness" => indexes
            .staleness(&namespace)
            .map(|value| format!("stale = {}; reasons = {:?}", value.stale(), value.reasons()))
            .map_err(|error| format!("staleness inspection refused: {error:?}")),
        "inspect-errors" => {
            let listing = indexes
                .errors(&tenant)
                .into_iter()
                .find(|listing| listing.name() == namespace.name());
            Ok(match listing {
                Some(listing) => format!(
                    "{} retained error(s): {:?}",
                    listing.records().len(),
                    listing.records()
                ),
                None => "no retained index errors".to_owned(),
            })
        }
        // Keep an explicit finite bound here even if this branch becomes
        // separate from the common helper above.
        "wait" => indexes
            .wait_for_non_stale(&namespace, WaitForNonStale::bounded(Duration::from_secs(2)))
            .map(|value| format!("wait outcome {value:?}"))
            .map_err(|error| format!("bounded wait refused: {error:?}")),
        other => Err(format!("unknown index action `{other}`")),
    }
}

fn parse_lock(name: &str) -> Result<IndexLockMode, String> {
    match name {
        "unlock" => Ok(IndexLockMode::Unlock),
        "locked-ignore" => Ok(IndexLockMode::LockedIgnore),
        "locked-error" => Ok(IndexLockMode::LockedError),
        other => Err(format!("unknown definition lock `{other}`")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::state::{Config, DEFAULT_ADDR};

    fn studio(name: &str) -> StudioState {
        let directory = std::env::temp_dir().join(format!(
            "terrabase-studio-indexes-{name}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos(),
        ));
        StudioState::open(Config { data_dir: directory, addr: DEFAULT_ADDR.to_owned() }).unwrap()
    }

    #[test]
    fn explicit_index_lifecycle_is_driven_through_the_public_store() {
        let state = studio("lifecycle");
        let created = create_index(
            &state,
            "demo",
            "Users/ByName",
            "users",
            "name",
            "full",
            "normal",
            "unlock",
        )
        .unwrap();
        assert!(created.contains("Created"), "{created}");
        assert!(run_action(&state, "demo", "Users/ByName", "inspect-staleness").is_ok());
        assert!(run_action(&state, "demo", "Users/ByName", "stop").is_ok());
        assert!(run_action(&state, "demo", "Users/ByName", "start").is_ok());
        assert!(run_action(&state, "demo", "Users/ByName", "lock-error").is_ok());
        assert!(run_action(&state, "demo", "Users/ByName", "delete").is_ok());
        assert!(state.engine.indexes().statistics(&engine_ops::tenant("demo").unwrap()).is_empty());
    }

    #[test]
    fn page_exposes_create_inspection_and_destructive_controls() {
        let state = studio("page");
        create_index(&state, "demo", "Users/ByName", "users", "name", "full", "normal", "unlock")
            .unwrap();
        let page = render(&state, "00112233445566778899aabbccddeeff");
        assert!(page.contains("@post('/indexes/create'"));
        assert!(page.contains("inspect staleness"));
        assert!(page.contains("reset side by side"));
        assert!(page.contains("delete index"));
        assert!(page.contains("optimizer-owned"));
    }
}
