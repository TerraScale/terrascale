//! The Overview page: engine identity, live metrics, and recent actions.

use crate::html::{self, ButtonStyle};
use crate::metrics;
use crate::state::StudioState;

/// Renders the Overview page.
pub fn render(state: &StudioState, nonce: &str) -> String {
    let snapshot = metrics::gather(state);
    let body = format!(
        "{heading}\n\
<div class=\"hidden\" data-init=\"@get('/events/metrics', {{openWhenHidden: true, retry: 'always'}})\" aria-hidden=\"true\"></div>\n\
{panel}\n\
{indexes}\n\
<div class=\"grid gap-6 lg:grid-cols-2\">{maintenance}{journal}</div>\n",
        heading = html::heading(
            "Overview",
            "One durable engine location, driven in-process. This panel is pushed over a single server-sent-event connection every two seconds.",
        ),
        panel = metrics::render_panel(&snapshot),
        maintenance = maintenance_card(),
        indexes = index_card(state),
        journal = journal_card(state),
    );
    html::page(nonce, "Overview", "/", "", &body)
}

fn index_card(state: &StudioState) -> String {
    let mut tenant_names = state.tenants();
    if tenant_names.is_empty() {
        tenant_names.push("demo".to_owned());
    }
    let options =
        tenant_names.iter().map(|name| html::option(name, name, true)).collect::<Vec<_>>().concat();
    let actions = [
        ("start", "start (resume)"),
        ("stop", "stop (pause)"),
        ("enable", "enable"),
        ("disable", "disable"),
        ("reset-side-by-side", "reset side-by-side"),
        ("rebuild", "one rebuild step"),
        ("wait", "wait for non-stale"),
        ("clear-errors", "clear errors"),
    ]
    .iter()
    .map(|(value, label)| html::option(value, label, *value == "rebuild"))
    .collect::<Vec<_>>()
    .concat();
    let body = format!(
        "<p class=\"mb-4 text-sm text-muted-foreground\">The lifecycle seam serves explicit indexes only: an `Auto/` namespace is refused with `NotExplicitIndex`, which is the typed answer you will see if you name one. Automatic indexes are managed by the optimizer and the `Maintain auto-index lifecycle` action below.</p>\n\
<form class=\"grid gap-4\" data-on:submit=\"if (!confirm('Run this index lifecycle action? Reset and rebuild actions can replace index generations.')) return; @post('/indexes/action', {{contentType: 'form', retry: 'never'}})\">\n\
<div class=\"grid gap-4 sm:grid-cols-3\">{tenant}{name}{action}</div>\n\
{confirmation}\n\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary} w-fit\">Run index action</button>\n\
</form>\n{receipt}",
        tenant = html::select("tenant", "Tenant", &options),
        name = html::text_input("name", "Index name", "", "ByLevel"),
        action = html::select("action", "Action", &actions),
        confirmation = html::confirmation_fields(
            "<tenant selected above>/<index name entered above>",
            "index <selected action value>",
        ),
        primary = ButtonStyle::Primary.classes(),
        receipt = html::receipt_region_with("index-receipt"),
    );
    html::card("Index lifecycle", &body)
}

fn maintenance_card() -> String {
    let body = format!(
        "<p class=\"mb-4 text-sm text-muted-foreground\">Storage maintenance passes through the engine's public compaction and index-lifecycle seams. Each action answers with its typed receipt below.</p>\n\
<div class=\"grid gap-4\">\n\
{reclaim}\
{history}\
<form data-on:submit=\"@post('/maintenance/maintain-indexes', {{contentType: 'form', retry: 'never'}})\">{maintain_confirmation}<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium transition-colors {secondary}\">Maintain auto-index lifecycle</button></form>\n\
</div>\n\
{receipt}\n\
<p class=\"mt-4 text-xs text-muted-foreground\">Both compaction policies derive their bounds from the store's own durable facts; the public compaction request carries no caller-minted sequence.</p>",
        primary = ButtonStyle::Primary.classes(),
        secondary = ButtonStyle::Secondary.classes(),
        reclaim = compact_form("reclaim", "Compact (reclaim WAL and roots)", ButtonStyle::Primary),
        history =
            compact_form("reclaim-history", "Compact and reclaim history", ButtonStyle::Primary),
        maintain_confirmation = html::confirmation_fields("local engine", "maintain indexes"),
        receipt = html::receipt_region(),
    );
    html::card("Storage maintenance", &body)
}

fn compact_form(policy: &str, label: &str, style: ButtonStyle) -> String {
    let phrase =
        if policy == "reclaim-history" { "compact reclaim history" } else { "compact reclaim" };
    format!(
        "<form class=\"grid gap-2\" data-on:submit=\"@post('/maintenance/compact', {{contentType: 'form', retry: 'never'}})\"><input type=\"hidden\" name=\"policy\" value=\"{policy}\">{confirmation}<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {classes} w-fit\">{label}</button></form>",
        policy = html::attribute(policy),
        confirmation = html::confirmation_fields("local engine", phrase),
        classes = style.classes(),
        label = html::esc(label),
    )
}

fn journal_card(state: &StudioState) -> String {
    let records = state.journal();
    let mut rows = String::new();
    for record in records.iter().take(12) {
        let tone = if record.ok { html::Tone::Good } else { html::Tone::Bad };
        rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        rows.push_str(&html::cell(&record.at));
        rows.push_str(&html::cell(&record.action));
        rows.push_str(&html::cell_raw(&html::badge(tone, &record.outcome)));
        rows.push_str("</tr>");
    }
    let table = if rows.is_empty() {
        String::new()
    } else {
        html::table(&["when (UTC)", "action", "outcome"], &[rows], "No action yet.")
    };
    html::card("Recent actions", &table)
}
