/* The status Worker's handlers. Static files are served by Workers assets
   before this runs; the Worker renders the pages and the JSON API from D1
   and keeps each response in the edge cache for a few seconds, so traffic
   spikes during an incident cost at most one D1 batch per URL per colo
   every CACHE_SECONDS. Its minute cron keeps the probe coordinator armed,
   rolls the probe samples up into hours and days, delivers subscription
   messages, and hourly refreshes the TLS certificates of the monitored
   hosts. */

import { bootstrap } from "./db/bootstrap";
import { isId } from "./ids";

import { admin } from "./admin";
import { regionLatencyDocument } from "./api/latency";
import { deliverDue, runOutbox } from "./notify/outbox";
import { subscriptions } from "./notify/subscribe";
import { statuspage, type StatuspageDocument } from "./api/statuspage";
import { incidentsDocument, latencyDocument, regionsDocument, statusDocument } from "./api/v1";
import { loadDays, loadDetails, loadPeriods } from "./db/details";
import { loadHistory, loadIncident, loadMaintenance, loadRecent, loadSnapshot } from "./db/reads";
import { byHour, DEFAULT_RANGE, isRange, RANGE_KEYS, rangeStart, type RangeKey } from "./domain/range";
import { HEADLINES } from "./domain/status";
import type { Env } from "./env";
import { badge } from "./feeds/badge";
import { logEvent } from "./log";
import { atom, calendar, rss } from "./feeds/feeds";
import { DAY, HOUR, startOfHour } from "./format";
import { cacheKey, oneOf, readQuery, text as short, type QuerySpec } from "./http/cache";
import { contentSecurityPolicy, type PolicyOptions } from "./http/csp";
import { secure } from "./http/security";
import { Measurements } from "./measure";
import { overview, snapshotWindow } from "./overview";
import { coordinator } from "./probe/coordinator";
import { deployments, type DeploymentObservation } from "./probe/deployments";
import { rollup } from "./probe/rollup";
import { CHECK_MINUTE, checkCertificates } from "./probe/tls";
import { createRouter } from "./router";
import { componentPage } from "./views/component";
import { embedPage } from "./views/embed";
import { historyPage, historyWindow } from "./views/history";
import { homePage } from "./views/home";
import { incidentPage } from "./views/incident";
import { integrationsPage } from "./views/integrations";
import { compactBarData, document, type Page } from "./views/layout";
import { MAINTENANCE_DAYS, maintenancePage } from "./views/maintenance";
import { notFoundPage } from "./views/not-found";
import { pingPage } from "./views/ping";
import { regionPage } from "./views/region";
import { uptimePage, YEAR_DAYS } from "./views/uptime";

const FONT_PRELOAD = '</fonts/Geist-Variable.woff2>; rel=preload; as=font; type="font/woff2"; crossorigin';
const deploymentRequests = new WeakMap<Env, Promise<DeploymentObservation[]>>();

interface RenderContext {
  now: number;
  /** The site's origin, for absolute links in feeds and APIs. */
  origin: string;
  /** Only the page's <main>, for in-place refreshes. */
  fragment: boolean;
  params: Record<string, string>;
  query: Record<string, string>;
  policy?: PolicyOptions;
}

interface Route {
  render: (env: Env, context: RenderContext) => Promise<Response>;
  /** Query parameters the route reads; every other one is ignored. */
  query?: QuerySpec;
  /** Widens the page's content security policy. */
  policy?: PolicyOptions;
}

/** Deploys and incidents shown on detail pages go back this far. */
const DETAIL_DAYS = 90;

/** Entries in the feeds and the Statuspage-compatible API. */
const FEED_ENTRIES = 50;

const json = (data: unknown, status = 200): Response =>
  Response.json(data, { status, headers: { "access-control-allow-origin": "*" } });

function text(body: string, type: string, headers: Record<string, string> = {}, status = 200): Response {
  return new Response(body, {
    status,
    headers: { "content-type": `${type}; charset=utf-8`, "access-control-allow-origin": "*", ...headers },
  });
}

const rangeQuery: QuerySpec = { range: oneOf(...RANGE_KEYS) };

const rangeOf = (query: Record<string, string>): RangeKey => (isRange(query.range) ? query.range : DEFAULT_RANGE);

async function loadOverview(env: Env, now: number, range: RangeKey) {
  return overview(await loadSnapshot(env, snapshotWindow(now, range)), now, range);
}

/** One Statuspage-compatible document, or a 404 for a name it does not have. */
async function statuspageDocument(env: Env, { now, origin }: RenderContext, name: string | undefined) {
  if (name === undefined || !Object.hasOwn(statuspage, name)) {
    return json({ error: { code: "not_found", message: "No such document." } }, 404);
  }
  const [view, recent] = await Promise.all([loadOverview(env, now, "24h"), loadRecent(env, FEED_ENTRIES)]);
  return json(statuspage[name as StatuspageDocument]({ view, ...recent, origin }));
}

const route = createRouter<Route>({
  "/": {
    query: rangeQuery,
    render: async (env, context) =>
      page(homePage(await loadOverview(env, context.now, rangeOf(context.query))), context),
  },
  "/history": {
    query: {
      component: isId,
      kind: oneOf("incident", "warning"),
      impact: oneOf("minor", "major", "critical"),
      q: short(80),
      month: (value) => /^20\d\d-(0[1-9]|1[0-2])$/.test(value),
    },
    policy: { formAction: "'self'" },
    render: async (env, context) => {
      const { query, now } = context;
      const history = await loadHistory(env, { ...query, ...historyWindow(query, now) });
      return page(historyPage(history, query, now), context);
    },
  },
  "/incidents/:id": {
    render: async (env, context) => {
      const detail = await loadIncident(env, context.params.id ?? "", context.now);
      return detail ? page(incidentPage(detail, context.now), context) : page(notFoundPage(), context, 404);
    },
  },
  "/uptime": {
    render: async (env, context) => {
      const view = await loadOverview(env, context.now, "90d");
      const days = await loadDays(
        env,
        view.endpoints.map((endpoint) => endpoint.id),
        context.now - YEAR_DAYS * DAY,
      );
      return page(uptimePage(view, days, context.now), context);
    },
  },
  "/components/:id": {
    query: rangeQuery,
    render: async (env, context) => {
      const range = rangeOf(context.query);
      const view = await loadOverview(env, context.now, range);
      const entry = view.groups.flatMap((group) => group.components).find((item) => item.id === context.params.id);
      if (!entry) return page(notFoundPage(), context, 404);
      const details = await loadDetails(env, {
        endpointIds: entry.clusters.map((cluster) => cluster.id),
        componentIds: [entry.id],
        from: rangeStart(range, context.now),
        hourly: byHour(range),
        since: context.now - DETAIL_DAYS * DAY,
      });
      return page(componentPage(view, entry, details, context.now), context);
    },
  },
  "/regions/:code": {
    query: rangeQuery,
    render: async (env, context) => {
      const range = rangeOf(context.query);
      const view = await loadOverview(env, context.now, range);
      const entry = view.regions.find((item) => item.code === context.params.code?.toUpperCase());
      if (!entry) return page(notFoundPage(), context, 404);
      const details = await loadDetails(env, {
        endpointIds: entry.clusters.map((cluster) => cluster.id),
        componentIds: [...new Set(entry.clusters.map((cluster) => cluster.componentId))],
        from: rangeStart(range, context.now),
        hourly: byHour(range),
        since: context.now - DETAIL_DAYS * DAY,
      });
      return page(regionPage(view, entry, details, context.now), context);
    },
  },
  "/ping": {
    // The browser test times requests to the regions' own hosts.
    policy: { connect: ["https://*.terrascale.tech"] },
    render: async (env, context) => {
      const view = await loadOverview(env, context.now, "24h");
      const details = await loadDetails(env, {
        endpointIds: view.endpoints.map((endpoint) => endpoint.id),
        componentIds: [],
        from: rangeStart("24h", context.now),
        hourly: true,
        since: context.now,
      });
      return page(pingPage(view, details, context.now), context);
    },
  },
  "/maintenance": {
    render: async (env, context) => {
      const { windows, components } = await loadMaintenance(env, context.now - MAINTENANCE_DAYS * DAY);
      return page(maintenancePage(windows, components, context.now), context);
    },
  },
  "/api/latency": {
    query: { region: (value) => /^[a-z0-9-]{1,40}$/i.test(value) },
    render: async (env, { now, query }) => {
      const view = await loadOverview(env, now, "24h");
      const regions = view.regions.filter(
        (region) => !query.region || region.code.toLowerCase() === query.region.toLowerCase(),
      );
      const periods = await loadPeriods(
        env,
        regions.flatMap((region) => region.clusters.map((cluster) => cluster.id)),
        startOfHour(now) - HOUR,
        true,
      );
      return json(regionLatencyDocument(regions, new Measurements(periods), now, view.monitoring.coverage));
    },
  },
  "/api/v1/status": {
    query: rangeQuery,
    render: async (env, { now, query }) => json(statusDocument(await loadOverview(env, now, rangeOf(query)))),
  },
  "/api/v1/incidents": {
    query: { limit: (value) => /^([1-9]\d?|100)$/.test(value) },
    render: async (env, { query }) =>
      json(incidentsDocument(await loadRecent(env, Number(query.limit ?? FEED_ENTRIES)))),
  },
  "/api/v1/regions": {
    render: async (env, { now }) => json(regionsDocument(await loadOverview(env, now, "24h"))),
  },
  "/api/v1/latency": {
    query: { ...rangeQuery, component: isId },
    render: async (env, { now, query }) => {
      const range: RangeKey = isRange(query.range) ? query.range : "24h";
      const view = await loadOverview(env, now, range);
      const component = view.groups.flatMap((group) => group.components).find((entry) => entry.id === query.component);
      if (!component)
        return json({ error: { code: "not_found", message: "Pass ?component= a measured component id." } }, 404);
      const periods = await loadPeriods(
        env,
        component.clusters.map((cluster) => cluster.id),
        rangeStart(range, now),
        byHour(range),
      );
      return json(latencyDocument(component, new Measurements(periods), range, now));
    },
  },
  "/api/v1/:document.json": {
    render: (env, context) => statuspageDocument(env, context, `${context.params.document}.json`),
  },
  "/api/v1/incidents/unresolved.json": {
    render: (env, context) => statuspageDocument(env, context, "incidents/unresolved.json"),
  },
  "/api/v1/scheduled-maintenances/:which": {
    render: (env, context) => statuspageDocument(env, context, `scheduled-maintenances/${context.params.which}`),
  },
  "/feed.atom": {
    render: async (env, { now, origin }) =>
      text(atom(await loadRecent(env, FEED_ENTRIES), origin, now), "application/atom+xml"),
  },
  "/feed.rss": {
    render: async (env, { now, origin }) =>
      text(rss(await loadRecent(env, FEED_ENTRIES), origin, now), "application/rss+xml"),
  },
  "/maintenance.ics": {
    render: async (env, { origin }) =>
      text(calendar(await loadRecent(env, FEED_ENTRIES), origin), "text/calendar", {
        "content-disposition": 'inline; filename="terrascale-maintenance.ics"',
      }),
  },
  "/badge/:id.svg": {
    render: async (env, { now, params }) => {
      const view = await loadOverview(env, now, "24h");
      if (params.id === "status")
        return text(
          badge("TerraScale", view.status, view.status === "operational" ? "Operational" : HEADLINES[view.status]),
          "image/svg+xml",
        );
      const component = view.groups.flatMap((group) => group.components).find((entry) => entry.id === params.id);
      if (!component) return text(badge("TerraScale", "none", "Unknown component"), "image/svg+xml", {}, 404);
      return text(badge(component.name, component.status, component.note ?? undefined), "image/svg+xml");
    },
  },
  "/embed": {
    policy: { frameAncestors: "*" },
    render: async (env, context) =>
      page(embedPage(await loadOverview(env, context.now, "24h"), context.origin), context),
  },
  "/integrations": {
    policy: { frame: ["'self'"] },
    render: async (env, context) =>
      page(integrationsPage(await loadOverview(env, context.now, "24h"), context.origin), context),
  },
});

async function page(content: Page, context: RenderContext, status = 200): Promise<Response> {
  const body = await compactBarData(
    context.fragment ? content.body.value : document(content, new Date(context.now).getUTCFullYear()).value,
  );
  return new Response(body, {
    status,
    headers: {
      "content-type": "text/html; charset=utf-8",
      "content-security-policy": await contentSecurityPolicy(context.policy),
      link: FONT_PRELOAD,
    },
  });
}

function unavailable(): Response {
  return secure(
    new Response("Status is temporarily unavailable.", { status: 503, headers: { "retry-after": "10" } }),
    false,
  );
}

async function render(route: Route, env: Env, context: RenderContext, request: Request): Promise<Response> {
  try {
    await bootstrap(env);
    return secure(await route.render(env, context), true);
  } catch (error) {
    logEvent("error", "status render failed", { component: "render", request, error });
    return unavailable();
  }
}

export default {
  async fetch(request, env, ctx): Promise<Response> {
    const url = new URL(request.url);
    const now = Date.now();
    if (url.pathname === "/api/v1/deployments") {
      if (request.method !== "GET") {
        return secure(new Response("Method not allowed", { status: 405, headers: { allow: "GET" } }), false);
      }
      const cache = caches.default;
      const key = new Request(cacheKey(url, {}, false));
      let response = await cache.match(key);
      if (!response) {
        let pending = deploymentRequests.get(env);
        if (!pending) {
          pending = deployments(env, now)
            .then(async (observations) => {
              await cache.put(key, secure(Response.json(observations), true)).catch((error: unknown) => {
                logEvent("error", "deployment cache write failed", { component: "deployments", request, error });
              });
              return observations;
            })
            .finally(() => deploymentRequests.delete(env));
          deploymentRequests.set(env, pending);
        }
        response = Response.json(await pending);
      }
      // Keep the observation timestamp; clients must return through the bounded edge cache.
      return secure(response, false);
    }
    if (url.pathname === "/health/version") {
      if (request.method !== "GET") {
        return secure(new Response("Method not allowed", { status: 405, headers: { allow: "GET" } }), false);
      }
      return secure(
        Response.json(
          { version: env.VERSION ?? "unknown", colo: request.cf?.colo ?? null },
          {
            headers: { "cache-control": "no-store", "x-deployment-sample": "20261026-3" },
          },
        ),
        false,
      );
    }
    // The browser ping test's baseline needs no database.
    if (url.pathname === "/api/ping") {
      return secure(
        Response.json({ colo: request.cf?.colo ?? null }, { headers: { "access-control-allow-origin": "*" } }),
        false,
      );
    }
    const adminRequest = url.pathname === "/admin" || url.pathname.startsWith("/admin/");
    const subscriptionRequest = url.pathname === "/subscribe" || url.pathname.startsWith("/subscribe/");
    if (adminRequest || subscriptionRequest) {
      try {
        await bootstrap(env);
      } catch (error) {
        logEvent("error", "status bootstrap failed", { component: "bootstrap", request, error });
        return unavailable();
      }
    }
    if (adminRequest) {
      const answer = await admin(request, env, now);
      if (answer instanceof Response) return secure(answer, false);
      const context = {
        now,
        origin: url.origin,
        fragment: false,
        params: {},
        query: {},
        policy: { formAction: "'self'" },
      };
      const response = await page(answer.page, context, answer.status);
      for (const [name, value] of Object.entries(answer.headers ?? {})) response.headers.set(name, value);
      return secure(response, false);
    }
    if (subscriptionRequest) {
      const answer = await subscriptions(env, request, now);
      if (answer instanceof Response) return secure(answer, false);
      // Send a new confirmation right away instead of on the next cron run.
      if (answer.deliver) ctx.waitUntil(deliverDue(env, now, {}, answer.deliver));
      const context = {
        now,
        origin: url.origin,
        fragment: false,
        params: {},
        query: {},
        policy: { formAction: "'self'" },
      };
      return secure(await page(answer.page, context, answer.status), false);
    }
    const match = route(url.pathname);

    if (
      !match ||
      (match.params.id !== undefined &&
        (match.params.id !== "status" || !url.pathname.startsWith("/badge/")) &&
        !isId(match.params.id))
    ) {
      return secure(
        await page(notFoundPage(), { now, origin: url.origin, fragment: false, params: {}, query: {} }, 404),
        false,
      );
    }
    if (request.method !== "GET" && request.method !== "HEAD") {
      return secure(new Response("Method not allowed", { status: 405, headers: { allow: "GET, HEAD" } }), false);
    }

    const fragment = url.searchParams.get("fragment") === "main";
    const query = readQuery(url, match.value.query);
    const key = new Request(cacheKey(url, query, fragment));
    const cache = caches.default;
    let response = await cache.match(key);
    // An edge hit carries the zone's hour-long browser cache TTL instead of
    // ours, so browsers would keep showing a page an incident has outdated.
    if (response) response = secure(response, true);
    else {
      response = await render(
        match.value,
        env,
        {
          now,
          origin: url.origin,
          fragment,
          params: match.params,
          query,
          policy: match.value.policy,
        },
        request,
      );
      if (response.ok) ctx.waitUntil(cache.put(key, response.clone()));
    }
    return request.method === "HEAD" ? new Response(null, response) : response;
  },

  async scheduled(controller, env, ctx): Promise<void> {
    await bootstrap(env);
    const now = controller.scheduledTime;
    const work: Promise<unknown>[] = [coordinator(env).arm(), rollup(env, now), runOutbox(env, now, {})];
    if (new Date(now).getUTCMinutes() === CHECK_MINUTE) work.push(checkCertificates(env, now));
    ctx.waitUntil(Promise.all(work));
  },
} satisfies ExportedHandler<Env>;
