//! The dedicated operator metrics page.
//!
//! This is a UI view over the same bounded, in-process snapshot used by the
//! overview. It is local diagnostics, not the production scrape boundary.

use crate::html;
use crate::metrics;
use crate::state::StudioState;

/// Renders the metrics page and opens one Datastar SSE stream for refreshes.
pub fn render(state: &StudioState) -> String {
    let snapshot = metrics::gather(state);
    let body = format!(
        "{heading}\n<div data-metrics-stream=\"/events/metrics\" data-show=\"false\" aria-hidden=\"true\"></div>\n{panel}\n{note}",
        heading = html::heading(
            "Metrics",
            "Live engine, storage, collection, index, and tier facts from this Studio process.",
        ),
        panel = metrics::render_panel(&snapshot),
        note = html::alert(
            html::Tone::Neutral,
            "Metrics boundary",
            "This page reads the in-process engine. Its Prometheus preview is available at /metrics/prometheus on the same loopback listener. ts-orchestrator owns the separate private production scrape listener, workload identity, network policy, and discovery.",
        ),
    );
    html::page("Metrics", "/metrics", "", &body)
}
