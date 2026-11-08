/* One incident: what happened, what it affected and for how long, the
   state and latency of the affected endpoints around it, and its timeline
   of updates, newest first. */

import type { IncidentDetail } from "../db/reads";
import {
  aggregate,
  IMPACT_STATUS,
  type Incident,
  spans,
  STATE_LABELS,
  worst,
  type Bucket,
  type TimelineState,
} from "../domain/status";
import { formatClock, formatDay, formatDuration } from "../format";
import { html, raw, type Html } from "../html";
import { chartLegend, lineChart } from "./charts/line";
import {
  affected,
  bars,
  badge,
  card,
  empty,
  incidentBadge,
  STATUS_TEXT,
  statusState,
  time,
  timing,
  zone,
} from "./components";
import type { Page } from "./layout";

const KINDS = { incident: "Incident", warning: "Warning", maintenance: "Maintenance" } as const;

/** The colour of each update's dot and badge; monitoring is neutral. */
const UPDATE_STATE: Partial<Record<Incident["status"], TimelineState>> = {
  investigating: "partial_outage",
  identified: "degraded",
  resolved: "operational",
  completed: "operational",
};

/** Buckets across the window for the state strip. */
const STRIP_BUCKETS = 48;

/** Milliseconds between chart points: every round, or every hour. */
const STEP = { round: 30_000, hour: 3_600_000 } as const;

/** Latency ranges offered beside the full window when it holds more samples. */
const RANGES = [
  { key: "1h", short: "1h", name: "Last hour", ms: 3_600_000 },
  { key: "30m", short: "30m", name: "Last 30 minutes", ms: 1_800_000 },
] as const;

/** Chart points across the window. */
function grid(detail: IncidentDetail): number[] {
  const step = STEP[detail.resolution];
  const first = detail.window.from - (detail.window.from % step);
  return Array.from(
    { length: Math.max(1, Math.ceil((detail.window.to - first) / step)) },
    (_, index) => first + index * step,
  );
}

/** The latency chart, one server-rendered view per range, and the radios
    picking a view; CSS shows the picked one, so it works without scripts. */
export function latency(detail: IncidentDetail): { chart: Html; picker: Html } {
  const points = grid(detail);
  const names = new Map(detail.components.map((component) => [component.id, component.name]));
  const series = detail.endpoints
    .map((endpoint) => {
      const values = new Map(
        detail.series
          .filter((point) => point.endpointId === endpoint.id)
          .map((point) => [Date.parse(point.start), point.latencyMs] as const),
      );
      return {
        name: `${names.get(endpoint.componentId) ?? endpoint.componentId} · ${endpoint.cluster}`,
        values: points.map((at) => values.get(at) ?? null),
      };
    })
    .filter((entry) => entry.values.some((value) => value !== null))
    .slice(0, 5);
  if (series.length === 0) {
    return { chart: empty("No measurements", "No health check results cover this incident's window."), picker: html`` };
  }
  const name = (at: number) => `${formatDay(at)} ${formatClock(at)} UTC`;
  const view = (from: number, range: string) => {
    const shown = points.slice(from);
    const ticks = [0, 1, 2, 3, 4].map((step) => formatClock(shown[Math.round((step * (shown.length - 1)) / 4)] ?? 0));
    return lineChart(
      {
        label: `Latency of the affected endpoints, ${range.toLowerCase()}`,
        ticks,
        points: shown.map(name),
        format: (value) => `${Math.round(value)} ms`,
      },
      series.map((entry) => ({ name: entry.name, values: entry.values.slice(from) })),
    );
  };
  const views = [
    { key: "full", short: "Full", name: "Full window", chart: view(0, "Full window") },
    ...RANGES.flatMap((range) => {
      const count = range.ms / STEP[detail.resolution];
      if (count < 4 || count >= points.length) return [];
      const chart = view(points.length - count, range.name);
      return chart.value ? [{ ...range, chart }] : [];
    }),
  ];
  const chart = html`${views.map(
    (entry) => html`<div class="st-chart-view" data-chart-view="${entry.key}">${entry.chart}</div>`,
  )}${series.length > 1 ? chartLegend(series.map((entry) => entry.name)) : ""}`;
  const picker =
    views.length > 1
      ? html`<fieldset class="st-range st-chart-range" aria-label="Latency time range">${views.map(
          (entry, index) =>
            html`<label><input class="ts-sr-only" type="radio" name="measured-range" value="${entry.key}" aria-label="${entry.name}" data-chart-pick${index === 0 ? raw(" checked") : ""}>${entry.short}</label>`,
        )}</fieldset>`
      : html``;
  return { chart, picker };
}

/** The affected endpoints' combined state across the window. */
function strip(detail: IncidentDetail): Html {
  const list: Bucket[] = spans(detail.window.from, detail.window.to, STRIP_BUCKETS).map((span) => {
    const inside = detail.series.filter((point) => {
      const at = Date.parse(point.start);
      return at >= span.start && at < span.end;
    });
    const byEndpoint = detail.endpoints.map((endpoint) =>
      inside
        .filter((point) => point.endpointId === endpoint.id)
        .reduce<TimelineState>((acc, point) => worst(acc, point.state), "none"),
    );
    return { ...span, state: aggregate(byEndpoint) };
  });
  return html`${bars(list, {
    label: "State of the affected endpoints during the incident",
    card: (bucket) => ({ rows: [["State", STATE_LABELS[bucket.state]]] }),
  })}
  <div class="st-axis" aria-hidden="true"><span>30 minutes before</span><span>${detail.incident.resolvedAt ? "30 minutes after" : "Now"}</span></div>`;
}

export function incidentPage(detail: IncidentDetail, now: number): Page {
  const { incident } = detail;
  const names = new Map(detail.components.map((component) => [component.id, component.name]));
  const start = Date.parse(incident.scheduledStart ?? incident.createdAt);
  const end = incident.resolvedAt ? Date.parse(incident.resolvedAt) : now;

  const measured = latency(detail);
  const impact = incident.kind === "maintenance" ? "maintenance" : IMPACT_STATUS[incident.impact];
  const components = incident.components.length === 0 ? ["All components"] : incident.components.map((id) => names.get(id) ?? id);

  const body = html`<main id="main" class="ts-page st-incident"${incident.resolvedAt ? "" : html` data-live data-title="${incident.title} · TerraScale Status"`}>
  <nav aria-label="Breadcrumb"><ol class="st-breadcrumb"><li><a href="/">Status</a></li><li><a href="/history">History</a></li><li aria-current="page">${incident.title}</li></ol></nav>
  <header class="st-page-head">
    <p class="ts-eyebrow">${KINDS[incident.kind]}${incident.source === "auto" ? " · Auto-detected" : ""}</p>
    <div class="ts-row">
      <h1 class="st-title">${incident.title}</h1>
      ${incidentBadge(incident, now)}
      ${badge(STATE_LABELS[impact], impact)}
    </div>
    <p class="ts-meta">${affected(incident, names)} · ${timing(incident)}</p>
  </header>

  <dl class="ts-stats" aria-label="Incident summary">
    <div class="ts-stat"><dt class="ts-stat-label">${incident.resolvedAt ? "Lasted" : "Ongoing for"}</dt><dd class="ts-stat-value st-stat-text">${formatDuration(Math.max(0, end - start))}</dd></div>
    <div class="ts-stat"><dt class="ts-stat-label">Started</dt><dd class="ts-stat-value">${time(start, "clock")}</dd><dd class="ts-stat-detail">${time(start, "datetime")}</dd></div>
    <div class="ts-stat"><dt class="ts-stat-label">Resolved</dt><dd class="ts-stat-value">${incident.resolvedAt ? time(incident.resolvedAt, "clock") : "—"}</dd><dd class="ts-stat-detail">${incident.resolvedAt ? time(incident.resolvedAt, "datetime") : "Ongoing"}</dd></div>
    <div class="ts-stat"><dt class="ts-stat-label">Updates</dt><dd class="ts-stat-value">${detail.updates.length}</dd></div>
  </dl>

  <div class="st-split">
    ${card(
      { id: "updates", eyebrow: "Timeline", title: "Updates", description: "Newest first." },
      detail.updates.length === 0
        ? empty("No updates", "This incident has no updates yet.")
        : html`<ol class="st-timeline">${detail.updates.map((update) => {
            const state = UPDATE_STATE[update.status] ?? "none";
            return html`<li class="st-timeline-item" data-state="${state}">
    <div class="ts-row">${badge(STATUS_TEXT[update.status] ?? update.status, state)}${time(update.createdAt, "datetime")}</div>
    <p class="st-incident-body">${update.body}</p>
  </li>`;
          })}</ol>`,
    )}

    <aside class="st-aside" aria-label="About this incident">
      <section class="ts-panel" aria-labelledby="affected-title">
        <h2 class="ts-label" id="affected-title">Affected components</h2>
        <ul class="st-affected">${components.map(
          (name) => html`<li data-state="${impact}"><span class="ts-dot" aria-hidden="true"></span><strong>${name}</strong>${badge(STATE_LABELS[impact], impact)}</li>`,
        )}</ul>
      </section>
      <section class="ts-panel st-follow">
        <div>
          <strong>Follow updates</strong>
          <p class="ts-muted">Email, RSS or a signed webhook.</p>
        </div>
        <a class="ts-button" href="/subscribe">Subscribe</a>
      </section>
    </aside>
  </div>

  ${card(
    {
      id: "measured",
      actions: measured.picker,
      eyebrow: "Health checks",
      title: "What the health checks saw",
      description: html`State and ${detail.resolution === "round" ? "per-round" : "hourly median"} latency of the affected endpoints, in ${zone}.`,
    },
    html`${strip(detail)}${measured.chart}`,
  )}
</main>`;

  return {
    title: `${incident.title} · TerraScale Status`,
    description: `${KINDS[incident.kind]}: ${incident.title}`,
    path: null,
    body,
    live: !incident.resolvedAt,
  };
}
