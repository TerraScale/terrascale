/* The admin page under /admin: sign-in, then every operator action through
   forms. GETs without a session go to the sign-in page; every POST must come
   from this origin, and every protected POST must carry the session's CSRF
   token. Sign-in attempts are rate limited per client. Nothing here is
   cached. */

import { incidents, INCIDENT_COLUMNS, iso } from "../db/reads";
import { parseSpec } from "../domain/assertions";
import type { Component, IncidentUpdate } from "../domain/status";
import type { Env } from "../env";
import { isId } from "../ids";
import { logEvent } from "../log";
import { clientKey } from "../notify/subscribe";
import { checkNow, type RoundEndpoint } from "../probe/round";
import { createRouter } from "../router";
import type { Page } from "../views/layout";
import { actions, Conflict, formBody, NotFound } from "./actions";
import { Invalid } from "../api/validate";
import { checkCsrf, clearCookie, csrfToken, readCookie, sameOrigin, session, setCookie, type Session } from "./auth";
import { authorized, begin, callback } from "./oidc";
import {
  catalogPage,
  incidentAdminPage,
  incidentsPage,
  loginPage,
  probesPage,
  problemPage,
  subscribersPage,
  type AdminEndpoint,
  type Catalog,
  type Frame,
  type ProbeRow,
  type Subscriber,
} from "./views";

/** A page to render, or a finished response. */
export type AdminAnswer = { page: Page; status: number; headers?: Record<string, string> } | Response;

const redirect = (location: string, headers: Record<string, string> = {}) =>
  new Response(null, { status: 303, headers: { location, ...headers } });

const COMPONENTS =
  "SELECT id, group_id AS groupId, name, description, lifecycle, position FROM components ORDER BY position, id";

const ENDPOINTS = `SELECT id, component_id AS componentId, region_code AS regionCode, cluster, provider, url, headers,
    position, expect_status AS expectStatus, keyword, assertions, extract, indirect, kind, probe,
    blocked_status AS blockedStatus, ${iso("live_since")} AS liveSince
  FROM endpoints ORDER BY component_id, position, id`;

async function loadCatalog(env: Env): Promise<Catalog> {
  const db = env.STATUS_DB;
  const [groups, components, endpoints, regions] = await db.batch([
    db.prepare("SELECT id, name, position FROM component_groups ORDER BY position, id"),
    db.prepare(COMPONENTS),
    db.prepare(ENDPOINTS),
    db.prepare(
      `SELECT code, name, country, map_x AS mapX, map_y AS mapY, position, ping_url AS pingUrl, ping_label AS pingLabel
        FROM regions ORDER BY position, code`,
    ),
  ]);
  return {
    groups: (groups?.results ?? []) as Catalog["groups"],
    components: (components?.results ?? []) as Catalog["components"],
    endpoints: (endpoints?.results ?? []) as AdminEndpoint[],
    regions: (regions?.results ?? []) as Catalog["regions"],
  };
}

const components = async (env: Env) =>
  ((await env.STATUS_DB.prepare(COMPONENTS).all<Component>()).results ?? []) as Component[];

type View = (env: Env, frame: Frame, params: Record<string, string>) => Promise<AdminAnswer>;

const views: Record<string, View> = {
  "/admin": async (env, frame) => {
    const db = env.STATUS_DB;
    const [open, closed, catalog] = await db.batch([
      db.prepare(
        `SELECT ${INCIDENT_COLUMNS} FROM incidents WHERE status NOT IN ('resolved', 'completed') ORDER BY created_at DESC`,
      ),
      db.prepare(
        `SELECT ${INCIDENT_COLUMNS} FROM incidents WHERE status IN ('resolved', 'completed') ORDER BY updated_at DESC LIMIT 20`,
      ),
      db.prepare(COMPONENTS),
    ]);
    return {
      page: incidentsPage(frame, incidents(open), incidents(closed), (catalog?.results ?? []) as Component[]),
      status: 200,
    };
  },
  "/admin/incidents/:id": async (env, frame, params) => {
    const db = env.STATUS_DB;
    const [found, updates] = await db.batch([
      db.prepare(`SELECT ${INCIDENT_COLUMNS} FROM incidents WHERE id = ?1`).bind(params.id),
      db
        .prepare(
          `SELECT id, incident_id AS incidentId, status, body, ${iso("created_at")} AS createdAt
            FROM incident_updates WHERE incident_id = ?1 ORDER BY created_at DESC, rowid DESC`,
        )
        .bind(params.id),
    ]);
    const incident = incidents(found)[0];
    if (!incident) throw new NotFound();
    return {
      page: incidentAdminPage(frame, incident, (updates?.results ?? []) as IncidentUpdate[], await components(env)),
      status: 200,
    };
  },
  "/admin/catalog": async (env, frame) => ({ page: catalogPage(frame, await loadCatalog(env)), status: 200 }),
  "/admin/subscribers": async (env, frame) => {
    const { results } = await env.STATUS_DB.prepare(
      `SELECT s.id, s.channel, s.target, s.flavor, s.components, ${iso("s.confirmed_at")} AS confirmedAt,
          ${iso("s.created_at")} AS createdAt,
          (SELECT count(*) FROM outbox o WHERE o.subscriber_id = s.id AND o.sent_at IS NULL) AS pending,
          (SELECT count(*) FROM outbox o WHERE o.subscriber_id = s.id AND o.sent_at IS NULL AND o.last_error IS NOT NULL) AS failed
        FROM subscribers s ORDER BY s.created_at DESC LIMIT 1000`,
    ).all<Subscriber>();
    return { page: subscribersPage(frame, results ?? [], await components(env)), status: 200 };
  },
  "/admin/probes": async (env, frame) => {
    const catalog = await loadCatalog(env);
    const { results } = await env.STATUS_DB.prepare(
      `SELECT s.endpoint_id AS endpointId, ${iso("s.at")} AS at, s.state, s.results FROM endpoints e
        JOIN probe_samples s ON s.endpoint_id = e.id AND s.at = (SELECT max(at) FROM probe_samples WHERE endpoint_id = e.id)`,
    ).all<{ endpointId: string; at: string; state: ProbeRow["state"]; results: string }>();
    const latest = new Map((results ?? []).map((row) => [row.endpointId, row]));
    const names = new Map(catalog.components.map((item) => [item.id, item.name]));
    const rows = catalog.endpoints.map((endpoint): ProbeRow => {
      const sample = latest.get(endpoint.id);
      return {
        endpoint,
        componentName: names.get(endpoint.componentId) ?? "",
        at: sample?.at ?? null,
        state: sample?.state ?? "none",
        results: sample ? (JSON.parse(sample.results) as ProbeRow["results"]) : [],
      };
    });
    return { page: probesPage(frame, rows, false), status: 200 };
  },
};

/** Checks HTTP endpoints from every vantage now: one, or all of them. */
async function runChecks(env: Env, frame: Frame, endpointId: string | null): Promise<AdminAnswer> {
  const catalog = await loadCatalog(env);
  const chosen = catalog.endpoints.filter(
    (endpoint) => endpoint.kind === "http" && (endpointId === null || endpoint.id === endpointId),
  );
  if (endpointId !== null && chosen.length === 0) throw new Invalid("endpointId", "must name an HTTP endpoint");
  const samples = await checkNow(
    env,
    chosen.map((endpoint): RoundEndpoint => ({ ...endpoint, spec: parseSpec(endpoint) })),
  );
  const names = new Map(catalog.components.map((item) => [item.id, item.name]));
  const rows = chosen.map((endpoint, index): ProbeRow => {
    const sample = samples[index];
    return {
      endpoint,
      componentName: names.get(endpoint.componentId) ?? "",
      at: new Date().toISOString(),
      state: (sample?.state ?? "none") as ProbeRow["state"],
      results: sample?.results ?? [],
    };
  });
  return { page: probesPage(frame, rows, true), status: 200 };
}

const viewRoute = createRouter(views);
const actionRoute = createRouter(actions);

const NO_INDEX = { "x-robots-tag": "noindex, nofollow" };

async function signIn(request: Request, env: Env, now: number): Promise<AdminAnswer> {
  const limited = !(await env.ADMIN_LOGIN_LIMITER.limit({ key: `admin:${clientKey(request)}` })).success;
  if (limited) return { page: loginPage("Too many attempts. Wait a minute and try again."), status: 429 };
  return begin(env, now);
}

/** Answers a request under /admin. */
export async function admin(request: Request, env: Env, now: number): Promise<AdminAnswer> {
  const url = new URL(request.url);
  const method = request.method;
  if (method !== "GET" && method !== "POST") {
    return new Response("Method not allowed", { status: 405, headers: { allow: "GET, POST" } });
  }
  if (method === "POST" && !sameOrigin(request)) {
    return new Response("Cross-site request refused.", { status: 403 });
  }
  const secret = env.ADMIN_SESSION_SECRET;
  const current = await session(secret, readCookie(request), now);
  try {
    if (["/admin/login", "/admin/authorize", "/admin/callback"].includes(url.pathname)) {
      if (method !== "GET") return new Response("Method not allowed", { status: 405, headers: { allow: "GET" } });
      if (url.pathname === "/admin/login") return { page: loginPage(), status: 200, headers: NO_INDEX };
      return url.pathname === "/admin/authorize" ? await signIn(request, env, now) : await callback(request, env, now);
    }
    if (!secret || !current) {
      return method === "GET" ? redirect("/admin/authorize") : new Response("Sign in first.", { status: 401 });
    }
    if (!(await authorized(current.accessToken)))
      return new Response("TerraScale owner or admin membership is required.", { status: 403 });
  } catch {
    return {
      page: loginPage("Authorization could not be verified. Start sign-in again."),
      status: 503,
      headers: { ...NO_INDEX, "referrer-policy": "no-referrer" },
    };
  }
  if (!secret || !current) return new Response("Sign in first.", { status: 401 });
  const csrf = await csrfToken(secret, current);

  try {
    if (method === "GET") {
      const match = viewRoute(url.pathname);
      if (!match) return new Response("Not found", { status: 404 });
      if (match.params.id !== undefined && !isId(match.params.id)) throw new NotFound();
      const path = url.pathname.startsWith("/admin/incidents/") ? "/admin" : (url.pathname as Frame["path"]);
      const done = url.searchParams.get("done") ?? undefined;
      const answer = await match.value(env, { csrf, path, done }, match.params);
      return answer instanceof Response ? answer : { ...answer, headers: NO_INDEX };
    }
    return await post(request, env, secret, current, csrf, now);
  } catch (error) {
    const page = (status: number, message: string) => ({
      page: problemPage({ csrf, path: "/admin" }, message),
      status,
      headers: NO_INDEX,
    });
    if (error instanceof Invalid) return page(400, error.message);
    if (error instanceof NotFound) return page(404, "No such record.");
    if (error instanceof Conflict) return page(409, error.message);
    if (error instanceof Error && /FOREIGN KEY|CHECK constraint|UNIQUE constraint/.test(error.message)) {
      return page(400, "A referenced record does not exist, a value is out of range, or the record already exists.");
    }
    logEvent("error", "admin request failed", { component: "admin", request, error });
    return page(500, "The request failed.");
  }
}

async function post(
  request: Request,
  env: Env,
  secret: string,
  current: Session,
  csrf: string,
  now: number,
): Promise<AdminAnswer> {
  const url = new URL(request.url);
  const form = new URLSearchParams((await request.text()).slice(0, 64_000));
  if (!(await checkCsrf(secret, current, form.get("csrf")))) {
    return new Response("The form expired. Reload the page and try again.", { status: 403 });
  }
  if (url.pathname === "/admin/logout") return redirect("/admin/login", { "set-cookie": clearCookie });
  if (url.pathname === "/admin/probes/run") {
    const endpointId = form.get("endpointId");
    if (endpointId !== null && !isId(endpointId)) throw new Invalid("endpointId", "must be a canonical ULID");
    const answer = await runChecks(env, { csrf, path: "/admin/probes" }, endpointId);
    return answer instanceof Response ? answer : { ...answer, headers: NO_INDEX };
  }
  const match = actionRoute(url.pathname);
  if (!match) return new Response("Not found", { status: 404 });
  if (match.params.id !== undefined && !isId(match.params.id)) throw new Invalid("id", "must be a canonical ULID");
  if (match.params.code !== undefined && !/^[A-Z]{3}$/.test(match.params.code)) throw new NotFound();
  if (url.pathname.endsWith("/delete") && form.get("confirm") !== "yes") {
    throw new Invalid("confirm", "tick the box to confirm the deletion");
  }
  return redirect(await match.value({ env, body: formBody(form), params: match.params, now }));
}
