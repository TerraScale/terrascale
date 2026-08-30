//! The guided Query page: build a typed query, see rows and index selection.

use terrabase::query::optimizer::{IndexField, SelectionOutcome};
use terrabase::{Engine, QueryResult, TenantId};

use crate::engine_ops::{self, ConditionForm, QueryForm};
use crate::html::{self, ButtonStyle};
use crate::state::StudioState;

/// The identifier the query result is merged into.
pub const RESULT_ID: &str = "query-result";

/// The condition kinds the form offers, in display order.
pub const CONDITION_KINDS: &[(&str, &str)] = &[
    ("equals", "equals"),
    ("not-equals", "not equals"),
    ("greater", "greater than"),
    ("greater-or-equal", "greater or equal"),
    ("less", "less than"),
    ("less-or-equal", "less or equal"),
    ("between", "between"),
    ("in", "in (JSON array)"),
    ("starts-with", "starts with"),
    ("ends-with", "ends with"),
    ("contains", "contains"),
    ("exists", "field exists"),
    ("missing", "field missing"),
];

/// How many condition rows the form renders.
pub const CONDITION_ROWS: usize = 4;

/// Renders the Query page.
pub fn render(state: &StudioState, selection: &Option<(String, String)>) -> String {
    let (tenant, collection) = selection.clone().unwrap_or_else(|| default_pair(state));
    let body = format!(
        "{heading}\n\
{builder}\n\
{tql}\n\
<div id=\"{RESULT_ID}\">{initial}</div>",
        heading = html::heading(
            "Query",
            "Build the engine's typed query — equality, comparison, membership, and pattern conditions, one ordering key, projection, and paging — then read the rows beside the plan the optimizer produced.",
        ),
        builder = builder_card(state, &tenant, &collection),
        tql = tql_card(&tenant),
        initial = html::card(
            "Index selection",
            &html::alert(
                html::Tone::Neutral,
                "",
                "Run a query to see the optimizer's selected index, candidates, and cache outcome.",
            ),
        ),
        RESULT_ID = RESULT_ID,
    );
    html::page("Query", "/query", "", &body)
}

fn tql_card(tenant: &str) -> String {
    let body = format!(
        "<form class=\"grid gap-4\" data-on:submit=\"@post('/query/tql', {{contentType: 'form', retry: 'never'}})\">\n\
{tenant}{text}\
<button type=\"submit\" class=\"inline-flex w-fit items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\">Run TQL</button>\n\
</form>\n\
<p class=\"mt-4 text-xs text-muted-foreground\">TQL text is limited by the engine's public query-text bound and runs with a 30-second deadline against the selected tenant snapshot.</p>",
        tenant = html::text_input("tenant", "Tenant", tenant, "tenant identifier"),
        text = html::textarea(
            "text",
            "TQL",
            "from users where active = true order by name limit 25",
            6,
        ),
        primary = ButtonStyle::Primary.classes(),
    );
    html::card("TQL", &body)
}

fn default_pair(state: &StudioState) -> (String, String) {
    let tenant = state.tenants().first().cloned().unwrap_or_else(|| "demo".to_owned());
    let collection =
        state.collections_of(&tenant).first().cloned().unwrap_or_else(|| "users".to_owned());
    (tenant, collection)
}

fn builder_card(state: &StudioState, tenant: &str, collection: &str) -> String {
    let mut tenant_names = state.tenants();
    if !tenant_names.iter().any(|name| name == tenant) {
        tenant_names.push(tenant.to_owned());
        tenant_names.sort();
    }
    let mut collection_names = state.collections_of(tenant);
    if !collection_names.iter().any(|name| name == collection) {
        collection_names.push(collection.to_owned());
        collection_names.sort();
    }
    let tenant_options = tenant_names
        .iter()
        .map(|name| html::option(name, name, name == tenant))
        .collect::<Vec<_>>()
        .concat();
    let collection_options = collection_names
        .iter()
        .map(|name| html::option(name, name, name == collection))
        .collect::<Vec<_>>()
        .concat();

    let mut condition_rows = String::new();
    for index in 0..CONDITION_ROWS {
        let kinds = CONDITION_KINDS
            .iter()
            .map(|(value, label)| html::option(value, label, index == 0 && *value == "equals"))
            .collect::<Vec<_>>()
            .concat();
        let kind = format!(
            "<select class=\"flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm\" name=\"kind\">{kinds}</select>",
            kinds = kinds
        );
        condition_rows.push_str(&format!(
            "<div class=\"grid gap-2 sm:grid-cols-[1fr_auto_1fr_1fr] items-end\">\n{field}\n\
<div class=\"grid gap-2 text-sm\"><span class=\"font-medium invisible\">kind</span>{kind}</div>\n\
{value}\n{high}\n</div>\n",
            field = html::text_input("field", "Field", "", "level"),
            kind = kind,
            value = html::text_input("value", "Value (JSON)", "", "5"),
            high = html::text_input("high", "High (between)", "", "10"),
        ));
    }

    let body = format!(
        "<form class=\"grid gap-4\" data-on:submit=\"@post('/query/run', {{contentType: 'form', retry: 'never'}})\">\n\
<div class=\"grid gap-4 sm:grid-cols-2\">{tenant}{collection}</div>\n\
<details class=\"rounded-lg border px-4 py-3\" open><summary class=\"cursor-pointer text-sm font-medium\">Conditions</summary>\n\
<p class=\"mt-2 text-xs text-muted-foreground\">Values are JSON, so <code>\"abc\"</code> is a string, <code>42</code> an integer, <code>1.5</code> a number, <code>true</code> a boolean. A row with an empty field is skipped. A condition the engine's validators refuse reports the typed validation failure rather than running.</p>\n\
<div class=\"mt-3 grid gap-3\">{condition_rows}</div></details>\n\
<div class=\"grid gap-4 sm:grid-cols-4\">{order_field}{direction}{offset}{limit}</div>\n\
{projection}\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary} w-fit\">Run query</button>\n\
</form>",
        tenant = html::select("tenant", "Tenant", &tenant_options),
        collection = html::select("collection", "Collection", &collection_options),
        condition_rows = condition_rows,
        order_field = html::text_input("order", "Order by (blank = key order)", "", "level"),
        direction = html::select(
            "direction",
            "Direction",
            &format!(
                "{}{}",
                html::option("asc", "ascending", true),
                html::option("desc", "descending", false),
            )
        ),
        offset = html::number_input("offset", "Offset", "0"),
        limit = html::number_input("limit", "Limit (0 = none)", "25"),
        projection = html::text_input(
            "projection",
            "Projection (comma-separated fields, blank = whole document)",
            "",
            "name, level"
        ),
        primary = ButtonStyle::Primary.classes(),
    );
    html::card("Query builder", &body)
}

/// The query form's raw submitted fields, before they become a typed query.
#[derive(Debug, Default)]
pub struct RawQueryForm {
    /// One field name per condition row.
    pub fields: Vec<String>,
    /// One condition kind per row, in the same order.
    pub kinds: Vec<String>,
    /// One JSON value per row, when the kind takes one.
    pub values: Vec<String>,
    /// One high bound per row, for `between`.
    pub highs: Vec<String>,
    /// The ordering field, blank for canonical key order.
    pub order: String,
    /// `asc` or `desc`.
    pub direction: String,
    /// The page offset.
    pub offset: String,
    /// The page limit, blank or `0` for none.
    pub limit: String,
    /// Comma-separated projection fields, blank for the whole document.
    pub projection: String,
}

/// Builds the typed form from the submitted fields.
///
/// This is the one place the HTTP form's flat field lists become the typed
/// query the engine compiles, so it has its own unit tests. A row whose field
/// is blank is skipped, which is how the unused rows of the builder are
/// ignored.
pub fn form_from_parts(raw: RawQueryForm) -> QueryForm {
    let mut conditions = Vec::new();
    for index in 0..CONDITION_ROWS {
        let field = raw.fields.get(index).map(|value| value.trim()).unwrap_or_default().to_owned();
        if field.is_empty() {
            continue;
        }
        let kind = raw.kinds.get(index).cloned().unwrap_or_else(|| "equals".to_owned());
        // `exists` and `missing` name a field only; a blank value stays blank
        // and the engine ignores it for those kinds.
        let value = raw.values.get(index).cloned().unwrap_or_default();
        let high = raw.highs.get(index).cloned().unwrap_or_default();
        conditions.push(ConditionForm { field, kind, value, high });
    }
    QueryForm {
        collection: String::new(),
        conditions,
        order_field: if raw.order.trim().is_empty() {
            None
        } else {
            Some(raw.order.trim().to_owned())
        },
        order_direction: if raw.direction == "desc" { "desc".to_owned() } else { "asc".to_owned() },
        offset: raw.offset.trim().parse().unwrap_or_default(),
        limit: if raw.limit.trim().is_empty() {
            None
        } else {
            raw.limit.trim().parse::<u32>().ok().filter(|value| *value > 0)
        },
        projection: raw
            .projection
            .split(',')
            .map(|field| field.trim().to_owned())
            .filter(|field| !field.is_empty())
            .collect(),
    }
}

/// Renders the result fragment: rows, statistics, and the index selection.
pub fn result_fragment(state: &StudioState, tenant: &str, form: &QueryForm) -> String {
    let parsed_tenant = match engine_ops::tenant(tenant) {
        Ok(parsed) => parsed,
        Err(failure) => return html::alert(html::Tone::Bad, "The query did not run", &failure.0),
    };
    let mut form = form.clone();
    if form.collection.is_empty() {
        form.collection =
            state.collections_of(tenant).first().cloned().unwrap_or_else(|| "users".to_owned());
    }
    let engine = state.engine.as_ref();
    match engine_ops::run_typed(engine, &parsed_tenant, form) {
        Ok(result) => render_result(engine, &parsed_tenant, &result),
        Err(failure) => html::alert(html::Tone::Bad, "The query did not run", &failure.0),
    }
}

/// Runs TQL text and renders its rows, statistics, and optimizer selection.
pub fn tql_result_fragment(state: &StudioState, tenant: &str, text: &str) -> String {
    let parsed_tenant = match engine_ops::tenant(tenant) {
        Ok(parsed) => parsed,
        Err(failure) => return html::alert(html::Tone::Bad, "The TQL did not run", &failure.0),
    };
    let engine = state.engine.as_ref();
    match engine_ops::run_tql(engine, &parsed_tenant, text) {
        Ok(result) => render_result(engine, &parsed_tenant, &result),
        Err(failure) => html::alert(html::Tone::Bad, "The TQL did not run", &failure.0),
    }
}

fn render_result(engine: &Engine, tenant: &TenantId, result: &QueryResult) -> String {
    let mut rows = String::new();
    for row in result.rows() {
        let document = row.document();
        let projected = row.projected();
        let body = if projected.is_empty() {
            crate::json::to_compact(document.value())
        } else {
            projected
                .iter()
                .map(|(field, value)| format!("{}: {}", field, crate::json::to_compact(value)))
                .collect::<Vec<_>>()
                .join(", ")
        };
        rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        rows.push_str(&html::cell(document.id().as_str()));
        rows.push_str(&html::cell(&document.metadata().version().to_string()));
        rows.push_str(&html::cell(&body));
        rows.push_str("</tr>");
    }
    let statistics = result.statistics();
    let selection = render_selection(engine, tenant);
    format!(
        "<div id=\"{RESULT_ID}\" class=\"grid gap-6\">\n\
{summary}\n\
{rows_card}\n\
{plan_card}\n\
</div>",
        RESULT_ID = RESULT_ID,
        summary = html::definition(&[
            ("matched (before paging)", result.matched().to_string()),
            ("returned", result.rows().len().to_string()),
            ("scanned", statistics.scanned().to_string()),
            ("skipped", statistics.skipped().to_string()),
            ("duration", format!("{:?}", statistics.duration().elapsed()),),
            ("staleness", format!("stale: {}", result.staleness().is_stale())),
            (
                "index used",
                result
                    .index_generation()
                    .map(|generation| format!(
                        "{} generation {} (epoch {})",
                        generation.namespace().name(),
                        generation.generation().generation(),
                        generation.generation().epoch(),
                    ))
                    .unwrap_or_else(|| "none — collection scan".to_owned())
            ),
        ]),
        rows_card = html::card(
            "Rows",
            &html::table(&["id", "version", "value"], &[rows], "No row matched."),
        ),
        plan_card = selection,
    )
}

/// Renders the newest index selection record the engine retained.
///
/// The record is the optimizer's own account of what it considered, so the
/// operator sees the plan's index selection rather than a studio guess.
fn render_selection(engine: &Engine, tenant: &TenantId) -> String {
    let records = engine.queries().recent_index_selections(tenant);
    let Some(record) = records.last() else {
        return html::card(
            "Index selection",
            &html::alert(
                html::Tone::Neutral,
                "",
                "No index selection recorded for this tenant yet. The optimizer records one for every dynamic query.",
            ),
        );
    };
    let fields = record.query_fields().iter().map(IndexField::name).collect::<Vec<_>>().join(", ");
    let candidates = if record.candidates().is_empty() {
        "none considered".to_owned()
    } else {
        record
            .candidates()
            .iter()
            .map(|candidate| format!("{} ({:?})", candidate.name(), candidate.verdict()))
            .collect::<Vec<_>>()
            .join("; ")
    };
    let outcome = match record.outcome() {
        SelectionOutcome::CollectionScan => "collection scan".to_owned(),
        SelectionOutcome::ChosenIndex { name, kind } => format!("chose {name} ({kind:?})"),
        SelectionOutcome::CreatedAutoIndex { name, build } => {
            format!("created automatic index {name} ({build:?})")
        }
        SelectionOutcome::CreationRefused { reason } => {
            format!("creation refused: {reason:?}")
        }
    };
    let body = html::definition(&[
        ("outcome", outcome),
        ("collection", record.collection().to_owned()),
        ("demanded fields", if fields.is_empty() { "—".to_owned() } else { fields }),
        ("candidates", candidates),
        ("cache hit", record.cache_hit().to_string()),
        ("retained records", records.len().to_string()),
    ]);
    html::card("Index selection", &body)
}
