/* Incident history: incidents and warnings filtered by component, kind,
   impact, text and month, with their figures, grouped by month. The
   filters are a plain GET form and links, so the page needs no script. */

import type { History } from "../db/reads";
import { incidentStats } from "../domain/history";
import type { Incident } from "../domain/status";
import { DAY, formatDuration, formatMonth, formatMonthShort } from "../format";
import { html, raw, type Html } from "../html";
import { card, empty, incidentItem, stat } from "./components";
import type { Page } from "./layout";

export interface HistoryQuery {
  component?: string;
  kind?: string;
  impact?: string;
  q?: string;
  /** "YYYY-MM"; without it the page covers the last twelve months. */
  month?: string;
}

/** Months of history offered as links. */
export const HISTORY_MONTHS = 12;

/** The window a query covers. */
export function historyWindow(query: HistoryQuery, now: number): { from: number; to: number } {
  if (query.month) {
    const from = Date.parse(`${query.month}-01T00:00:00Z`);
    const next = new Date(from);
    next.setUTCMonth(next.getUTCMonth() + 1);
    return { from, to: next.getTime() };
  }
  // The first of the month HISTORY_MONTHS - 1 months back, like the links.
  const start = new Date(now);
  start.setUTCDate(1);
  start.setUTCHours(0, 0, 0, 0);
  start.setUTCMonth(start.getUTCMonth() - (HISTORY_MONTHS - 1));
  return { from: start.getTime(), to: now + DAY };
}

function byMonth(incidents: readonly Incident[]): Map<string, Incident[]> {
  const groups = new Map<string, Incident[]>();
  for (const incident of incidents) {
    const month = incident.createdAt.slice(0, 7);
    groups.set(month, [...(groups.get(month) ?? []), incident]);
  }
  return groups;
}

/** The history URL for a query. */
function href(query: HistoryQuery): string {
  const params = new URLSearchParams(
    Object.entries(query).filter((entry): entry is [string, string] => typeof entry[1] === "string" && entry[1] !== ""),
  );
  const search = params.toString();
  return `/history${search ? `?${search}` : ""}`;
}

function option(value: string, label: string, current: string | undefined): Html {
  return html`<option value="${value}"${value === (current ?? "") ? raw(" selected") : ""}>${label}</option>`;
}

function filters(history: History, query: HistoryQuery): Html {
  return html`<form class="st-filters" method="get" action="/history" role="search" aria-label="Filter incidents">
  <label class="st-field"><span>Component</span>
    <select class="ts-select" name="component">
      ${option("", "All components", query.component)}
      ${history.components.map((component) => option(component.id, component.name, query.component))}
    </select>
  </label>
  <label class="st-field"><span>Kind</span>
    <select class="ts-select" name="kind">
      ${option("", "All kinds", query.kind)}${option("incident", "Incidents", query.kind)}${option("warning", "Warnings", query.kind)}
    </select>
  </label>
  <label class="st-field"><span>Impact</span>
    <select class="ts-select" name="impact">
      ${option("", "Any impact", query.impact)}${option("minor", "Minor", query.impact)}${option("major", "Major", query.impact)}${option("critical", "Critical", query.impact)}
    </select>
  </label>
  <label class="st-field st-field-grow"><span>Search</span>
    <input class="ts-input" type="search" name="q" value="${query.q ?? ""}" maxlength="80" placeholder="Search titles">
  </label>
  ${query.month ? html`<input type="hidden" name="month" value="${query.month}">` : ""}
  <button class="ts-button" type="submit">Filter</button>
</form>`;
}

function months(query: HistoryQuery, now: number): Html {
  const list = Array.from({ length: HISTORY_MONTHS }, (_, index) => {
    const date = new Date(now);
    date.setUTCDate(1);
    date.setUTCMonth(date.getUTCMonth() - index);
    return date.toISOString().slice(0, 7);
  });
  // The range on view is plain text; only the others link.
  const link = (month: string | undefined, label: string) =>
    query.month === month
      ? html`<span aria-current="true">${label}</span>`
      : html`<a href="${href({ ...query, month })}">${label}</a>`;
  // Short names ("Oct") keep the strip on one row; twelve straight months need no year.
  return html`<nav class="st-range st-months" aria-label="Months">${link(undefined, "12 months")}${list.map((month) =>
    link(month, formatMonthShort(`${month}-01T00:00:00Z`).replace(/ ’\d{2}$/, "")),
  )}</nav>`;
}

export function historyPage(history: History, query: HistoryQuery, now: number): Page {
  const stats = incidentStats(history.incidents);
  const names = new Map(history.components.map((component) => [component.id, component.name]));
  const filtered = Boolean(query.component || query.kind || query.impact || query.q);
  const groups = [...byMonth(history.incidents)];
  const period = query.month ? formatMonth(`${query.month}-01T00:00:00Z`) : "the last 12 months";

  const body = html`<main id="main" class="ts-page st-history">
  <header class="st-page-head">
    <p class="ts-eyebrow">Transparency</p>
    <h1 class="st-title">Incident history</h1>
    <p class="ts-muted">Incidents and operational warnings from the TerraScale platform, newest first. Automatic incidents are opened by the health checks themselves.</p>
  </header>

  <dl class="ts-stats" aria-label="Figures for ${period}">
    ${stat("Incidents", String(stats.count), period)}
    ${stat("Mean time to recovery", stats.mttrMs === null ? "—" : formatDuration(stats.mttrMs), `${stats.resolved} resolved`)}
    ${stat("Longest", stats.longest ? formatDuration(stats.longest.durationMs) : "—", stats.longest?.title ?? "nothing resolved")}
    ${stat("Critical", String(stats.byImpact.critical), `${stats.byImpact.major} major · ${stats.byImpact.minor} minor`)}
  </dl>

  <section class="st-history-controls" aria-label="Filters">
    ${filters(history, query)}
    ${months(query, now)}
    ${filtered ? html`<p class="ts-meta">Filtered · <a class="ts-link" href="${href({ month: query.month })}">Clear filters</a></p>` : ""}
  </section>

  ${
    groups.length === 0
      ? empty(
          filtered ? "No matching incidents" : "No incidents recorded",
          filtered
            ? "No incident or warning matches these filters."
            : `TerraScale reported no incidents or warnings in ${period}.`,
          filtered ? "search-x" : "circle-check",
        )
      : groups.map(([month, list]) =>
          card(
            {
              id: `month-${month}`,
              title: formatMonth(`${month}-01T00:00:00Z`),
              description: `${list.length} ${list.length === 1 ? "entry" : "entries"}`,
            },
            html`<ul class="ts-list">${list.map((incident) => incidentItem(incident, now, names))}</ul>`,
          ),
        )
  }
</main>`;

  return {
    title: "Incident history · TerraScale Status",
    description: "Past incidents and operational warnings for TerraScale services.",
    path: "/history",
    body,
  };
}
