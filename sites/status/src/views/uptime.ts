/* Uptime: a year of daily platform status as a calendar, a monthly uptime
   table per component, and every component's
   90-day bars. Uptime is the share of probe rounds that were not down. */

import type { VantagePeriod } from "../db/details";
import { aggregate, probeState, probeUptime, sumCounts, type Bucket, type ProbeCounts } from "../domain/status";
import { DAY, formatMonthShort, formatUptime } from "../format";
import { html, type Html } from "../html";
import { Measurements } from "../measure";
import type { Overview } from "../overview";
import { heatmap, lastDays } from "./charts/heatmap";
import { bars, card, componentHref, empty, probeRows, stat, statusBadge } from "./components";
import type { Page } from "./layout";

export const YEAR_DAYS = 365;

const MONTHS = 12;

interface Month {
  from: number;
  to: number;
}

function months(now: number): Month[] {
  return Array.from({ length: MONTHS }, (_, index) => {
    const start = new Date(now);
    start.setUTCDate(1);
    start.setUTCHours(0, 0, 0, 0);
    start.setUTCMonth(start.getUTCMonth() - index);
    const end = new Date(start);
    end.setUTCMonth(end.getUTCMonth() + 1);
    return { from: start.getTime(), to: Math.min(end.getTime(), now + DAY) };
  });
}

function uptimeCell(counts: ProbeCounts): Html {
  const value = probeUptime(counts);
  if (value === null) return html`<td class="st-sla-cell" data-sla="none">—</td>`;
  return html`<td class="st-sla-cell ts-num" data-sla="measured">${formatUptime(value)}</td>`;
}

export function uptimePage(view: Overview, periods: readonly VantagePeriod[], now: number): Page {
  const measurements = new Measurements(periods);
  const components = view.groups.flatMap((group) => group.components).filter((entry) => entry.lifecycle === "active");
  const byComponent = new Map(
    components.map((component) => [
      component.id,
      view.endpoints.filter((endpoint) => endpoint.componentId === component.id).map((endpoint) => endpoint.id),
    ]),
  );
  const allEndpoints = [...byComponent.values()].flat();

  const days: Bucket[] = lastDays(now, YEAR_DAYS).map((start) => {
    const counts = measurements.counts(allEndpoints, "all", start, start + DAY, DAY);
    const state = aggregate(
      components.map((component) =>
        aggregate(
          (byComponent.get(component.id) ?? []).map((id) =>
            probeState(measurements.counts([id], "all", start, start + DAY, DAY)),
          ),
        ),
      ),
    );
    return { start, end: start + DAY, state, counts };
  });
  const year = sumCounts(days.flatMap((day) => (day.counts ? [day.counts] : [])));
  const monthList = months(now);
  const measuredDays = days.filter((day) => (day.counts?.checks ?? 0) > 0).length;

  const table = html`<div class="st-table-wrap"><table class="st-sla">
  <caption class="ts-sr-only">Measured monthly uptime per component</caption>
  <thead><tr><th scope="col">Component</th>${monthList.map((month) => html`<th scope="col">${formatMonthShort(month.from)}</th>`)}</tr></thead>
  <tbody>
    ${components.map(
      (component) =>
        html`<tr><th scope="row"><a class="st-incident-link" href="${componentHref(component.id)}">${component.name}</a></th>${monthList.map(
          (month) =>
            uptimeCell(measurements.counts(byComponent.get(component.id) ?? [], "all", month.from, month.to, DAY)),
        )}</tr>`,
    )}
    <tr class="st-sla-total"><th scope="row">Platform</th>${monthList.map((month) => uptimeCell(measurements.counts(allEndpoints, "all", month.from, month.to, DAY)))}</tr>
  </tbody>
</table></div>`;

  const body = html`<main id="main" class="ts-page">
  <header class="st-page-head">
    <p class="ts-eyebrow">Reliability</p>
    <h1 class="st-title">Uptime</h1>
    <p class="ts-muted">Measured uptime comes from health check history stored independently in Cloudflare D1. It is the share of 30-second rounds from answering vantage points in which a component was not down. Unmeasured time is excluded, not counted as healthy.</p>
    <p class="ts-muted"><strong>No SLA during Public Alpha.</strong> These preliminary measurements are not an availability commitment or a basis for service credits. SLA numbers will only be considered after 30 consecutive days of clean monitoring data. <a class="ts-link" href="https://docs.terrascale.tech/legal/sla">Read the SLA policy</a>.</p>
    <p class="ts-muted">Current monitoring coverage: ${view.monitoring.coverage.verified} of ${view.monitoring.coverage.total} vantage points verified.${view.monitoring.delayed ? " Monitoring is delayed; recent status may be out of date." : ""} Historical data may include incomplete coverage and earlier probe defects.</p>
  </header>

  <dl class="ts-stats" aria-label="Uptime summary">
    ${stat("Last 12 months", formatUptime(probeUptime(year)), "platform")}
    ${stat("Last 90 days", formatUptime(view.uptimeHistory), "platform")}
    ${stat("Measured", String(measuredDays), `of the last ${YEAR_DAYS}`, measuredDays === 1 ? "day" : "days")}
  </dl>

  ${card(
    {
      id: "calendar",
      title: "Last 12 months",
      description: "Daily platform status in UTC days. Hover, focus or tap a day for details.",
    },
    heatmap(days, `Platform: ${formatUptime(probeUptime(year))} uptime over the last ${YEAR_DAYS} days`, (day) => ({
      rows: probeRows(day.counts),
    })),
  )}

  ${card(
    {
      id: "monthly",
      title: "Monthly uptime",
      description: "Observed availability only. Missing months have no data; the current month is still open.",
    },
    components.length === 0
      ? empty("Nothing measured yet", "Monthly uptime appears once components are measured.")
      : table,
  )}

  ${card(
    { id: "components", title: "Last 90 days", description: "Daily status of every measured component." },
    html`<ul class="ts-list">${components.map(
      (component) => html`<li class="ts-list-item st-service">
    <div class="ts-list-main"><h3 class="ts-strong"><a class="st-incident-link" href="${componentHref(component.id)}">${component.name}</a></h3></div>
    <div class="st-service-state"><span class="ts-meta ts-num">${formatUptime(component.uptime)}</span>${statusBadge(component.status, component.note ?? undefined)}</div>
    ${bars(component.bars, { label: `${component.name}: ${formatUptime(component.uptime)} uptime over the last 90 days` })}
  </li>`,
    )}</ul>`,
  )}
</main>`;

  return {
    title: "Uptime · TerraScale Status",
    description: "A year of measured uptime for TerraScale components.",
    status: view.status,
    path: "/uptime",
    body,
  };
}
