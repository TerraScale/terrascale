/* What the admin page can change: incidents, warnings and maintenance
   windows with their updates, deploy markers, and the catalog (groups,
   components, endpoints, regions) and subscribers. Every action reads a
   posted form, validates it, writes in one batch and answers with the page
   to return to. Editing an automatic incident marks it operator-touched, so
   the probe coordinator leaves it alone. */

import { sqlTime } from "../db/reads";
import { isAssertion, parsePath } from "../domain/assertions";
import type { IncidentImpact, IncidentKind, IncidentStatus } from "../domain/status";
import type { Env } from "../env";
import { isId, newId } from "../ids";
import { choice, ids, instant, Invalid, optionalChoice, optionalString, string, type Body } from "../api/validate";

export const KINDS: readonly IncidentKind[] = ["incident", "warning", "maintenance"];
export const IMPACTS: readonly IncidentImpact[] = ["none", "minor", "major", "critical"];
export const INCIDENT_STATUSES: readonly IncidentStatus[] = ["investigating", "identified", "monitoring", "resolved"];
export const MAINTENANCE_STATUSES: readonly IncidentStatus[] = ["scheduled", "in_progress", "completed"];
export const PROVIDERS = ["cloudflare", "other"] as const;
export const PROBES = ["health", "documents", "management"] as const;
const FINAL: readonly IncidentStatus[] = ["resolved", "completed"];

export const statusesOf = (kind: IncidentKind) => (kind === "maintenance" ? MAINTENANCE_STATUSES : INCIDENT_STATUSES);

export class NotFound extends Error {}

/** The request clashes with the current state of the record. */
export class Conflict extends Error {}

/** A posted form as a body: empty fields are left out, `components` is a list. */
export function formBody(form: URLSearchParams): Body {
  const body: Body = {};
  for (const [name, value] of form) {
    if (name === "components") continue;
    if (value.trim() !== "") body[name] = value;
  }
  if (form.has("components")) body.components = form.getAll("components");
  return body;
}

export interface Action {
  env: Env;
  body: Body;
  params: Record<string, string>;
  now: number;
}

/** Where to go after an action, with what it did. */
const back = (path: string, done: string) => `${path}${path.includes("?") ? "&" : "?"}done=${done}`;

interface Stored {
  kind: IncidentKind;
  scheduledStart: string | null;
  scheduledEnd: string | null;
}

async function stored(env: Env, id: string): Promise<Stored> {
  const row = await env.STATUS_DB.prepare(
    `SELECT kind, strftime('%Y-%m-%dT%H:%M:%SZ', scheduled_start) AS scheduledStart,
      strftime('%Y-%m-%dT%H:%M:%SZ', scheduled_end) AS scheduledEnd FROM incidents WHERE id = ?1`,
  )
    .bind(id)
    .first<Stored>();
  if (!row) throw new NotFound();
  return row;
}

// An open automatic incident for the component that nothing would measure
// once the change applies, so the coordinator could never close it.
const OPEN_AUTO = `SELECT 1 FROM incidents WHERE source = 'auto' AND auto_key = ?1
  AND status NOT IN ('resolved', 'completed')`;
const LAST_ENDPOINT_OPEN_AUTO = `SELECT 1 FROM incidents i JOIN endpoints e ON e.id = ?1 AND i.auto_key = e.component_id
  WHERE i.source = 'auto' AND i.status NOT IN ('resolved', 'completed')
    AND NOT EXISTS (SELECT 1 FROM endpoints o WHERE o.component_id = e.component_id AND o.id != ?1)`;

async function refuseStranding(env: Env, sql: string, id: string): Promise<void> {
  if (await env.STATUS_DB.prepare(sql).bind(id).first()) {
    throw new Conflict("An automatic incident is open for this component; resolve it first.");
  }
}

const componentStatements = (env: Env, id: string, components: readonly string[]) => [
  env.STATUS_DB.prepare("DELETE FROM incident_components WHERE incident_id = ?1").bind(id),
  ...components.map((component) =>
    env.STATUS_DB.prepare("INSERT INTO incident_components (incident_id, component_id) VALUES (?1, ?2)").bind(
      id,
      component,
    ),
  ),
];

const postUpdate = (env: Env, id: string, status: IncidentStatus, body: string, time: string) =>
  env.STATUS_DB.prepare(
    "INSERT INTO incident_updates (id, incident_id, status, body, created_at) VALUES (?1, ?2, ?3, ?4, ?5)",
  ).bind(newId(), id, status, body, time);

/** A datetime-local input ("2026-10-01T02:00") is UTC on this page. */
function utc(body: Body, field: string): void {
  const value = body[field];
  if (typeof value === "string" && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/.test(value)) body[field] = `${value}Z`;
}

const SCHEDULE_FIELDS = ["scheduledStart", "scheduledEnd"] as const;

const hasSchedule = (body: Body): boolean => SCHEDULE_FIELDS.some((field) => body[field] !== undefined);

/** The scheduled window of maintenance, checked for order. A field left out
    keeps its stored value, when there is one. */
function schedule(body: Body, current?: Stored): { start: string; end: string } {
  SCHEDULE_FIELDS.forEach((field) => utc(body, field));
  const at = (field: (typeof SCHEDULE_FIELDS)[number], fallback: string | null | undefined) =>
    body[field] === undefined && fallback ? Date.parse(fallback) : instant(body, field);
  const start = at("scheduledStart", current?.scheduledStart);
  const end = at("scheduledEnd", current?.scheduledEnd);
  if (end <= start) throw new Invalid("scheduledEnd", "must be after scheduledStart");
  return { start: sqlTime(start), end: sqlTime(end) };
}

function onlyMaintenance(kind: IncidentKind, body: Body): void {
  if (kind !== "maintenance" && hasSchedule(body)) throw new Invalid("scheduledStart", "applies to maintenance only");
}

function integer(body: Body, field: string, min: number, max: number): number | undefined {
  if (body[field] === undefined) return undefined;
  const value = Number(body[field]);
  if (!Number.isInteger(value) || value < min || value > max) throw new Invalid(field, `must be ${min} to ${max}`);
  return value;
}

function decimal(body: Body, field: string, max: number): number {
  const value = Number(string(body, field, 20));
  if (!Number.isFinite(value) || value < 0 || value > max) throw new Invalid(field, `must be 0 to ${max}`);
  return value;
}

function id(body: Body, field: string): string {
  const value = string(body, field, 26);
  if (!isId(value)) throw new Invalid(field, "must be a canonical ULID");
  return value;
}

function json(body: Body, field: string, fallback: string, valid: (value: unknown) => boolean, rule: string): string {
  const text = optionalString(body, field, 8_000) ?? fallback;
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    throw new Invalid(field, "must be JSON");
  }
  if (!valid(value)) throw new Invalid(field, rule);
  return JSON.stringify(value);
}

const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);

const regionCode = (body: Body, field = "code"): string => {
  const value = string(body, field, 3).toUpperCase();
  if (!/^[A-Z]{3}$/.test(value)) throw new Invalid(field, "must be a three-letter region code such as GRU");
  return value;
};

function httpsUrl(body: Body, field: string): string | null {
  const value = optionalString(body, field, 500);
  if (value === undefined) return null;
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Invalid(field, "must be an https:// URL");
  }
  if (url.protocol !== "https:") throw new Invalid(field, "must be an https:// URL");
  return value;
}

/** An endpoint's columns from a form, in the order the statements bind them. */
function endpointFields(body: Body) {
  const kind = choice(body, "kind", ["http", "canary"] as const);
  const url = string(body, "url", 500);
  if (kind === "http") httpsUrl(body, "url");
  const expect = string(body, "expectStatus", 40);
  if (!expect.split(",").every((part) => /^([1-5]xx|[1-5]\d\d)$/.test(part.trim().toLowerCase()))) {
    throw new Invalid("expectStatus", 'must be "2xx", a status such as "200", or a list such as "200,204"');
  }
  return [
    id(body, "componentId"),
    body.regionCode === undefined ? null : regionCode(body, "regionCode"),
    string(body, "cluster", 40),
    choice(body, "provider", PROVIDERS),
    url,
    json(
      body,
      "headers",
      "{}",
      (value) => isObject(value) && Object.values(value).every((entry) => typeof entry === "string"),
      "must be a JSON object of header names to strings",
    ),
    integer(body, "position", 0, 1_000) ?? 0,
    expect.toLowerCase(),
    optionalString(body, "keyword", 200) ?? null,
    json(
      body,
      "assertions",
      "[]",
      (value) => Array.isArray(value) && value.every(isAssertion),
      'must be a JSON list such as [{"path":"status","op":"eq","value":"ready"}]',
    ),
    json(
      body,
      "extract",
      "{}",
      (value) =>
        isObject(value) && Object.values(value).every((path) => typeof path === "string" && parsePath(path) !== null),
      'must be a JSON object of names to paths such as {"version":"version"}',
    ),
    body.indirect === "1" ? 1 : 0,
    kind,
    kind === "canary" ? (optionalChoice(body, "probe", PROBES) ?? null) : null,
    integer(body, "blockedStatus", 100, 599) ?? null,
  ] as const;
}

type Handler = (action: Action) => Promise<string>;

export const actions: Record<string, Handler> = {
  "/admin/incidents": async ({ env, body, now }) => {
    const kind = choice(body, "kind", KINDS);
    const title = string(body, "title", 200);
    const message = string(body, "message", 4_000);
    const impact = optionalChoice(body, "impact", IMPACTS) ?? (kind === "maintenance" ? "none" : "minor");
    const status =
      optionalChoice(body, "status", statusesOf(kind)) ??
      (kind === "maintenance" ? "scheduled" : kind === "warning" ? "monitoring" : "investigating");
    const components = ids(body, "components");
    onlyMaintenance(kind, body);
    const window = kind === "maintenance" ? schedule(body) : null;
    const id = newId();
    const time = sqlTime(now);
    await env.STATUS_DB.batch([
      env.STATUS_DB.prepare(
        `INSERT INTO incidents (id, kind, title, status, impact, scheduled_start, scheduled_end, created_at, updated_at, resolved_at)
          VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?8, ?9)`,
      ).bind(
        id,
        kind,
        title,
        status,
        impact,
        window?.start ?? null,
        window?.end ?? null,
        time,
        FINAL.includes(status) ? time : null,
      ),
      ...componentStatements(env, id, components),
      postUpdate(env, id, status, message, time),
    ]);
    return back(`/admin/incidents/${id}`, "created");
  },

  "/admin/incidents/:id/edit": async ({ env, params, body, now }) => {
    const id = params.id ?? "";
    const current = await stored(env, id);
    const title = optionalString(body, "title", 200);
    const impact = optionalChoice(body, "impact", IMPACTS);
    onlyMaintenance(current.kind, body);
    const window = hasSchedule(body) ? schedule(body, current) : null;
    await env.STATUS_DB.batch([
      env.STATUS_DB.prepare(
        `UPDATE incidents SET title = coalesce(?2, title), impact = coalesce(?3, impact),
          scheduled_start = coalesce(?4, scheduled_start), scheduled_end = coalesce(?5, scheduled_end),
          operator_touched = 1, updated_at = ?6 WHERE id = ?1`,
      ).bind(id, title ?? null, impact ?? null, window?.start ?? null, window?.end ?? null, sqlTime(now)),
      // The form always lists the components; none checked means the whole platform.
      ...componentStatements(env, id, ids(body, "components")),
    ]);
    return back(`/admin/incidents/${id}`, "saved");
  },

  "/admin/incidents/:id/updates": async ({ env, params, body, now }) => {
    const id = params.id ?? "";
    const { kind } = await stored(env, id);
    const status = choice(body, "status", statusesOf(kind));
    const message = string(body, "message", 4_000);
    const impact = optionalChoice(body, "impact", IMPACTS);
    const time = sqlTime(now);
    const final = FINAL.includes(status);
    await env.STATUS_DB.batch([
      env.STATUS_DB.prepare(
        `UPDATE incidents SET status = ?2, impact = coalesce(?3, impact), operator_touched = 1, updated_at = ?4,
          resolved_at = CASE WHEN ?5 THEN coalesce(resolved_at, ?4) END,
          auto_key = CASE WHEN ?5 THEN NULL ELSE auto_key END
          WHERE id = ?1`,
      ).bind(id, status, impact ?? null, time, final ? 1 : 0),
      postUpdate(env, id, status, message, time),
    ]);
    return back(`/admin/incidents/${id}`, "posted");
  },

  "/admin/incidents/:id/delete": async ({ env, params }) => {
    await stored(env, params.id ?? "");
    await env.STATUS_DB.prepare("DELETE FROM incidents WHERE id = ?1").bind(params.id).run();
    return back("/admin", "deleted");
  },

  "/admin/deploys": async ({ env, body, now }) => {
    const endpoint = id(body, "endpointId");
    const version = string(body, "version", 100);
    const result = await env.STATUS_DB.prepare(
      "INSERT OR IGNORE INTO deploys (endpoint_id, version, detected_at) VALUES (?1, ?2, ?3)",
    )
      .bind(endpoint, version, sqlTime(now))
      .run();
    // Deploys are keyed by endpoint and second: one was already recorded.
    if ((result.meta.changes ?? 0) === 0) throw new Conflict("A deploy was already recorded for this endpoint now.");
    return back("/admin/catalog#deploys", "recorded");
  },

  "/admin/groups": async ({ env, body }) => {
    await env.STATUS_DB.prepare("INSERT INTO component_groups (id, name, position) VALUES (?1, ?2, ?3)")
      .bind(newId(), string(body, "name", 80), integer(body, "position", 0, 1_000) ?? 0)
      .run();
    return back("/admin/catalog#groups", "created");
  },

  "/admin/groups/:id/edit": async ({ env, params, body }) => {
    const result = await env.STATUS_DB.prepare("UPDATE component_groups SET name = ?2, position = ?3 WHERE id = ?1")
      .bind(params.id, string(body, "name", 80), integer(body, "position", 0, 1_000) ?? 0)
      .run();
    if ((result.meta.changes ?? 0) === 0) throw new NotFound();
    return back("/admin/catalog#groups", "saved");
  },

  "/admin/groups/:id/delete": async ({ env, params }) => {
    if (await env.STATUS_DB.prepare("SELECT 1 FROM components WHERE group_id = ?1").bind(params.id).first()) {
      throw new Conflict("The group still has components; move or delete them first.");
    }
    const result = await env.STATUS_DB.prepare("DELETE FROM component_groups WHERE id = ?1").bind(params.id).run();
    if ((result.meta.changes ?? 0) === 0) throw new NotFound();
    return back("/admin/catalog#groups", "deleted");
  },

  "/admin/components": async ({ env, body }) => {
    await env.STATUS_DB.prepare(
      `INSERT INTO components (id, group_id, name, description, lifecycle, position) VALUES (?1, ?2, ?3, ?4, ?5, ?6)`,
    )
      .bind(
        newId(),
        id(body, "groupId"),
        string(body, "name", 80),
        optionalString(body, "description", 300) ?? "",
        choice(body, "lifecycle", ["active", "planned"] as const),
        integer(body, "position", 0, 1_000) ?? 0,
      )
      .run();
    return back("/admin/catalog#components", "created");
  },

  "/admin/components/:id/edit": async ({ env, params, body }) => {
    const lifecycle = choice(body, "lifecycle", ["active", "planned"] as const);
    // Planning a component stops probing it.
    if (lifecycle === "planned") await refuseStranding(env, OPEN_AUTO, params.id ?? "");
    const result = await env.STATUS_DB.prepare(
      `UPDATE components SET group_id = ?2, name = ?3, description = ?4, lifecycle = ?5, position = ?6 WHERE id = ?1`,
    )
      .bind(
        params.id,
        id(body, "groupId"),
        string(body, "name", 80),
        optionalString(body, "description", 300) ?? "",
        lifecycle,
        integer(body, "position", 0, 1_000) ?? 0,
      )
      .run();
    if ((result.meta.changes ?? 0) === 0) throw new NotFound();
    return back("/admin/catalog#components", "saved");
  },

  "/admin/components/:id/delete": async ({ env, params }) => {
    const component = params.id ?? "";
    await refuseStranding(env, OPEN_AUTO, component);
    // Endpoints go first, their samples and rollups with them.
    const [, result] = await env.STATUS_DB.batch([
      env.STATUS_DB.prepare("DELETE FROM endpoints WHERE component_id = ?1").bind(component),
      env.STATUS_DB.prepare("DELETE FROM components WHERE id = ?1").bind(component),
    ]);
    if ((result?.meta.changes ?? 0) === 0) throw new NotFound();
    return back("/admin/catalog#components", "deleted");
  },

  "/admin/endpoints": async ({ env, body }) => {
    await env.STATUS_DB.prepare(
      `INSERT INTO endpoints (id, component_id, region_code, cluster, provider, url, headers, position, expect_status,
          keyword, assertions, extract, indirect, kind, probe, blocked_status)
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16)`,
    )
      .bind(newId(), ...endpointFields(body))
      .run();
    return back("/admin/catalog#endpoints", "created");
  },

  "/admin/endpoints/:id/edit": async ({ env, params, body }) => {
    const result = await env.STATUS_DB.prepare(
      `UPDATE endpoints SET component_id = ?2, region_code = ?3, cluster = ?4, provider = ?5, url = ?6, headers = ?7,
          position = ?8, expect_status = ?9, keyword = ?10, assertions = ?11, extract = ?12, indirect = ?13, kind = ?14,
          probe = ?15, blocked_status = ?16
        WHERE id = ?1`,
    )
      .bind(params.id, ...endpointFields(body))
      .run();
    if ((result.meta.changes ?? 0) === 0) throw new NotFound();
    return back(`/admin/catalog#endpoint-${params.id}`, "saved");
  },

  "/admin/endpoints/:id/delete": async ({ env, params }) => {
    await refuseStranding(env, LAST_ENDPOINT_OPEN_AUTO, params.id ?? "");
    const result = await env.STATUS_DB.prepare("DELETE FROM endpoints WHERE id = ?1").bind(params.id).run();
    if ((result.meta.changes ?? 0) === 0) throw new NotFound();
    return back("/admin/catalog#endpoints", "deleted");
  },

  "/admin/regions": async ({ env, body }) => {
    const code = regionCode(body);
    const existing = await env.STATUS_DB.prepare("SELECT 1 FROM regions WHERE code = ?1").bind(code).first();
    if (existing) throw new Conflict(`Region ${code} already exists.`);
    await env.STATUS_DB.prepare(
      `INSERT INTO regions (code, name, country, map_x, map_y, position, ping_url, ping_label)
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)`,
    )
      .bind(code, ...regionFields(body))
      .run();
    return back("/admin/catalog#regions", "created");
  },

  "/admin/regions/:code/edit": async ({ env, params, body }) => {
    const result = await env.STATUS_DB.prepare(
      `UPDATE regions SET name = ?2, country = ?3, map_x = ?4, map_y = ?5, position = ?6, ping_url = ?7, ping_label = ?8
        WHERE code = ?1`,
    )
      .bind(params.code, ...regionFields(body))
      .run();
    if ((result.meta.changes ?? 0) === 0) throw new NotFound();
    return back("/admin/catalog#regions", "saved");
  },

  "/admin/regions/:code/delete": async ({ env, params }) => {
    if (await env.STATUS_DB.prepare("SELECT 1 FROM endpoints WHERE region_code = ?1").bind(params.code).first()) {
      throw new Conflict("Endpoints still run in this region; move or delete them first.");
    }
    const result = await env.STATUS_DB.prepare("DELETE FROM regions WHERE code = ?1").bind(params.code).run();
    if ((result.meta.changes ?? 0) === 0) throw new NotFound();
    return back("/admin/catalog#regions", "deleted");
  },

  "/admin/subscribers/:id/delete": async ({ env, params }) => {
    const result = await env.STATUS_DB.prepare("DELETE FROM subscribers WHERE id = ?1").bind(params.id).run();
    if ((result.meta.changes ?? 0) === 0) throw new NotFound();
    return back("/admin/subscribers", "deleted");
  },
};

function regionFields(body: Body) {
  return [
    string(body, "name", 80),
    string(body, "country", 80),
    // The region map's viewBox (views/charts/geometry).
    decimal(body, "mapX", 136),
    decimal(body, "mapY", 80),
    integer(body, "position", 0, 1_000) ?? 0,
    httpsUrl(body, "pingUrl"),
    optionalString(body, "pingLabel", 120) ?? null,
  ] as const;
}
