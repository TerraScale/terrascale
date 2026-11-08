/* The admin pages: sign-in, incidents, one incident, the catalog,
   subscribers and probes. Plain server-rendered forms posting to
   /admin/...; each carries the session's CSRF token. */

import type { Component, ComponentGroup, Incident, IncidentUpdate, Region } from "../domain/status";
import { COMING_SOON, type TimelineState } from "../domain/status";
import { html, raw, type Html } from "../html";
import { CANARY_VANTAGE } from "../probe/canary";
import { VANTAGES } from "../probe/vantages";
import { card, empty, statusBadge, time } from "../views/components";
import type { Page } from "../views/layout";
import { IMPACTS, KINDS, PROBES, PROVIDERS, statusesOf } from "./actions";

/** An endpoint with everything the admin page edits. */
export interface AdminEndpoint {
  id: string;
  componentId: string;
  regionCode: string | null;
  cluster: string;
  provider: string;
  url: string;
  headers: string;
  position: number;
  expectStatus: string;
  keyword: string | null;
  assertions: string;
  extract: string;
  indirect: number;
  kind: "http" | "canary";
  probe: string | null;
  blockedStatus: number | null;
  liveSince: string | null;
}

export interface AdminRegion extends Region {
  position: number;
}

export interface Catalog {
  groups: (ComponentGroup & { position: number })[];
  components: (Component & { position: number })[];
  endpoints: AdminEndpoint[];
  regions: AdminRegion[];
}

export interface Subscriber {
  id: string;
  channel: string;
  target: string;
  flavor: string;
  components: string;
  confirmedAt: string | null;
  createdAt: string;
  pending: number;
  failed: number;
}

/** One endpoint's probe: the latest stored round, or a check run now. */
export interface ProbeRow {
  endpoint: AdminEndpoint;
  componentName: string;
  at: string | null;
  state: TimelineState;
  results: { v: string; ok: 0 | 1 | null; ms: number | null; e: string | null }[];
}

const DONE: Record<string, string> = {
  created: "Created.",
  saved: "Saved.",
  posted: "Update posted.",
  deleted: "Deleted.",
  recorded: "Deploy recorded.",
};

const NAV = [
  { href: "/admin", label: "Incidents" },
  { href: "/admin/catalog", label: "Catalog" },
  { href: "/admin/subscribers", label: "Subscribers" },
  { href: "/admin/probes", label: "Probes" },
] as const;

export interface Frame {
  csrf: string;
  path: (typeof NAV)[number]["href"];
  done?: string;
}

const hidden = (csrf: string) => html`<input type="hidden" name="csrf" value="${csrf}">`;

function shell(frame: Frame, title: string, body: Html): Page {
  return {
    title: `${title} · Admin · TerraScale Status`,
    description: "TerraScale status administration.",
    path: null,
    body: html`<main id="main" class="ts-page ad-page">
  <header class="st-page-head ts-row-between">
    <div>
      <p class="ts-eyebrow">Admin</p>
      <h1 class="st-title">${title}</h1>
    </div>
    <form method="post" action="/admin/logout">${hidden(frame.csrf)}<button class="ts-button ad-quiet" type="submit">Sign out</button></form>
  </header>
  <nav class="st-range ad-nav" aria-label="Admin pages">${NAV.map(
    (item) =>
      html`<a href="${item.href}"${frame.path === item.href ? raw(' aria-current="true"') : ""}>${item.label}</a>`,
  )}</nav>
  ${frame.done && DONE[frame.done] ? html`<article class="ts-alert" data-state="operational" role="status"><p class="ts-alert-body">${DONE[frame.done]}</p></article>` : ""}
  ${body}
</main>`,
  };
}

const field = (label: string, control: Html, wide = false) =>
  html`<label class="st-field${wide ? " ad-wide" : ""}"><span>${label}</span>${control}</label>`;

const input = (name: string, value: string | number | null | undefined, extra = "") =>
  html`<input class="ts-input" name="${name}" value="${value ?? ""}"${raw(extra ? ` ${extra}` : "")}>`;

const textarea = (name: string, value: string | null | undefined, extra = "") =>
  html`<textarea class="ts-input ad-text" name="${name}"${raw(extra ? ` ${extra}` : "")}>${value ?? ""}</textarea>`;

function select(name: string, options: readonly (string | [string, string])[], current: string | null, blank?: string) {
  return html`<select class="ts-select" name="${name}">${blank === undefined ? "" : html`<option value="">${blank}</option>`}${options.map(
    (option) => {
      const [value, label] = typeof option === "string" ? [option, option.replace("_", " ")] : option;
      return html`<option value="${value}"${value === current ? raw(" selected") : ""}>${label}</option>`;
    },
  )}</select>`;
}

function componentChoices(components: readonly Component[], chosen: readonly string[]) {
  return html`<fieldset class="st-choices st-components ad-wide"><legend>Components <span class="ts-meta">(none checked: the whole platform)</span></legend>${components.map(
    (component) =>
      html`<label class="st-choice"><input type="checkbox" name="components" value="${component.id}"${chosen.includes(component.id) ? raw(" checked") : ""}> ${component.name}</label>`,
  )}</fieldset>`;
}

/** A delete button that needs a ticked box, so a stray click removes nothing. */
const remove = (frame: Frame, action: string, what: string) =>
  html`<form class="ad-delete" method="post" action="${action}">${hidden(frame.csrf)}<label class="st-choice"><input type="checkbox" name="confirm" value="yes" required> Delete ${what}</label><button class="ts-button ad-danger" type="submit">Delete</button></form>`;

/** "2026-10-01T02:00:00Z" → "2026-10-01T02:00" for datetime-local inputs, in UTC. */
const local = (at: string | null) => (at ? at.slice(0, 16) : "");

export function loginPage(error?: string): Page {
  return {
    title: "Sign in · Admin · TerraScale Status",
    description: "TerraScale status administration.",
    path: null,
    body: html`<main id="main" class="ts-page st-narrow">
  <header class="st-page-head"><p class="ts-eyebrow">Admin</p><h1 class="st-title">Sign in</h1></header>
  ${error ? html`<article class="ts-alert" data-state="outage" role="alert"><p class="ts-alert-body">${error}</p></article>` : ""}
  ${card(
    { id: "login", title: "TerraScale account" },
    html`<p>Requires an owner or admin role in the TerraScale organization.</p>
    <a class="ts-button" href="/admin/authorize">Continue with TerraScale</a>`,
  )}
</main>`,
  };
}

/** A failed admin action, with a way back. */
export function problemPage(frame: Frame, message: string): Page {
  return shell(
    frame,
    "Not saved",
    html`<article class="ts-alert" data-state="outage" role="alert"><p class="ts-alert-body">${message}</p></article>
  <p class="ts-meta">Go back to fix the form, or return to <a class="ts-link" href="${frame.path}">${NAV.find((item) => item.href === frame.path)?.label ?? "Admin"}</a>.</p>`,
  );
}

const incidentRow = (incident: Incident) => html`<li class="ts-list-item">
  <div class="ts-list-main st-linked">
    <h4 class="ts-strong"><a class="st-incident-link" href="/admin/incidents/${incident.id}">${incident.title}</a></h4>
    <p class="ts-meta">${incident.kind} · ${incident.status.replace("_", " ")} · ${incident.impact} impact${incident.source === "auto" ? " · automatic" : ""} · ${time(incident.createdAt, "datetime")}</p>
  </div>
</li>`;

export function incidentsPage(frame: Frame, open: Incident[], closed: Incident[], components: Component[]): Page {
  return shell(
    frame,
    "Incidents",
    html`${card(
      { id: "open", title: "Open", description: "Open incidents and warnings, and maintenance not yet completed." },
      open.length === 0
        ? empty("Nothing open", "No incident, warning or maintenance is open.", "circle-check")
        : html`<ul class="ts-list">${open.map(incidentRow)}</ul>`,
    )}
  ${card(
    {
      id: "new",
      title: "Publish",
      description: "An incident, a warning, or a maintenance window (its schedule in UTC). Subscribers are notified.",
    },
    html`<form class="ad-form" method="post" action="/admin/incidents">${hidden(frame.csrf)}
    ${field("Kind", select("kind", KINDS, "incident"))}
    ${field("Impact", select("impact", IMPACTS, null, "Default"))}
    ${field("Title", input("title", "", 'required maxlength="200"'), true)}
    ${field("Message", textarea("message", "", 'required maxlength="4000"'), true)}
    ${field("Maintenance starts (UTC)", input("scheduledStart", "", 'type="datetime-local"'))}
    ${field("Maintenance ends (UTC)", input("scheduledEnd", "", 'type="datetime-local"'))}
    ${componentChoices(components, [])}
    <button class="ts-button" type="submit">Publish</button>
  </form>`,
  )}
  ${card(
    { id: "closed", title: "Recently closed" },
    closed.length === 0
      ? empty("Nothing closed yet", "Resolved incidents appear here.")
      : html`<ul class="ts-list">${closed.map(incidentRow)}</ul>`,
  )}`,
  );
}

export function incidentAdminPage(
  frame: Frame,
  incident: Incident,
  updates: IncidentUpdate[],
  components: Component[],
): Page {
  const maintenance = incident.kind === "maintenance";
  return shell(
    frame,
    incident.title,
    html`<p class="ts-meta">${incident.kind} · ${incident.status.replace("_", " ")} · ${incident.impact} impact${incident.source === "auto" ? " · automatic (editing takes it over from the health checks)" : ""} · <a class="ts-link" href="/incidents/${incident.id}">Public page</a></p>
  ${card(
    { id: "update", title: "Post an update", description: "Changes the status; subscribers are notified." },
    html`<form class="ad-form" method="post" action="/admin/incidents/${incident.id}/updates">${hidden(frame.csrf)}
    ${field("Status", select("status", statusesOf(incident.kind), incident.status))}
    ${field("Impact", select("impact", IMPACTS, null, "Unchanged"))}
    ${field("Message", textarea("message", "", 'required maxlength="4000"'), true)}
    <button class="ts-button" type="submit">Post update</button>
  </form>`,
  )}
  ${card(
    { id: "edit", title: "Edit" },
    html`<form class="ad-form" method="post" action="/admin/incidents/${incident.id}/edit">${hidden(frame.csrf)}
    ${field("Title", input("title", incident.title, 'required maxlength="200"'), true)}
    ${field("Impact", select("impact", IMPACTS, incident.impact))}
    ${
      maintenance
        ? html`${field("Starts (UTC)", input("scheduledStart", local(incident.scheduledStart), 'type="datetime-local" required'))}
    ${field("Ends (UTC)", input("scheduledEnd", local(incident.scheduledEnd), 'type="datetime-local" required'))}`
        : ""
    }
    ${componentChoices(components, incident.components)}
    <button class="ts-button" type="submit">Save</button>
  </form>`,
  )}
  ${card(
    { id: "timeline", title: "Updates" },
    html`<ol class="st-timeline">${updates.map(
      (update) =>
        html`<li class="st-timeline-item"><p class="ts-strong">${update.status.replace("_", " ")} · ${time(update.createdAt, "datetime")}</p><p>${update.body}</p></li>`,
    )}</ol>`,
  )}
  ${card({ id: "delete", title: "Delete", description: "Only for records published by mistake: it disappears from the page, the feeds and the history." }, remove(frame, `/admin/incidents/${incident.id}/delete`, "this record"))}`,
  );
}

const pending = (endpoint: AdminEndpoint) =>
  endpoint.liveSince
    ? html`<span class="ts-meta">live since ${time(endpoint.liveSince, "datetime")}</span>`
    : statusBadge("none", "Pending launch");

function endpointForm(
  frame: Frame,
  action: string,
  catalog: Catalog,
  endpoint: Partial<AdminEndpoint>,
  submit: string,
) {
  return html`<form class="ad-form" method="post" action="${action}">${hidden(frame.csrf)}
    ${field(
      "Component",
      select(
        "componentId",
        catalog.components.map((item): [string, string] => [item.id, item.name]),
        endpoint.componentId ?? null,
      ),
    )}
    ${field(
      "Region",
      select(
        "regionCode",
        catalog.regions.map((item): [string, string] => [item.code, `${item.code} · ${item.name}`]),
        endpoint.regionCode ?? null,
        "Global",
      ),
    )}
    ${field("Cluster", input("cluster", endpoint.cluster, 'required maxlength="40" placeholder="gru"'))}
    ${field("Provider", select("provider", PROVIDERS, endpoint.provider ?? "other"))}
    ${field(
      "Kind",
      select(
        "kind",
        [
          ["http", "HTTP check from every vantage"],
          ["canary", "Canary run by the coordinator"],
        ],
        endpoint.kind ?? "http",
      ),
    )}
    ${field("Canary probe", select("probe", PROBES, endpoint.probe ?? null, "Data API canary"))}
    ${field("URL", input("url", endpoint.url, 'required maxlength="500" placeholder="https://api.terrascale.tech/readyz"'), true)}
    ${field("Headers (JSON)", input("headers", endpoint.headers ?? "{}", 'placeholder=\'{"Cache-Control":"no-cache"}\''), true)}
    ${field("Expected status", input("expectStatus", endpoint.expectStatus ?? "2xx", 'required maxlength="40"'))}
    ${field("Blocked status", input("blockedStatus", endpoint.blockedStatus, 'inputmode="numeric" placeholder="none"'))}
    ${field("Position", input("position", endpoint.position ?? 0, 'inputmode="numeric"'))}
    ${field("Keyword", input("keyword", endpoint.keyword, 'maxlength="200"'))}
    ${field("Assertions (JSON)", textarea("assertions", endpoint.assertions ?? "[]"), true)}
    ${field("Extract (JSON)", input("extract", endpoint.extract ?? "{}", 'placeholder=\'{"version":"version"}\''), true)}
    <label class="st-choice ad-wide"><input type="checkbox" name="indirect" value="1"${endpoint.indirect === 1 ? raw(" checked") : ""}> Indirect: the URL reports on the component (such as a vendor status page), so failing to reach it is unknown, not down</label>
    <button class="ts-button" type="submit">${submit}</button>
  </form>`;
}

export function catalogPage(frame: Frame, catalog: Catalog): Page {
  const groupNames = catalog.groups.map((item): [string, string] => [item.id, item.name]);
  const componentName = new Map(catalog.components.map((item) => [item.id, item.name]));
  return shell(
    frame,
    "Catalog",
    html`<p class="ts-muted">What the status page measures. New endpoints show "Pending launch" until their first passing check, then count toward uptime.</p>
  ${card(
    {
      id: "components",
      title: "Components",
      description: `Active components are measured; "${COMING_SOON}" ones are shown but not probed.`,
    },
    html`<ul class="ts-list">${catalog.components.map(
      (component) => html`<li class="ts-list-item ad-item" id="component-${component.id}">
      <form class="ad-form" method="post" action="/admin/components/${component.id}/edit">${hidden(frame.csrf)}
        ${field("Name", input("name", component.name, 'required maxlength="80"'))}
        ${field("Group", select("groupId", groupNames, component.groupId))}
        ${field(
          "State",
          select(
            "lifecycle",
            [
              ["active", "Active"],
              ["planned", COMING_SOON],
            ],
            component.lifecycle,
          ),
        )}
        ${field("Position", input("position", component.position, 'inputmode="numeric"'))}
        ${field("Description", input("description", component.description, 'maxlength="300"'), true)}
        <button class="ts-button" type="submit">Save</button>
      </form>
      ${remove(frame, `/admin/components/${component.id}/delete`, `${component.name} and its endpoints`)}
    </li>`,
    )}</ul>
    <h3 class="st-group-title">Add a component</h3>
    <form class="ad-form" method="post" action="/admin/components">${hidden(frame.csrf)}
      ${field("Name", input("name", "", 'required maxlength="80"'))}
      ${field("Group", select("groupId", groupNames, null))}
      ${field(
        "State",
        select(
          "lifecycle",
          [
            ["active", "Active"],
            ["planned", COMING_SOON],
          ],
          "active",
        ),
      )}
      ${field("Position", input("position", 0, 'inputmode="numeric"'))}
      ${field("Description", input("description", "", 'maxlength="300"'), true)}
      <button class="ts-button" type="submit">Add component</button>
    </form>`,
  )}
  ${card(
    {
      id: "endpoints",
      title: "Endpoints",
      description: "Each is checked every 30 seconds. Use a verified public origin for each deployment.",
    },
    html`<ul class="ts-list">${catalog.endpoints.map(
      (endpoint) => html`<li class="ts-list-item ad-item" id="endpoint-${endpoint.id}">
      <details>
        <summary><span class="ts-strong">${componentName.get(endpoint.componentId) ?? endpoint.componentId}</span> · ${endpoint.cluster}${endpoint.regionCode ? ` · ${endpoint.regionCode}` : " · global"} · <code>${endpoint.url}</code> ${pending(endpoint)}</summary>
        ${endpointForm(frame, `/admin/endpoints/${endpoint.id}/edit`, catalog, endpoint, "Save")}
        <form method="post" action="/admin/probes/run">${hidden(frame.csrf)}<input type="hidden" name="endpointId" value="${endpoint.id}"><button class="ts-button ad-quiet" type="submit">Check now</button></form>
        ${remove(frame, `/admin/endpoints/${endpoint.id}/delete`, "this endpoint and its history")}
      </details>
    </li>`,
    )}</ul>
    <h3 class="st-group-title">Add an endpoint</h3>
    ${endpointForm(frame, "/admin/endpoints", catalog, {}, "Add endpoint")}`,
  )}
  ${card(
    { id: "groups", title: "Groups" },
    html`<ul class="ts-list">${catalog.groups.map(
      (group) => html`<li class="ts-list-item ad-item">
      <form class="ad-form" method="post" action="/admin/groups/${group.id}/edit">${hidden(frame.csrf)}
        ${field("Name", input("name", group.name, 'required maxlength="80"'))}
        ${field("Position", input("position", group.position, 'inputmode="numeric"'))}
        <button class="ts-button" type="submit">Save</button>
      </form>
      ${remove(frame, `/admin/groups/${group.id}/delete`, group.name)}
    </li>`,
    )}</ul>
    <form class="ad-form" method="post" action="/admin/groups">${hidden(frame.csrf)}
      ${field("Name", input("name", "", 'required maxlength="80"'))}
      ${field("Position", input("position", 0, 'inputmode="numeric"'))}
      <button class="ts-button" type="submit">Add group</button>
    </form>`,
  )}
  ${card(
    {
      id: "regions",
      title: "Regions",
      description: "Deployment regions (codes such as GRU), placed on the map by x 0–136 and y 0–80.",
    },
    html`<ul class="ts-list">${catalog.regions.map(
      (region) => html`<li class="ts-list-item ad-item">
      <form class="ad-form" method="post" action="/admin/regions/${region.code}/edit">${hidden(frame.csrf)}
        <p class="ts-strong ad-wide">${region.code}</p>
        ${regionFields(region)}
        <button class="ts-button" type="submit">Save</button>
      </form>
      ${remove(frame, `/admin/regions/${region.code}/delete`, region.code)}
    </li>`,
    )}</ul>
    <form class="ad-form" method="post" action="/admin/regions">${hidden(frame.csrf)}
      ${field("Code", input("code", "", 'required maxlength="3" placeholder="GRU"'))}
      ${regionFields({})}
      <button class="ts-button" type="submit">Add region</button>
    </form>`,
  )}
  ${card(
    {
      id: "deploys",
      title: "Record a deploy",
      description:
        "A marker on the endpoint's latency charts. Endpoints reporting a version record deploys on their own.",
    },
    html`<form class="ad-form" method="post" action="/admin/deploys">${hidden(frame.csrf)}
      ${field(
        "Endpoint",
        select(
          "endpointId",
          catalog.endpoints.map((item): [string, string] => [
            item.id,
            `${componentName.get(item.componentId) ?? ""} · ${item.cluster}`,
          ]),
          null,
        ),
      )}
      ${field("Version", input("version", "", 'required maxlength="100"'))}
      <button class="ts-button" type="submit">Record</button>
    </form>`,
  )}`,
  );
}

function regionFields(region: Partial<AdminRegion>) {
  return html`${field("Name", input("name", region.name, 'required maxlength="80"'))}
    ${field("Country", input("country", region.country, 'required maxlength="80"'))}
    ${field("Map x", input("mapX", region.mapX, 'required inputmode="decimal"'))}
    ${field("Map y", input("mapY", region.mapY, 'required inputmode="decimal"'))}
    ${field("Position", input("position", region.position ?? 0, 'inputmode="numeric"'))}
    ${field("Browser ping URL", input("pingUrl", region.pingUrl, 'maxlength="500" placeholder="https://api.terrascale.tech/healthz"'), true)}
    ${field("Ping label", input("pingLabel", region.pingLabel, 'maxlength="120"'), true)}`;
}

export function subscribersPage(frame: Frame, subscribers: Subscriber[], components: Component[]): Page {
  const names = new Map(components.map((item) => [item.id, item.name]));
  const scope = (list: string) => {
    const chosen = (JSON.parse(list) as string[]).map((id) => names.get(id) ?? id);
    return chosen.length === 0 ? "Everything" : chosen.join(", ");
  };
  return shell(
    frame,
    "Subscribers",
    card(
      {
        id: "subscribers",
        title: `${subscribers.length} subscribers`,
        description: `${subscribers.filter((item) => item.confirmedAt).length} confirmed. Messages queued and failing are counted per subscriber.`,
      },
      subscribers.length === 0
        ? empty("No subscribers", "People subscribe on /subscribe.")
        : html`<div class="st-table-wrap"><table class="st-ping">
    <thead><tr><th scope="col">Target</th><th scope="col">Channel</th><th scope="col">Components</th><th scope="col">Confirmed</th><th scope="col">Queued</th><th scope="col">Failing</th><th scope="col"></th></tr></thead>
    <tbody>${subscribers.map(
      (item) =>
        html`<tr><th scope="row">${item.target}</th><td>${item.flavor}</td><td>${scope(item.components)}</td><td>${item.confirmedAt ? time(item.confirmedAt, "datetime") : "Not yet"}</td><td class="ts-num">${item.pending}</td><td class="ts-num">${item.failed}</td><td>${remove(frame, `/admin/subscribers/${item.id}/delete`, "")}</td></tr>`,
    )}</tbody>
  </table></div>`,
    ),
  );
}

const outcome = (result: ProbeRow["results"][number] | undefined) => {
  if (!result) return html`<td class="ts-meta">—</td>`;
  const state = result.ok === 1 ? "operational" : result.ok === 0 ? "major_outage" : "none";
  const text = result.ok === 1 ? `${result.ms ?? "—"} ms` : (result.e ?? "Unknown");
  return html`<td>${statusBadge(state, text)}</td>`;
};

export function probesPage(frame: Frame, rows: ProbeRow[], ranNow: boolean): Page {
  const columns = [
    ...VANTAGES.map((vantage) => ({ code: vantage.code, name: vantage.name })),
    { code: CANARY_VANTAGE, name: "Coordinator" },
  ];
  return shell(
    frame,
    "Probes",
    html`${card(
      {
        id: "probes",
        title: ranNow ? "Checked just now" : "Latest round",
        description: ranNow
          ? "Every vantage checked these endpoints just now. Nothing was stored."
          : "What the latest stored round saw. Canaries run in the coordinator only.",
        actions: html`<form method="post" action="/admin/probes/run">${hidden(frame.csrf)}<button class="ts-button" type="submit">Check every endpoint now</button></form>`,
      },
      rows.length === 0
        ? empty("Nothing to show", "No endpoint has been checked yet.")
        : html`<div class="st-table-wrap"><table class="st-ping">
    <thead><tr><th scope="col">Endpoint</th><th scope="col">State</th>${columns.map((column) => html`<th scope="col">${column.name}</th>`)}</tr></thead>
    <tbody>${rows.map(
      (row) =>
        html`<tr><th scope="row">${row.componentName} · ${row.endpoint.cluster}<span class="ts-meta"> ${row.at ? time(row.at, "seconds") : ""}</span></th><td>${row.endpoint.liveSince ? statusBadge(row.state) : statusBadge("none", "Pending launch")}</td>${columns.map((column) => outcome(row.results.find((result) => result.v === column.code)))}</tr>`,
    )}</tbody>
  </table></div>`,
    )}`,
  );
}
