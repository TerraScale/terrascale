//! The Documents page: paginated listing, point read, create/replace, delete.

use crate::engine_ops::{self, DocumentPage, OpError, PAGE_SIZE};
use crate::html::{self, ButtonStyle};
use crate::state::StudioState;

/// The identifier the document table and its pager are merged into.
pub const ROWS_ID: &str = "document-rows";
/// The identifier the point-read result is merged into.
pub const VIEW_ID: &str = "document-view";

/// The tenant/collection selection the page is currently showing.
#[derive(Clone, Debug)]
pub struct Selection {
    /// The tenant name.
    pub tenant: String,
    /// The collection name.
    pub collection: String,
    /// The zero-based page index.
    pub page: u64,
}

impl Selection {
    /// Picks a sensible default: the first registered tenant and collection,
    /// or the sample names when the registry is empty.
    pub fn default_for(state: &StudioState) -> Self {
        let tenant = state.tenants().first().cloned().unwrap_or_else(|| "demo".to_owned());
        let collection =
            state.collections_of(&tenant).first().cloned().unwrap_or_else(|| "users".to_owned());
        Self { tenant, collection, page: 0 }
    }
}

/// Renders the Documents page.
pub fn render(state: &StudioState, selection: &Selection, nonce: &str) -> String {
    let body = format!(
        "{heading}\n\
{limit_note}\n\
{list}\n\
<div class=\"grid gap-6 lg:grid-cols-2\">{read_card}{editor_card}</div>\n\
{delete_card}\n",
        heading = html::heading(
            "Documents",
            "Point reads, create/replace, and delete through one engine transaction each, at Synced durability.",
        ),
        limit_note = html::alert(
            html::Tone::Neutral,
            "What the studio can list",
            "The engine's public in-process surface has no tenant or collection enumeration: committed_tenants and the storage window reads are crate-private, and every query names one collection. The selector below therefore lists the tenants and collections this studio has observed (kept in studio/state.json beside the engine location) — type any other name to work with data the studio has not seen.",
        ),
        list = list_card(state, selection),
        read_card = read_card(state, selection),
        editor_card = editor_card(state, selection),
        delete_card = delete_card(state, selection),
    );
    html::page(nonce, "Documents", "/documents", "", &body)
}

fn list_card(state: &StudioState, selection: &Selection) -> String {
    let tenant_options = options_for(state, &selection.tenant);
    let collection_options = collection_options(state, &selection.tenant, &selection.collection);
    let body = format!(
        "<form class=\"grid gap-4\" data-on:submit=\"@post('/documents/rows', {{contentType: 'form', retry: 'never'}})\">\n\
<div class=\"grid gap-4 sm:grid-cols-2\">\n{tenant}{collection}</div>\n\
<input type=\"hidden\" name=\"page\" value=\"0\">\n\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary} w-fit\">List documents</button>\n\
</form>\n\
<div id=\"{ROWS_ID}\" class=\"mt-4\">{rows}</div>",
        tenant = html::select("tenant", "Tenant", &tenant_options),
        collection = html::select("collection", "Collection", &collection_options),
        primary = ButtonStyle::Primary.classes(),
        ROWS_ID = ROWS_ID,
        rows = rows_fragment(state, selection),
    );
    html::card("Collection", &body)
}

fn options_for(state: &StudioState, chosen: &str) -> String {
    let mut names = state.tenants();
    if !names.iter().any(|name| name == chosen) {
        names.push(chosen.to_owned());
        names.sort();
    }
    names.iter().map(|name| html::option(name, name, name == chosen)).collect::<Vec<_>>().concat()
}

fn collection_options(state: &StudioState, tenant: &str, chosen: &str) -> String {
    let mut names = state.collections_of(tenant);
    if !names.iter().any(|name| name == chosen) {
        names.push(chosen.to_owned());
        names.sort();
    }
    names.iter().map(|name| html::option(name, name, name == chosen)).collect::<Vec<_>>().concat()
}

/// Renders the rows fragment: table, pager, and any typed read failure.
///
/// Both the page's first paint and the `POST /documents/rows` action merge
/// this markup into `#{ROWS_ID}`.
pub fn rows_fragment(state: &StudioState, selection: &Selection) -> String {
    let page = load_page(state, selection);
    match page {
        Ok(page) => rows_table(selection, &page),
        Err(failure) => html::alert(html::Tone::Bad, "The list did not read", &failure.0),
    }
}

fn load_page(state: &StudioState, selection: &Selection) -> Result<DocumentPage, OpError> {
    let tenant = engine_ops::tenant(&selection.tenant)?;
    let collection = engine_ops::collection(&selection.collection)?;
    engine_ops::list_documents(
        state.engine.as_ref(),
        &tenant,
        &collection,
        selection.page,
        PAGE_SIZE,
    )
}

fn rows_table(selection: &Selection, page: &DocumentPage) -> String {
    let mut rows = String::new();
    for row in &page.rows {
        rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        rows.push_str(&html::cell(&row.id));
        rows.push_str(&html::cell(&row.version.to_string()));
        rows.push_str(&html::cell(&row.fields.join(", ")));
        rows.push_str(&html::cell(&row.json));
        rows.push_str("</tr>");
    }
    let table = html::table(
        &["id", "version", "fields", "value"],
        &[rows],
        "This collection holds no documents.",
    );
    let pager = render_pager(selection, page);
    format!(
        "<div class=\"mb-3 text-xs text-muted-foreground\">{matched} document(s) matched · page {number} of {pages} · {limit} per page</div>\n\
{table}\n{pager}",
        matched = page.matched,
        number = page.number() + 1,
        pages = page.pages(),
        limit = page.limit,
        table = table,
        pager = pager,
    )
}

fn pager_form(selection: &Selection, page: u64, label: &str) -> String {
    format!(
        "<form data-on:submit=\"@post('/documents/rows', {{contentType: 'form', retry: 'never'}})\">\
<input type=\"hidden\" name=\"tenant\" value=\"{tenant}\">\
<input type=\"hidden\" name=\"collection\" value=\"{collection}\">\
<input type=\"hidden\" name=\"page\" value=\"{page}\">\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium transition-colors {classes}\">{label}</button>\
</form>",
        tenant = html::attribute(&selection.tenant),
        collection = html::attribute(&selection.collection),
        page = page,
        classes = ButtonStyle::Secondary.classes(),
        label = html::esc(label),
    )
}

fn render_pager(selection: &Selection, page: &DocumentPage) -> String {
    let disabled = "pointer-events-none opacity-50";
    let previous = if page.number() == 0 {
        format!(
            "<span class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium {classes} {disabled}\">Previous</span>",
            classes = ButtonStyle::Secondary.classes(),
            disabled = disabled
        )
    } else {
        pager_form(selection, page.number() - 1, "Previous")
    };
    let last_page = page.number() + 1 >= page.pages();
    let next = if last_page {
        format!(
            "<span class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium {classes} {disabled}\">Next</span>",
            classes = ButtonStyle::Secondary.classes(),
            disabled = disabled
        )
    } else {
        pager_form(selection, page.number() + 1, "Next")
    };
    format!(
        "<div class=\"mt-4 flex items-center gap-2\">{previous}{next}</div>",
        previous = previous,
        next = next
    )
}

fn read_card(state: &StudioState, selection: &Selection) -> String {
    let tenant_options = options_for(state, &selection.tenant);
    let collection_options = collection_options(state, &selection.tenant, &selection.collection);
    let body = format!(
        "<form class=\"grid gap-4\" data-on:submit=\"@post('/documents/read', {{contentType: 'form', retry: 'never'}})\">\n{tenant}{collection}{id}\
<div class=\"flex gap-2\"><button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\">Read document</button>\n\
<button type=\"button\" class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium transition-colors {secondary}\" data-on:click=\"@post('/documents/clear-view', {{payload: {{}}, retry: 'never'}})\">Clear</button></div>\n\
</form>\n<div id=\"{VIEW_ID}\" class=\"mt-4\"></div>",
        tenant = html::select("tenant", "Tenant", &tenant_options),
        collection = html::select("collection", "Collection", &collection_options),
        id = format!(
            "{}{}",
            html::text_input("id", "Document id", "", "the document identifier"),
            html::text_input(
                "confirm_target",
                "Type tenant/collection/id",
                "",
                "demo/users/document-id",
            ) + &html::text_input("confirm_phrase", "Type action phrase", "", "delete document"),
        ),
        primary = ButtonStyle::Primary.classes(),
        secondary = ButtonStyle::Secondary.classes(),
        VIEW_ID = VIEW_ID,
    );
    html::card("Point read", &body)
}

fn editor_card(state: &StudioState, selection: &Selection) -> String {
    let tenant_options = options_for(state, &selection.tenant);
    let collection_options = collection_options(state, &selection.tenant, &selection.collection);
    let sample = serde_json::to_string_pretty(&serde_json::json!({
        "name": "Ada Lovelace",
        "level": 3,
        "active": true
    }))
    .unwrap_or_else(|_| "{\n}".to_owned());
    let body = format!(
        "<form class=\"grid gap-4\" data-on:submit=\"@post('/documents/put', {{contentType: 'form', retry: 'never'}})\">\n{tenant}{collection}{id}\
{json}\
<div class=\"grid gap-4 sm:grid-cols-2\">{expected}{version}</div>\n\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary} w-fit\">Create or replace</button>\n\
</form>\n{receipt}",
        tenant = html::select("tenant", "Tenant", &tenant_options),
        collection = html::select("collection", "Collection", &collection_options),
        id = html::text_input("id", "Document id", "", "a new or existing identifier"),
        json = html::json_editor("json", "Document JSON", &sample),
        expected = html::select(
            "expected",
            "Expected version",
            &format!(
                "{}{}{}",
                html::option("any", "any (create or replace)", true),
                html::option("new", "new (refuse if it exists)", false),
                html::option("exact", "exact version", false),
            )
        ),
        version = html::number_input("version", "Exact version", "1"),
        primary = ButtonStyle::Primary.classes(),
        receipt = html::receipt_region_with("editor-receipt"),
    );
    html::card("Create or replace", &body)
}

fn delete_card(state: &StudioState, selection: &Selection) -> String {
    let tenant_options = options_for(state, &selection.tenant);
    let collection_options = collection_options(state, &selection.tenant, &selection.collection);
    let sample = format!(
        "<div class=\"mt-4 border-t pt-4\"><form data-on:submit=\"if (!confirm('Load six fixed sample document IDs? Existing documents with those IDs may be replaced.')) return; @post('/documents/sample', {{contentType: 'form', retry: 'never'}})\"><button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium transition-colors {classes}\">Load sample documents</button></form>\n<div id=\"sample-receipt\" class=\"mt-3\"></div></div>",
        classes = ButtonStyle::Secondary.classes(),
    );
    let body = format!(
        "<form class=\"grid gap-4\" data-on:submit=\"if (!confirm('Delete this document? The engine stages a collection-scoped tombstone and commits it at Synced durability.')) return; @post('/documents/delete', {{contentType: 'form', retry: 'never'}})\">\n\
<div class=\"grid gap-4 sm:grid-cols-3\">{tenant}{collection}{id}</div>\n\
{confirmation}\n\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {destructive} w-fit\">Delete document</button>\n\
</form>\n{receipt}\n{sample}",
        tenant = html::select("tenant", "Tenant", &tenant_options),
        collection = html::select("collection", "Collection", &collection_options),
        id = html::text_input("id", "Document id", "", "the document identifier"),
        confirmation = html::confirmation_fields(
            &format!(
                "{}/{}/<document id entered above>",
                selection.tenant, selection.collection
            ),
            "delete document",
        ),
        destructive = ButtonStyle::Destructive.classes(),
        receipt = html::receipt_region_with("delete-receipt"),
    );
    html::card("Delete", &body)
}
