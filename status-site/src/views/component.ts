/* One component: its state and uptime over a range, latency with deploy
   markers, latency from each vantage, its clusters, deploys and incidents. */

import type { Details } from "../db/details";
import { RANGES } from "../domain/range";
import { percentile } from "../domain/histogram";
import { COMING_SOON, probeUptime, sumCounts } from "../domain/status";
import { formatUptime } from "../format";
import { html } from "../html";
import { Measurements } from "../measure";
import type { ComponentOverview, Overview } from "../overview";
import {
  bars,
  card,
  clusterRow,
  componentHref,
  empty,
  incidentItem,
  ms,
  rangeSwitch,
  stat,
  statusBadge,
  time,
  zone,
} from "./components";
import { latencyCharts } from "./latency";
import type { Page } from "./layout";

export function componentPage(view: Overview, entry: ComponentOverview, details: Details, now: number): Page {
  const measurements = new Measurements(details.periods);
  const ids = entry.clusters.map((cluster) => cluster.id);
  const charts = latencyCharts(measurements, ids, details.deploys, view.range, now, view.monitoring.coverage);
  const total = sumCounts(entry.bars.flatMap((bucket) => (bucket.counts ? [bucket.counts] : [])));
  const group = view.groups.find((item) => item.id === entry.groupId);
  const names = new Map(view.groups.flatMap((item) => item.components).map((item) => [item.id, item.name]));
  const range = RANGES[view.range].label;
  const base = componentHref(entry.id);
  const planned = entry.lifecycle === "planned";

  const body = html`<main id="main" class="ts-page" data-live data-title="${entry.name} · TerraScale Status">
  <header class="st-page-head">
    <p class="ts-eyebrow"><a class="ts-link" href="/">Status</a> · ${group?.name ?? "Component"}</p>
    <div class="ts-row">
      <h1 class="st-title">${entry.name}</h1>
      ${statusBadge(entry.status, planned ? COMING_SOON : (entry.note ?? undefined))}
    </div>
    <p class="ts-muted">${entry.description}</p>
  </header>

  ${
    planned
      ? empty("Not launched yet", `${entry.name} is coming soon. It will be measured here once it launches.`)
      : html`<dl class="ts-stats" aria-label="${entry.name} over the last ${range}">
    ${stat("Uptime", formatUptime(probeUptime(total)), `last ${range}`)}
    ${stat("Median", ms(percentile(total.hist, 0.5)), "response time")}
    ${stat("p95", ms(percentile(total.hist, 0.95)), "response time")}
    ${stat("Rounds", total.checks.toLocaleString("en"), `${total.failures.toLocaleString("en")} down`)}
  </dl>

  ${card(
    {
      id: "status",
      title: "Status",
      description: html`Over the last ${range}, in ${zone}.`,
      actions: rangeSwitch(view.range, base),
    },
    html`${bars(entry.bars, { label: `${entry.name}: ${formatUptime(entry.uptime)} uptime over the last ${range}` })}
  <div class="st-axis" aria-hidden="true"><span>${range} ago</span><span>Now</span></div>`,
  )}

  ${card({ id: "latency", title: "Response time", description: "Median and p95 of the consensus, with deploys marked.", actions: rangeSwitch(view.range, base) }, charts.summary)}
  ${card({ id: "vantages", title: "From each vantage point", description: "Median response seen from each vantage.", actions: rangeSwitch(view.range, base) }, charts.vantages)}

  ${card(
    { id: "clusters", title: "Clusters", description: "Every endpoint that serves this component." },
    html`<ul class="ts-list">${entry.clusters.map((cluster) => clusterRow(cluster, view.range, "cluster"))}</ul>`,
  )}`
  }

  ${
    details.deploys.length > 0
      ? card(
          {
            id: "deploys",
            title: "Deploys",
            description: "New versions seen on the health checks in the last 90 days.",
          },
          html`<ul class="ts-list">${details.deploys.map(
            (deploy) =>
              html`<li class="ts-list-item"><span class="ts-strong ts-num">${deploy.version}</span><span class="ts-meta">${deploy.endpointId} · ${time(deploy.at, "datetime")}</span></li>`,
          )}</ul>`,
        )
      : ""
  }

  ${card(
    {
      id: "incidents",
      title: "Incidents",
      description: "Incidents affecting this component, or the whole platform, in the last 90 days.",
    },
    details.incidents.length === 0
      ? empty("No incidents", `No incident affected ${entry.name} in the last 90 days.`, "circle-check")
      : html`<ul class="ts-list">${details.incidents.map((incident) => incidentItem(incident, now, names))}</ul>`,
  )}
</main>`;

  return {
    title: `${entry.name} · TerraScale Status`,
    description: `Live status, uptime and latency of ${entry.name}.`,
    status: view.status,
    path: null,
    body,
    live: true,
  };
}
