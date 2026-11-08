/* Markup helpers mirroring the ts-dashboard component kit (ui.Card, ui.Stat,
   ui.StatusBadge, ui.Empty) plus the status bars. */

import { DEFAULT_RANGE, RANGE_KEYS, RANGES, type RangeKey } from "../domain/range";
import { html, raw, type Html } from "../html";
import type { ClusterOverview } from "../overview";
import { EXPIRY_WARNING_MS } from "../probe/tls";
import { sparkline } from "./charts/sparkline";
import { hash } from "./charts/geometry";
import { icon, type IconName } from "./icons";
import { percentile } from "../domain/histogram";
import {
  IMPACT_STATUS,
  maintenancePhase,
  probeUptime,
  STATE_LABELS,
  stateAttr,
  type Bucket,
  type Incident,
  type MaintenancePhase,
  type ProbeCounts,
  type TimelineState,
} from "../domain/status";
import {
  DAY,
  formatClock,
  formatClockSeconds,
  formatDateTime,
  formatDay,
  formatDuration,
  formatUptime,
} from "../format";

export interface CardProps {
  id: string;
  title: string;
  eyebrow?: string;
  description?: string | Html;
  actions?: Html;
}

export function card(props: CardProps, body: Html): Html {
  return html`<section class="ts-card" id="${props.id}" aria-labelledby="${props.id}-title">
  <header class="ts-card-head">
    <div class="ts-card-heading">
      ${props.eyebrow ? html`<p class="ts-eyebrow">${props.eyebrow}</p>` : ""}
      <h2 class="ts-card-title" id="${props.id}-title">${props.title}</h2>
      ${props.description ? html`<p class="ts-card-description">${props.description}</p>` : ""}
    </div>
    ${props.actions ?? ""}
  </header>
  ${body}
</section>`;
}

export function stat(label: string, value: string, detail?: string, unit?: string): Html {
  return html`<div class="ts-stat">
  <dt class="ts-stat-label">${label}</dt>
  <dd class="ts-stat-value">${value}${unit ? html`<span class="ts-stat-unit">${unit}</span>` : ""}</dd>
  ${detail ? html`<dd class="ts-stat-detail">${detail}</dd>` : ""}
</div>`;
}

export function statusBadge(state: TimelineState, text: string = STATE_LABELS[state]): Html {
  return html`<span class="ts-badge" data-state="${stateAttr(state)}"><span class="ts-dot" aria-hidden="true"></span>${text}</span>`;
}

export function badge(text: string, state?: TimelineState): Html {
  return state
    ? html`<span class="ts-badge" data-state="${stateAttr(state)}">${text}</span>`
    : html`<span class="ts-badge">${text}</span>`;
}

/** An empty state: an icon tile, a title, one line and an optional action.
    The check icon means all clear, so its tile takes the success tint. */
export function empty(title: string, detail: string, glyph: IconName = "info", action?: Html): Html {
  return html`<div class="ts-empty" role="status">
  <span class="ts-empty-icon"${glyph === "circle-check" ? raw(' data-state="operational"') : ""} aria-hidden="true">${icon(glyph)}</span>
  <p class="ts-empty-title">${title}</p>
  <p class="ts-empty-detail">${detail}</p>
  ${action ?? ""}
</div>`;
}

/** A time rendered in UTC; the client script re-renders it in the visitor's
    time zone. */
export function time(at: string | number, format: "datetime" | "seconds" | "clock"): Html {
  const iso = new Date(at).toISOString().replace(".000Z", "Z");
  const text =
    format === "datetime"
      ? formatDateTime(at)
      : format === "clock"
        ? formatClock(Number(new Date(at)))
        : formatClockSeconds(Number(new Date(at)));
  return html`<time datetime="${iso}" data-format="${format}">${text}</time>`;
}

/** The time zone times are shown in; the client script names the visitor's. */
export const zone = html`<span data-zone>UTC</span>`;

/** A latency in milliseconds, or a dash. */
export const ms = (value: number | null): string => (value === null ? "—" : `${value} ms`);

/** What a hover card shows below its time and state. */
export interface CardContent {
  rows?: [string, string][];
  notes?: string[];
}

/** The rows describing a stretch of probes. */
export function probeRows(counts: ProbeCounts | undefined): [string, string][] {
  if (!counts || counts.checks === 0) return [["Checks", "None in this period"]];
  return [
    ["Uptime", formatUptime(probeUptime(counts))],
    ["Checks", `${(counts.checks - counts.failures).toLocaleString("en")} of ${counts.checks.toLocaleString("en")} up`],
    ["Median", ms(percentile(counts.hist, 0.5))],
    ["p95", ms(percentile(counts.hist, 0.95))],
  ];
}

export interface BarsOptions<T extends Bucket> {
  /** Summarizes the strip for assistive tech. */
  label: string;
  large?: boolean;
  card?: (bucket: T) => CardContent;
}

/** When a bar's time span was, in UTC; the client script re-renders spans
    shorter than a day in the visitor's time zone. */
function when(bucket: Bucket): string {
  if (bucket.end - bucket.start >= DAY) return formatDay(bucket.start);
  return `${formatDay(bucket.start)}, ${formatClock(bucket.start)}–${formatClock(bucket.end)} UTC`;
}

/** Compact server-rendered bars keep date/state details without scripts;
    the client expands the shared tooltip data into the usual interactive strip. */
export function bars<T extends Bucket>(list: readonly T[], options: BarsOptions<T>): Html {
  if (list.length === 0) return html``;
  const states = [...new Set(list.map((bucket) => bucket.state))];
  const data = JSON.stringify(
    list.map((bucket) => ({
      state: [stateAttr(bucket.state), STATE_LABELS[bucket.state]],
      from: bucket.start,
      to: bucket.end,
      card: options.card?.(bucket) ?? { rows: probeRows(bucket.counts) },
    })),
  );
  const id = `bars-${hash(options.label + list[0]!.start)}`;
  const paths = states.map((state) => {
    const rectangles: string[] = [];
    const spans: string[] = [];
    let from = -1;
    for (let index = 0; index <= list.length; index++) {
      if (list[index]?.state === state) {
        if (from < 0) from = index;
      } else if (from >= 0) {
        rectangles.push(`M${from * 10},0h${(index - from) * 10 - 2}v28H${from * 10}z`);
        spans.push(`${when(list[from]!)}${index - from > 1 ? `–${when(list[index - 1]!)}` : ""}`);
        from = -1;
      }
    }
    return html`<path data-state="${stateAttr(state)}" d="${rectangles.join("")}"><title>${STATE_LABELS[state]}: ${spans.join(", ")}</title></path>`;
  });
  return html`<svg class="st-bars st-bars-static${options.large ? " st-bars-lg" : ""}" role="img" tabindex="0" aria-label="${options.label}" viewBox="0 0 ${list.length * 10 - 2} 28" preserveAspectRatio="none" data-bars><defs><pattern id="${id}-pattern" width="10" height="28" patternUnits="userSpaceOnUse"><rect width="8" height="28" rx="2" fill="white"></rect></pattern><mask id="${id}-mask"><rect width="100%" height="28" fill="url(#${id}-pattern)"></rect></mask></defs><g mask="url(#${id}-mask)">${paths}</g></svg><script type="application/json" data-bar-record>${raw(data.replaceAll("<", "\\u003c"))}</script>`;
}

export function legend(states: readonly TimelineState[]): Html {
  return html`<ul class="st-legend" aria-label="Legend">${states.map(
    (state) =>
      html`<li data-state="${stateAttr(state)}"><span class="ts-dot" aria-hidden="true"></span>${STATE_LABELS[state]}</li>`,
  )}</ul>`;
}

export const STATUS_TEXT: Record<Incident["status"], string> = {
  investigating: "Investigating",
  identified: "Identified",
  monitoring: "Monitoring",
  resolved: "Resolved",
  scheduled: "Scheduled",
  in_progress: "In progress",
  completed: "Completed",
};

const PHASE_STATE: Record<MaintenancePhase, TimelineState> = {
  scheduled: "maintenance",
  in_progress: "maintenance",
  completed: "operational",
};

/** The state an incident status is colored in: closed is operational,
    maintenance is blue, and anything open carries the incident's impact. */
export function statusState(incident: Incident, status: string): TimelineState {
  if (status === "resolved" || status === "completed") return "operational";
  return incident.kind === "maintenance" ? "maintenance" : IMPACT_STATUS[incident.impact];
}

/** An incident's status text and the state coloring its badge. */
export function incidentBadge(incident: Incident, now: number): Html {
  if (incident.kind === "maintenance") {
    const phase = maintenancePhase(incident, now);
    return badge(STATUS_TEXT[phase], PHASE_STATE[phase]);
  }
  return badge(STATUS_TEXT[incident.status], statusState(incident, incident.status));
}

/** The labels beside an incident's title. */
function labels(incident: Incident, now: number): Html {
  return html`${incident.kind === "warning" ? badge("Warning") : ""}${incident.kind === "maintenance" ? badge("Maintenance") : ""}${
    incident.source === "auto"
      ? html`<span class="ts-badge" title="Opened by automated health checks">Auto-detected</span>`
      : ""
  }${incidentBadge(incident, now)}`;
}

/** When an incident started and, once resolved, how long it lasted; for
    maintenance, its window. */
export function timing(incident: Incident): Html {
  if (incident.kind === "maintenance" && incident.scheduledStart && incident.scheduledEnd) {
    return html`Scheduled ${time(incident.scheduledStart, "datetime")} to ${time(incident.scheduledEnd, "datetime")}`;
  }
  const started = html`Started ${time(incident.createdAt, "datetime")}`;
  if (!incident.resolvedAt) return html`${started} · updated ${time(incident.updatedAt, "datetime")}`;
  const lasted = formatDuration(Date.parse(incident.resolvedAt) - Date.parse(incident.createdAt));
  return html`${started} · resolved after ${lasted}`;
}

/** An incident's page. */
export const incidentHref = (id: string): string => `/incidents/${encodeURIComponent(id)}`;

/** The names of the components an incident affects, or the platform. */
export function affected(incident: Incident, names: ReadonlyMap<string, string>): string {
  if (incident.components.length === 0) return "All components";
  return incident.components.map((id) => names.get(id) ?? id).join(", ");
}

/** An active incident as an alert, colored by its impact. */
export function incidentAlert(incident: Incident, now: number, names: ReadonlyMap<string, string>): Html {
  const state = incident.kind === "maintenance" ? "maintenance" : IMPACT_STATUS[incident.impact];
  return html`<article class="ts-alert st-linked" data-state="${stateAttr(state)}">
  <div class="ts-row">
    <h3 class="ts-alert-title"><a class="st-incident-link" href="${incidentHref(incident.id)}">${incident.kind === "warning" ? "Warning: " : ""}${incident.title}</a></h3>
    ${labels(incident, now)}
  </div>
  <p class="ts-alert-body">${incident.message}</p>
  <p class="ts-meta">${affected(incident, names)} · ${timing(incident)}</p>
</article>`;
}

/** An incident as a divided list row; `extra` follows its timing. */
export function incidentItem(incident: Incident, now: number, names: ReadonlyMap<string, string>, extra?: Html): Html {
  return html`<li class="ts-list-item st-incident-row" data-state="${incident.kind === "maintenance" ? "maintenance" : IMPACT_STATUS[incident.impact]}">
  <span class="ts-dot st-incident-dot" aria-hidden="true"></span>
  <article class="ts-list-main">
    <div class="ts-row">
      <h3 class="st-incident-title"><a class="st-incident-link" href="${incidentHref(incident.id)}">${incident.title}</a></h3>
      ${labels(incident, now)}
    </div>
    <p class="st-incident-body">${incident.message}</p>
    <p class="ts-meta">${affected(incident, names)} · ${timing(incident)}${extra ? html` · ${extra}` : ""}</p>
  </article>
</li>`;
}

/** A component's page. */
export const componentHref = (id: string): string => `/components/${id}`;

/** A region's page. */
export const regionHref = (code: string): string => `/regions/${code}`;

const rangeHref = (base: string, range: RangeKey): string =>
  range === DEFAULT_RANGE ? base : `${base}?range=${range}`;

/** Links switching a page's time range; pages render without scripts. */
export function rangeSwitch(current: RangeKey, base: string): Html {
  return html`<nav class="st-range" aria-label="Time range">${RANGE_KEYS.map(
    (range) =>
      html`<a href="${rangeHref(base, range)}"${range === current ? raw(' aria-current="true"') : ""}>${RANGES[range].short}</a>`,
  )}</nav>`;
}

export const PROVIDERS: Record<ClusterOverview["provider"], string> = {
  cloudflare: "Cloudflare",
  other: "Self-hosted",
};

/** One cluster: its component, where it runs, pings, state and bars. */
export function clusterRow(
  entry: ClusterOverview,
  range: RangeKey,
  title: "component" | "cluster" = "component",
): Html {
  const summary = `${entry.componentName} on ${entry.cluster}: ${formatUptime(entry.uptime)} of checks up over the last ${RANGES[range].label}, currently ${(entry.note ?? STATE_LABELS[entry.status]).toLowerCase()}`;
  // A regional cluster is named after its region ("gru"), so the region is said once.
  const region = entry.regionCode?.toLowerCase();
  const where = `${PROVIDERS[entry.provider]} · ${entry.cluster}${region === undefined ? " · global" : region === entry.cluster ? "" : ` · ${region}`}`;
  const certificate = entry.certificate?.notAfter
    ? html` · <span class="st-tls" title="${entry.certificate.issuer ?? "TLS certificate"}"${Date.parse(entry.certificate.notAfter) - Date.now() < EXPIRY_WARNING_MS ? raw(' data-state="degraded"') : ""}>TLS until ${formatDay(Date.parse(entry.certificate.notAfter))}</span>`
    : "";
  return html`<li class="ts-list-item st-service">
  <div class="ts-list-main">
    <h4 class="ts-strong">${title === "component" ? html`<a class="st-incident-link" href="${componentHref(entry.componentId)}">${entry.componentName}</a>` : entry.cluster}</h4>
    <p class="ts-meta">${where}${certificate}</p>
  </div>
  <div class="st-service-state">
    ${sparkline(entry.latency)}
    <span class="ts-meta ts-num" title="Latest ping · mean over the range">${ms(entry.pingMs)} · ${ms(entry.meanLatency)}</span>
    ${statusBadge(entry.status, entry.note ?? undefined)}
  </div>
  ${bars(entry.bars, { label: summary })}
</li>`;
}
