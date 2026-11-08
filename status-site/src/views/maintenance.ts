/* Scheduled maintenance: windows in progress and upcoming, with a
   countdown to each, then those completed in the last 90 days. During a
   window the components it covers show as under maintenance and the health
   checks open no incidents for them. */

import { maintenancePhase, type Component, type Incident } from "../domain/status";
import { html, type Html } from "../html";
import { card, empty, incidentItem } from "./components";
import type { Page } from "./layout";

export const MAINTENANCE_DAYS = 90;

/** "Starts in 2h 5m" or "ends in 40m", counted down by the client script. */
export function countdown(window: Incident, now: number): Html {
  const phase = maintenancePhase(window, now);
  if (phase === "completed") return html``;
  const at = phase === "scheduled" ? window.scheduledStart : window.scheduledEnd;
  if (!at) return html``;
  return html`<span class="st-countdown" data-until="${Date.parse(at)}" data-prefix="${phase === "scheduled" ? "Starts" : "Ends"}"></span>`;
}

export function maintenanceItems(windows: readonly Incident[], now: number, names: ReadonlyMap<string, string>): Html {
  return html`<ul class="ts-list">${windows.map((window) => incidentItem(window, now, names, countdown(window, now)))}</ul>`;
}

export function maintenancePage(windows: readonly Incident[], components: readonly Component[], now: number): Page {
  const names = new Map(components.map((component) => [component.id, component.name]));
  const open = windows
    .filter((window) => maintenancePhase(window, now) !== "completed")
    .sort((a, b) => Date.parse(a.scheduledStart ?? "") - Date.parse(b.scheduledStart ?? ""));
  const done = windows.filter((window) => maintenancePhase(window, now) === "completed");

  const body = html`<main id="main" class="ts-page" data-live data-title="Scheduled maintenance · TerraScale Status">
  <header class="st-page-head">
    <p class="ts-eyebrow">Planning</p>
    <h1 class="st-title">Scheduled maintenance</h1>
    <p class="ts-muted">Planned work on TerraScale components. Subscribe to the calendar to see windows in your own.</p>
  </header>
  ${card(
    { id: "upcoming", title: "In progress and upcoming", description: "Soonest first." },
    open.length === 0
      ? empty("No maintenance scheduled", "Planned maintenance will be announced here in advance.")
      : maintenanceItems(open, now, names),
  )}
  ${card(
    { id: "completed", title: "Completed", description: `Windows that ended in the last ${MAINTENANCE_DAYS} days.` },
    done.length === 0
      ? empty("Nothing completed recently", "No maintenance ended in this period.")
      : html`<ul class="ts-list">${done.map((window) => incidentItem(window, now, names))}</ul>`,
  )}
</main>`;

  return {
    title: "Scheduled maintenance · TerraScale Status",
    description: "Planned maintenance windows for TerraScale components.",
    path: "/maintenance",
    body,
    live: true,
  };
}
