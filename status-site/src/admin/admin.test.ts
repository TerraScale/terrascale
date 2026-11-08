import { afterEach, describe, expect, it, vi } from "vitest";
import app from "../app";
import type { Env } from "../env";
import { isId } from "../ids";
import type { ProbeRequest, ProbeResponse } from "../probe/protocol";
import { testD1, type TestD1 } from "../testing/d1";
import { COOKIE, issue, SESSION_MS } from "./auth";
import { ORGANIZATION, ORGANIZATION_URL } from "./oidc";
import { vantageBindings } from "../testing/vantages";

const SECRET = "s".repeat(43);
const ORIGIN = "https://status.terrascale.tech";
const ctx = { waitUntil: () => {}, passThroughOnException: () => {} } as unknown as ExecutionContext;
afterEach(() => vi.restoreAllMocks());

/** A vantage answering every target with `status`. */
const vantage = (code: string, status = 200) => ({
  fetch: async (_url: string, init: RequestInit) => {
    const body = JSON.parse(String(init.body)) as ProbeRequest;
    return Response.json({
      vantage: code,
      colo: code.toUpperCase(),
      results: body.targets.map((target) => ({ key: target.key, status, latencyMs: 25, error: null, body: "{}" })),
    } satisfies ProbeResponse);
  },
});

async function setup(options: { allowed?: boolean } = {}) {
  const attempts: string[] = [];
  vi.spyOn(globalThis, "fetch").mockImplementation(async (url) => {
    expect(url).toBe(ORGANIZATION_URL);
    return Response.json({ id: ORGANIZATION, role: "admin" });
  });
  const env = {
    STATUS_DB: testD1(),
    ADMIN_SESSION_SECRET: SECRET,
    ADMIN_LOGIN_LIMITER: {
      limit: async ({ key }: { key: string }) => (attempts.push(key), { success: options.allowed ?? true }),
    },
    ...vantageBindings({
      sfo: vantage("sfo"),
      gru: vantage("gru"),
      fra: vantage("fra"),
      nrt: vantage("nrt", 503),
    }),
  } as unknown as Env & { STATUS_DB: TestD1 };

  const call = (path: string, init: RequestInit = {}) =>
    app.fetch(
      new Request(`${ORIGIN}${path}`, { redirect: "manual", ...init }) as Request<unknown, IncomingRequestCfProperties>,
      env,
      ctx,
    );
  const form = (fields: Record<string, string | string[]>) => {
    const body = new URLSearchParams();
    for (const [name, value] of Object.entries(fields)) for (const item of [value].flat()) body.append(name, item);
    return body;
  };
  const login = () => call("/admin/authorize", { headers: { "cf-connecting-ip": "203.0.113.9" } });
  const cookie = `${COOKIE}=${await issue(SECRET, Date.now(), "synthetic-access", Date.now() + SESSION_MS)}`;
  const get = (path: string, as = cookie) => call(path, { headers: { cookie: as } });
  const csrf = async (as = cookie) =>
    /name="csrf" value="([^"]+)"/.exec(await (await get("/admin", as)).text())?.[1] ?? "";
  const token = await csrf();
  const post = (
    path: string,
    fields: Record<string, string | string[]> = {},
    headers: Record<string, string> = { origin: ORIGIN },
  ) =>
    call(path, {
      method: "POST",
      headers: { cookie, "content-type": "application/x-www-form-urlencoded", ...headers },
      body: form({ csrf: token, ...fields }),
    });
  const one = (query: string, ...values: (string | number | null)[]) =>
    env.STATUS_DB.sqlite.prepare(query).get(...values);
  return { env, call, login, get, post, csrf, cookie, token, attempts, one };
}

describe("admin sign-in", () => {
  it("sends visitors without a session to the sign-in page", async () => {
    const { call } = await setup();
    const response = await call("/admin");
    expect(response.status).toBe(303);
    expect(response.headers.get("location")).toBe("/admin/authorize");
    const page = await call("/admin/login");
    expect(page.status).toBe(200);
    expect(page.headers.get("cache-control")).toBe("no-store");
    expect(page.headers.get("x-robots-tag")).toBe("noindex, nofollow");
    expect(page.headers.get("content-security-policy")).toContain("form-action 'self'");
    expect(await page.text()).toContain("Continue with TerraScale");
  });

  it("rate limits sign-in per client before contacting the identity provider", async () => {
    const { login, attempts } = await setup({ allowed: false });
    const response = await login();
    expect(response.status).toBe(429);
    expect(response.headers.get("set-cookie")).toBeNull();
    expect(attempts.at(-1)).toBe("admin:203.0.113.9");
  });

  it("refuses expired, forged and foreign cookies", async () => {
    const { get, cookie } = await setup();
    const expired = `${COOKIE}=${await issue(SECRET, Date.now() - SESSION_MS - 1000, "synthetic-access", Date.now())}`;
    const forged = [
      expired,
      `${cookie}x`,
      `${COOKIE}=${await issue("another secret", Date.now(), "synthetic-access", Date.now() + SESSION_MS)}`,
      `other=${cookie.slice(COOKIE.length + 1)}`,
    ];
    for (const value of forged) expect((await get("/admin", value)).status).toBe(303);
    expect((await get("/admin", cookie)).status).toBe(200);
  });

  it("requires the session's CSRF token and this origin on every form", async () => {
    const { post, env, csrf } = await setup();
    const fields = { kind: "incident", title: "Outage", message: "Looking." };
    expect((await post("/admin/incidents", { ...fields, csrf: "" })).status).toBe(403);
    expect((await post("/admin/incidents", { ...fields, csrf: "bm90LXRoZS10b2tlbg" })).status).toBe(403);
    // Another session's token is not this session's.
    const other = await csrf(
      `${COOKIE}=${await issue(SECRET, Date.now(), "synthetic-access", Date.now() + SESSION_MS)}`,
    );
    expect((await post("/admin/incidents", { ...fields, csrf: other })).status).toBe(403);
    expect((await post("/admin/incidents", fields, { origin: "https://evil.test" })).status).toBe(403);
    expect((await post("/admin/incidents", fields, {})).status).toBe(403);
    expect(env.STATUS_DB.sqlite.prepare("SELECT count(*) AS n FROM incidents").get()).toEqual({ n: 0 });
    expect((await post("/admin/incidents", fields)).status).toBe(303);
  });

  it("refuses cross-site form posts and mutations without a session", async () => {
    const { call } = await setup();
    expect((await call("/admin/login", { method: "POST", headers: { origin: "https://evil.test" } })).status).toBe(403);
    const response = await call("/admin/incidents", {
      method: "POST",
      headers: { origin: ORIGIN, "content-type": "application/x-www-form-urlencoded" },
      body: "kind=incident",
    });
    expect(response.status).toBe(401);
  });

  it("signs out by clearing the cookie", async () => {
    const { post } = await setup();
    const response = await post("/admin/logout");
    expect(response.status).toBe(303);
    expect(response.headers.get("set-cookie")).toMatch(new RegExp(`^${COOKIE}=; Path=/; Max-Age=0; HttpOnly; Secure`));
  });
});

describe("admin actions", () => {
  const GRU_API = "01M4E7G1W82VKFPF5FJ9AVK1DQ";
  const WEBSITE = "01M4E7G1W82VKFPF5FJ9AVK1D3";
  const DOCS = "01M4E7G1W82VKFPF5FJ9AVK1CX";
  const PLATFORM = "01M4E7G1W7XH3WHVG75QYZRE6Y";

  const idFrom = (response: Response) =>
    /\/admin\/incidents\/([0-9A-Z]{26})/.exec(response.headers.get("location") ?? "")?.[1] ?? "";

  it("publishes, edits, updates and resolves an incident", async () => {
    const { post, get, one, env } = await setup();
    const created = await post("/admin/incidents", {
      kind: "incident",
      title: "Docs errors",
      message: "Some pages fail.",
      impact: "major",
      components: [DOCS],
    });
    expect(created.status).toBe(303);
    const id = idFrom(created);
    expect(isId(id)).toBe(true);
    expect(created.headers.get("location")).toBe(`/admin/incidents/${id}?done=created`);
    expect(one("SELECT status, impact FROM incidents WHERE id = ?", id)).toEqual({
      status: "investigating",
      impact: "major",
    });
    expect(one("SELECT component_id FROM incident_components WHERE incident_id = ?", id)).toEqual({
      component_id: DOCS,
    });
    const page = await (await get(`/admin/incidents/${id}?done=created`)).text();
    expect(page).toContain("Created.");
    expect(page).toContain("Some pages fail.");

    expect(
      (
        await post(`/admin/incidents/${id}/edit`, {
          title: "Docs down",
          impact: "critical",
          components: [DOCS, WEBSITE],
        })
      ).status,
    ).toBe(303);
    expect(one("SELECT title, impact, operator_touched FROM incidents WHERE id = ?", id)).toEqual({
      title: "Docs down",
      impact: "critical",
      operator_touched: 1,
    });
    expect(
      env.STATUS_DB.sqlite
        .prepare("SELECT component_id FROM incident_components WHERE incident_id = ? ORDER BY component_id")
        .all(id),
    ).toHaveLength(2);

    expect((await post(`/admin/incidents/${id}/updates`, { status: "resolved", message: "Fixed." })).status).toBe(303);
    expect(one("SELECT status, resolved_at IS NOT NULL AS resolved FROM incidents WHERE id = ?", id)).toEqual({
      status: "resolved",
      resolved: 1,
    });
    expect(one("SELECT count(*) AS n FROM incident_updates WHERE incident_id = ?", id)).toEqual({ n: 2 });
    // A status of the wrong kind is refused.
    const wrong = await post(`/admin/incidents/${id}/updates`, { status: "completed", message: "No." });
    expect(wrong.status).toBe(400);
    expect(await wrong.text()).toContain("status: must be one of");
  });

  it("schedules maintenance in UTC and refuses a window that ends first", async () => {
    const { post, one } = await setup();
    const created = await post("/admin/incidents", {
      kind: "maintenance",
      title: "Database upgrade",
      message: "Short pauses.",
      scheduledStart: "2026-10-20T02:00",
      scheduledEnd: "2026-10-20T03:00",
    });
    const id = idFrom(created);
    expect(one("SELECT status, impact, scheduled_start, scheduled_end FROM incidents WHERE id = ?", id)).toEqual({
      status: "scheduled",
      impact: "none",
      scheduled_start: "2026-10-20 02:00:00",
      scheduled_end: "2026-10-20 03:00:00",
    });
    expect((await post(`/admin/incidents/${id}/edit`, { scheduledEnd: "2026-10-20T01:00" })).status).toBe(400);
    expect(
      (
        await post("/admin/incidents", {
          kind: "warning",
          title: "x",
          message: "y",
          scheduledStart: "2026-10-20T02:00",
        })
      ).status,
    ).toBe(400);
  });

  it("deletes an incident only with the box ticked", async () => {
    const { post, one } = await setup();
    const id = idFrom(await post("/admin/incidents", { kind: "warning", title: "Oops", message: "Mistake." }));
    expect((await post(`/admin/incidents/${id}/delete`)).status).toBe(400);
    expect(one("SELECT count(*) AS n FROM incidents")?.n).toBe(1);
    const deleted = await post(`/admin/incidents/${id}/delete`, { confirm: "yes" });
    expect(deleted.headers.get("location")).toBe("/admin?done=deleted");
    expect(one("SELECT count(*) AS n FROM incidents")?.n).toBe(0);
    expect((await post(`/admin/incidents/${id}/delete`, { confirm: "yes" })).status).toBe(404);
  });

  it("records a deploy marker once per endpoint and second", async () => {
    const { post, one } = await setup();
    expect((await post("/admin/deploys", { endpointId: GRU_API, version: "2026.10.11" })).status).toBe(303);
    expect(one("SELECT version FROM deploys WHERE endpoint_id = ?", GRU_API)).toEqual({ version: "2026.10.11" });
    expect((await post("/admin/deploys", { endpointId: GRU_API, version: "again" })).status).toBe(409);
    expect((await post("/admin/deploys", { endpointId: "dashboard", version: "1" })).status).toBe(400);
  });

  it("manages groups, refusing to delete one with components", async () => {
    const { post, one } = await setup();
    expect((await post("/admin/groups", { name: "Tools", position: "3" })).status).toBe(303);
    const { id } = one("SELECT id FROM component_groups WHERE name = 'Tools'") as { id: string };
    expect((await post(`/admin/groups/${id}/edit`, { name: "Developer tools", position: "4" })).status).toBe(303);
    expect(one("SELECT name, position FROM component_groups WHERE id = ?", id)).toEqual({
      name: "Developer tools",
      position: 4,
    });
    expect((await post(`/admin/groups/${PLATFORM}/delete`, { confirm: "yes" })).status).toBe(409);
    expect((await post(`/admin/groups/${id}/delete`, { confirm: "yes" })).status).toBe(303);
    expect(one("SELECT count(*) AS n FROM component_groups WHERE id = ?", id)).toEqual({ n: 0 });
  });

  it("manages components, refusing to plan or delete one with an open automatic incident", async () => {
    const { post, one, env } = await setup();
    expect(
      (
        await post("/admin/components", {
          groupId: PLATFORM,
          name: "Status",
          lifecycle: "active",
          description: "This page",
        })
      ).status,
    ).toBe(303);
    const { id } = one("SELECT id FROM components WHERE name = 'Status'") as { id: string };
    expect(isId(id)).toBe(true);
    expect(
      (await post(`/admin/components/${id}/edit`, { groupId: PLATFORM, name: "Status page", lifecycle: "planned" }))
        .status,
    ).toBe(303);
    expect(one("SELECT name, lifecycle, description FROM components WHERE id = ?", id)).toEqual({
      name: "Status page",
      lifecycle: "planned",
      description: "",
    });
    expect(
      (await post("/admin/components", { groupId: "00000000000000000000000000", name: "x", lifecycle: "active" }))
        .status,
    ).toBe(400);

    env.STATUS_DB.sqlite.exec(`INSERT INTO incidents (id, kind, title, status, impact, source, auto_key)
      VALUES ('00000000000000000000000001', 'incident', 'Website is down', 'investigating', 'critical', 'auto', '${WEBSITE}')`);
    expect(
      (await post(`/admin/components/${WEBSITE}/edit`, { groupId: PLATFORM, name: "Website", lifecycle: "planned" }))
        .status,
    ).toBe(409);
    expect((await post(`/admin/components/${WEBSITE}/delete`, { confirm: "yes" })).status).toBe(409);
    expect((await post(`/admin/endpoints/01M4E7G1W82VKFPF5FJ9AVK1DS/delete`, { confirm: "yes" })).status).toBe(409);

    // Deleting a component takes its endpoints and their history with it.
    env.STATUS_DB.sqlite.exec(`INSERT INTO probe_samples (endpoint_id, at, state, ok, total, results)
      VALUES ('01M4E7G1W82VKFPF5FJ9AVK1DD', '2026-10-11 00:00:00', 'operational', 3, 3, '[]')`);
    expect((await post(`/admin/components/${DOCS}/delete`, { confirm: "yes" })).status).toBe(303);
    expect(one("SELECT count(*) AS n FROM endpoints WHERE component_id = ?", DOCS)).toEqual({ n: 0 });
    expect(one("SELECT count(*) AS n FROM probe_samples")).toEqual({ n: 0 });
  });

  it("adds, edits and deletes endpoints, validating every field", async () => {
    const { post, one, get } = await setup();
    const fields = {
      componentId: DOCS,
      regionCode: "gru",
      cluster: "gru",
      provider: "other",
      kind: "http",
      url: "https://docs.terrascale.tech/healthz",
      headers: '{"Cache-Control":"no-cache"}',
      expectStatus: "200,204",
      assertions: '[{"path":"status","op":"eq","value":"ok"}]',
      extract: '{"version":"version"}',
      indirect: "1",
    };
    expect((await post("/admin/endpoints", fields)).status).toBe(303);
    const { id } = one("SELECT id FROM endpoints WHERE url = ?", fields.url) as { id: string };
    expect(
      one("SELECT region_code, headers, expect_status, indirect, live_since FROM endpoints WHERE id = ?", id),
    ).toEqual({
      region_code: "GRU",
      headers: '{"Cache-Control":"no-cache"}',
      expect_status: "200,204",
      indirect: 1,
      live_since: null,
    });
    for (const [field, value, error] of [
      ["url", "http://docs.terrascale.tech/", "url: must be an https:// URL"],
      ["headers", "[1]", "headers: must be a JSON object"],
      ["headers", "{nope", "headers: must be JSON"],
      ["assertions", '[{"path":"a..b","op":"eq"}]', "assertions: must be a JSON list"],
      ["extract", '{"v":"a..b"}', "extract: must be a JSON object"],
      ["expectStatus", "ok", "expectStatus: must be"],
      ["regionCode", "ams", "A referenced record does not exist"],
      ["provider", "oci", "provider: must be one of cloudflare, other"],
      ["blockedStatus", "999", "blockedStatus: must be 100 to 599"],
    ]) {
      const response = await post("/admin/endpoints", { ...fields, [field!]: value! });
      expect(response.status).toBe(400);
      expect(await response.text()).toContain(error!.replace(/"/g, "&quot;"));
    }
    expect(
      (await post(`/admin/endpoints/${id}/edit`, { ...fields, regionCode: "", cluster: "global", indirect: "" }))
        .status,
    ).toBe(303);
    expect(one("SELECT region_code, cluster, indirect FROM endpoints WHERE id = ?", id)).toEqual({
      region_code: null,
      cluster: "global",
      indirect: 0,
    });
    expect(await (await get("/admin/catalog")).text()).toContain(`id="endpoint-${id}"`);
    expect((await post(`/admin/endpoints/${id}/delete`, { confirm: "yes" })).status).toBe(303);
    expect(one("SELECT count(*) AS n FROM endpoints WHERE id = ?", id)).toEqual({ n: 0 });
  });

  it("manages regions, refusing to delete one with endpoints", async () => {
    const { post, one } = await setup();
    const fields = {
      name: "Amsterdam",
      country: "Netherlands",
      mapX: "66",
      mapY: "21",
      pingUrl: "https://api.terrascale.tech/healthz",
    };
    expect((await post("/admin/regions", { code: "ams", ...fields })).status).toBe(303);
    expect(one("SELECT code, name, map_x FROM regions WHERE code = 'AMS'")).toEqual({
      code: "AMS",
      name: "Amsterdam",
      map_x: 66,
    });
    expect((await post("/admin/regions", { code: "AMS", ...fields })).status).toBe(409);
    expect((await post("/admin/regions", { code: "AMST", ...fields })).status).toBe(400);
    expect((await post("/admin/regions/AMS/edit", { ...fields, mapX: "500" })).status).toBe(400);
    expect((await post("/admin/regions/AMS/edit", { ...fields, name: "Amsterdam (ams)" })).status).toBe(303);
    expect(one("SELECT name FROM regions WHERE code = 'AMS'")).toEqual({ name: "Amsterdam (ams)" });
    expect((await post("/admin/regions/GRU/delete", { confirm: "yes" })).status).toBe(409);
    expect((await post("/admin/regions/AMS/delete", { confirm: "yes" })).status).toBe(303);
    expect(one("SELECT count(*) AS n FROM regions WHERE code = 'AMS'")).toEqual({ n: 0 });
  });

  it("lists and removes subscribers", async () => {
    const { post, get, one, env } = await setup();
    env.STATUS_DB.sqlite.exec(`INSERT INTO subscribers (id, channel, target, flavor, components, confirmed_at)
      VALUES ('00000000000000000000000005', 'email', 'ops@example.com', 'email', '["${DOCS}"]', '2026-10-11 00:00:00')`);
    const page = await (await get("/admin/subscribers")).text();
    expect(page).toContain("ops@example.com");
    expect(page).toContain("Documentation");
    expect((await post("/admin/subscribers/00000000000000000000000005/delete", { confirm: "yes" })).status).toBe(303);
    expect(one("SELECT count(*) AS n FROM subscribers")).toEqual({ n: 0 });
  });

  it("checks endpoints from every vantage on demand, storing nothing", async () => {
    const { post, get, one } = await setup();
    const all = await post("/admin/probes/run");
    expect(all.status).toBe(200);
    const page = await all.text();
    expect(page).toContain("Checked just now");
    expect(page).toContain("TerraBase API · gru");
    expect(page).toContain("25 ms");
    expect(page).toContain("HTTP 503");
    const single = await (await post("/admin/probes/run", { endpointId: GRU_API })).text();
    expect(single.match(/<tr><th scope="row">/g)).toHaveLength(1);
    expect((await post("/admin/probes/run", { endpointId: "01M4E7G1W82VKFPF5FJ9AVK1DF" })).status).toBe(400);
    expect(one("SELECT count(*) AS n FROM probe_samples")).toEqual({ n: 0 });
    expect((await get("/admin/probes")).status).toBe(200);
  });

  it("renders every page with the session's CSRF token, and 404s unknown ones", async () => {
    const { get, token } = await setup();
    for (const path of ["/admin", "/admin/catalog", "/admin/subscribers", "/admin/probes"]) {
      const response = await get(path);
      expect(response.status).toBe(200);
      expect(await response.text()).toContain(`name="csrf" value="${token}"`);
    }
    expect((await get("/admin/nope")).status).toBe(404);
    expect((await get("/admin/incidents/00000000000000000000000009")).status).toBe(404);
    expect((await get("/admin/incidents/not-an-id")).status).toBe(404);
  });
});
