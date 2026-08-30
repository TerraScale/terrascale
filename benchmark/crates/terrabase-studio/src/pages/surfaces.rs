//! Engine-native inspection surfaces that do not belong to the CRUD pages.
//!
//! These controls intentionally use the public `Engine` family and streaming
//! stores.  The page is also the honest inventory of capabilities that need a
//! remote member or a host-owned protocol boundary.

use crate::html::{self, ButtonStyle, Tone};
use crate::state::StudioState;

/// The stable target for family inspection results.
pub const FAMILY_ID: &str = "family-result";
/// The stable target for stream inspection results.
pub const STREAM_ID: &str = "stream-result";

/// Renders the engine-native inspection page.
pub fn render(state: &StudioState, nonce: &str) -> String {
    let body = format!(
        "{heading}\n<div class=\"grid gap-6 lg:grid-cols-2\">{health}{family}</div>\n{stream}\n{limits}",
        heading = html::heading(
            "Engine surfaces",
            "Bounded inspection of public family and streaming APIs on this one in-process engine location.",
        ),
        health = health_card(state),
        family = family_card(state),
        stream = stream_card(state),
        limits = limits_card(),
    );
    html::page(nonce, "Engine surfaces", "/surfaces", "", &body)
}

fn health_card(state: &StudioState) -> String {
    let sequence = state
        .engine
        .published_sequence()
        .map(|value| format!("seq {}", value.get()))
        .unwrap_or_else(|_| "unavailable".to_owned());
    let body = format!(
        "<div class=\"grid gap-3\">{status}{links}</div>",
        status = html::definition(&[
            ("engine snapshot", sequence),
            ("liveness", "GET /healthz → process is alive".to_owned()),
            ("readiness", "GET /readyz → engine snapshot is readable".to_owned()),
            ("Prometheus", "GET /metrics/prometheus → bounded local exposition".to_owned()),
        ]),
        links = html::alert(
            Tone::Neutral,
            "Production boundary",
            "These probes describe this loopback Studio process. They do not represent a remote cluster member, quorum, replication lag, or orchestrator health.",
        ),
    );
    html::card("Health & readiness", &body)
}

fn family_card(state: &StudioState) -> String {
    let tenant = state.tenants().first().cloned().unwrap_or_else(|| "demo".to_owned());
    let collection =
        state.collections_of(&tenant).first().cloned().unwrap_or_else(|| "users".to_owned());
    let body = format!(
        "<p class=\"mb-4 text-sm text-muted-foreground\">Read the schema-validation family for a collection and optionally validate one stored document. The result is rendered as bounded JSON and typed violation rows.</p>\n<form class=\"grid gap-4\" data-on:submit=\"@post('/families/validation', {{contentType: 'form', retry: 'never'}})\">\n<div class=\"grid gap-4 sm:grid-cols-3\">{tenant}{collection}{document}</div>\n<button type=\"submit\" class=\"inline-flex w-fit items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\">Inspect validation family</button>\n</form>\n<div class=\"mt-5 border-t pt-5\"><p class=\"mb-3 text-sm text-muted-foreground\">Declare a versioned JSON Schema subset. This writes one typed family record and is guarded by an exact confirmation.</p>\n<form class=\"grid gap-4\" data-on:submit=\"if (!confirm('Declare this schema version? Existing writes may be rejected by the new policy.')) return; @post('/families/schema/declare', {{contentType: 'form', retry: 'never'}})\">\n<div class=\"grid gap-4 sm:grid-cols-3\">{declare_tenant}{declare_collection}{version}</div>{schema}{confirmation}\n<button type=\"submit\" class=\"inline-flex w-fit items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\">Declare schema</button>\n</form></div>\n<div id=\"{FAMILY_ID}\" class=\"mt-4\">{empty}</div>",
        tenant = html::text_input("tenant", "Tenant", &tenant, "demo"),
        collection = html::text_input("collection", "Collection", &collection, "users"),
        document = html::text_input("document", "Document (optional)", "", "sample-000"),
        declare_tenant = html::text_input("tenant", "Tenant", &tenant, "demo"),
        declare_collection = html::text_input("collection", "Collection", &collection, "users"),
        version = html::number_input("version", "Schema version", "1"),
        schema = html::json_editor(
            "schema",
            "JSON Schema subset",
            r#"{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"x-terrabase-schema-version":1}"#,
        ),
        confirmation =
            html::confirmation_fields(&format!("{tenant}/{collection}"), "declare schema",),
        primary = ButtonStyle::Primary.classes(),
        FAMILY_ID = FAMILY_ID,
        empty = html::alert(
            Tone::Neutral,
            "Ready",
            "Submit a collection to read its public schema-validation state."
        ),
    );
    html::card("Data families", &body)
}

fn stream_card(state: &StudioState) -> String {
    let tenant = state.tenants().first().cloned().unwrap_or_else(|| "demo".to_owned());
    let collection =
        state.collections_of(&tenant).first().cloned().unwrap_or_else(|| "users".to_owned());
    let body = format!(
        "<p class=\"mb-4 text-sm text-muted-foreground\">Open a snapshot-bound document stream and fetch one bounded batch. The cursor remains an engine-native value and is not accepted from the browser.</p>\n<form class=\"grid gap-4\" data-on:submit=\"@post('/streams/documents', {{contentType: 'form', retry: 'never'}})\">\n<div class=\"grid gap-4 sm:grid-cols-3\">{tenant}{collection}{prefix}{limit}</div>\n<button type=\"submit\" class=\"inline-flex w-fit items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\">Read stream batch</button>\n</form>\n<div id=\"{STREAM_ID}\" class=\"mt-4\">{empty}</div>",
        tenant = html::text_input("tenant", "Tenant", &tenant, "demo"),
        collection = html::text_input("collection", "Collection", &collection, "users"),
        prefix = html::text_input("prefix", "Id prefix (optional)", "", "sample-"),
        limit = html::number_input("limit", "Batch limit", "20"),
        primary = ButtonStyle::Primary.classes(),
        STREAM_ID = STREAM_ID,
        empty = html::alert(
            Tone::Neutral,
            "Ready",
            "Submit a collection to read its first stream batch."
        ),
    );
    html::card("Reactive & stream state", &body)
}

fn limits_card() -> String {
    let rows = [
        (
            "Schema declarations",
            "Public family read and declaration APIs are available here through the bounded schema editor; migrations remain a separate typed drive.",
        ),
        (
            "Migrations",
            "The engine has a typed migration drive, but it requires a complete contract and host-owned resume state; no unsafe free-form transform editor is presented.",
        ),
        (
            "Relationships",
            "The relationship model is currently an in-memory typed store, not an Engine-backed durable operation.",
        ),
        (
            "Remote cluster/member actions",
            "Blocked here: ClusterHost and member sessions require real member locations and authenticated TBEP; this Studio never fabricates them.",
        ),
    ];
    let mut markup = String::new();
    for (name, note) in rows {
        markup.push_str(&html::alert(Tone::Neutral, name, note));
    }
    html::card("Capability limits", &markup)
}
