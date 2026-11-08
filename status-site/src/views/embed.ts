/* The embeddable widget: the headline and the state of every measured
   component, sized for an iframe on another site. It refreshes in place
   like the status page, and links open the full page in a new tab. */

import { HEADLINES, stateAttr } from "../domain/status";
import { html } from "../html";
import type { Overview } from "../overview";
import { statusBadge } from "./components";
import type { Page } from "./layout";

export function embedPage(view: Overview, origin: string): Page {
  const components = view.groups.flatMap((group) => group.components).filter((entry) => entry.lifecycle === "active");
  const body = html`<main id="main" class="st-embed" data-live data-title="${HEADLINES[view.status]} · TerraScale Status">
  <a class="st-embed-head" href="${origin}/" target="_blank" rel="noopener" data-state="${stateAttr(view.status)}">
    <span class="ts-dot" aria-hidden="true"></span>
    <span class="ts-strong">${HEADLINES[view.status]}</span>
    <span class="ts-meta">TerraScale Status ↗</span>
  </a>
  <ul class="st-embed-list">${components.map(
    (entry) => html`<li><span>${entry.name}</span>${statusBadge(entry.status, entry.note ?? undefined)}</li>`,
  )}</ul>
  ${
    view.activeIncidents.length > 0
      ? html`<p class="ts-meta">${view.activeIncidents.length} active ${view.activeIncidents.length === 1 ? "incident" : "incidents"} · <a class="ts-link" href="${origin}/" target="_blank" rel="noopener">details</a></p>`
      : ""
  }
</main>`;
  return {
    title: "TerraScale Status",
    description: "TerraScale status widget.",
    path: null,
    body,
    live: true,
    bare: true,
  };
}
