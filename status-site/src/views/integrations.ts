/* Integrations: every way to follow TerraScale status from elsewhere, with
   snippets to copy: feeds, the calendar, badges, the widget, and the APIs. */

import { html, type Html } from "../html";
import type { Overview } from "../overview";
import { card } from "./components";
import type { Page } from "./layout";

function snippet(code: string): Html {
  return html`<pre class="st-code"><code>${code}</code></pre>`;
}

function row(title: string, detail: string, url: string, code?: string): Html {
  return html`<li class="ts-list-item st-integration">
  <div class="ts-list-main">
    <h3 class="ts-strong">${title}</h3>
    <p class="ts-meta">${detail}</p>
    <a class="ts-link st-mono" href="${url}">${url}</a>
    ${code ? snippet(code) : ""}
  </div>
</li>`;
}

export function integrationsPage(view: Overview, origin: string): Page {
  const components = view.groups.flatMap((group) => group.components).filter((entry) => entry.lifecycle === "active");
  // Per-component examples need a component; a fresh page shows the platform only.
  const first = components[0];

  const body = html`<main id="main" class="ts-page">
  <header class="st-page-head">
    <p class="ts-eyebrow">Developers</p>
    <h1 class="st-title">Integrations</h1>
    <p class="ts-muted">Follow TerraScale status from your reader, calendar, README, dashboard or code.</p>
  </header>

  ${card(
    { id: "feeds", title: "Feeds", description: "Every incident, warning and maintenance window, with its updates." },
    html`<ul class="ts-list">
    ${row("Atom", "For feed readers and Slack or Discord feed apps.", `${origin}/feed.atom`)}
    ${row("RSS", "The same entries as RSS 2.0.", `${origin}/feed.rss`)}
    ${row("Maintenance calendar", "Subscribe in Google Calendar, Outlook or Apple Calendar to see windows in your own.", `${origin}/maintenance.ics`)}
  </ul>`,
  )}

  ${card(
    { id: "badges", title: "Badges", description: "Live SVG badges for the platform or any component." },
    html`<div class="st-badges">
    <img src="/badge/status.svg" alt="TerraScale status" height="20">
    ${components.map((entry) => html`<img src="/badge/${entry.id}.svg" alt="${entry.name} status" height="20">`)}
  </div>
  ${snippet(
    first
      ? `[![TerraScale status](${origin}/badge/status.svg)](${origin})\n[![${first.name}](${origin}/badge/${first.id}.svg)](${origin}/components/${first.id})`
      : `[![TerraScale status](${origin}/badge/status.svg)](${origin})`,
  )}`,
  )}

  ${card(
    { id: "widget", title: "Widget", description: "A compact, live status widget for your own site." },
    html`<iframe class="st-embed-preview" src="/embed" title="TerraScale status widget" loading="lazy"></iframe>
  ${snippet(`<iframe src="${origin}/embed" title="TerraScale status" width="360" height="260" style="border:0"></iframe>`)}`,
  )}

  ${card(
    { id: "api", title: "APIs", description: "JSON with open CORS, cached for 10 seconds." },
    html`<ul class="ts-list">
    ${row("Region ping", "Median, p95 and sample counts for the last completed UTC hour, consensus and per vantage. Takes ?region=gru.", `${origin}/api/latency`, `curl -s ${origin}/api/latency?region=gru`)}
    ${row("Status", "Headline, components with bars, regions, recent incidents. Takes ?range=24h|7d|30d|90d.", `${origin}/api/v1/status`)}
    ${row("Incidents", "The latest incidents, warnings and maintenance with their updates. Takes ?limit=1–100.", `${origin}/api/v1/incidents`)}
    ${row("Regions", "Every region with its clusters and the latest latency from each vantage.", `${origin}/api/v1/regions`)}
    ${first ? row("Latency", "Median and p95 per bucket, overall and per vantage, for one component.", `${origin}/api/v1/latency?component=${first.id}&range=24h`) : ""}
    ${row(
      "Statuspage-compatible",
      "The Atlassian Statuspage format, for tools that already read status pages: status, summary, components, incidents (and /unresolved), scheduled-maintenances (and /upcoming, /active). Documents use the .json suffix.",
      `${origin}/api/v1/summary.json`,
      `curl -s ${origin}/api/v1/status.json | jq .status`,
    )}
  </ul>`,
  )}
</main>`;

  return {
    title: "Integrations · TerraScale Status",
    description: "Feeds, badges, a widget and APIs for TerraScale status.",
    status: view.status,
    path: null,
    body,
  };
}
