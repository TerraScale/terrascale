import { createHash } from "node:crypto";
import { afterEach, describe, expect, test, vi } from "vitest";
import { parseDatabaseJson } from "./database-codec.js";
import { parseTerraBaseRoutingDocument, TerraBaseDiscoveryRouter } from "./discovery.js";

// Exact actual Worker + SQLite DO output. Fixed clock and synthetic R3 17/41 are codec evidence.
const successBytes = "{\"schema_version\":1,\"scope\":{\"tenant_id\":\"10000000-0000-0000-0000-000000000001\",\"database_id\":\"20000000-0000-0000-0000-000000000001\",\"shard_id\":\"18446744073709551615\"},\"authority\":{\"deployment_id\":\"fixture-deployment\",\"cell\":\"gru\",\"publisher_id\":\"publisher-a\",\"publisher_generation\":\"18446744073709551615\"},\"revision\":\"41\",\"published_at\":1790769600,\"expires_at\":1790769609,\"refreshSeconds\":8,\"residency\":null,\"endpoints\":[{\"region\":\"GRU\",\"url\":\"https://gru.api.terrascale.tech\",\"catalog\":{\"digest\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\"},\"capabilities\":{\"atomic_groups\":false,\"client_v1\":true,\"conditional_mutation\":false,\"query\":true,\"read\":true,\"security_state\":false,\"subscriptions\":true,\"write\":true},\"security_writer\":null,\"priority\":1}]}";
const forbiddenBytes = "{\"error\":{\"code\":\"discovery_forbidden\",\"message\":\"native discovery admission is unavailable\",\"fix\":\"retry with current authorized native scope metadata\"}}";
const now = 1790769600000;
const scope = { tenant: "10000000-0000-0000-0000-000000000001", database: "20000000-0000-0000-0000-000000000001", shard: 18446744073709551615n };
const key = "tbk1." + Buffer.alloc(16, 5).toString("base64url") + "." + Buffer.alloc(32, 6).toString("base64url");
const headers = { "content-type": "application/json", "cache-control": "private, no-store" };
/** @returns {Record<string, any>} */
const fixture = () => JSON.parse(successBytes);
/**
 * @param {string} [bytes]
 * @param {number} [status]
 * @returns {Response}
 */
const response = (bytes = successBytes, status = 200) => new Response(bytes, { status, headers });

afterEach(() => vi.useRealTimers());

describe("actual receiver frozen byte conformance", () => {
  test.each(["API", "ATLAS", "AUTH", "SYNC", "STATUS", "WWW", "APP", "DASH", "DOCS", "GIT", "CDN", "ADMIN", "MCP", "MAIL"])("rejects reserved %s as an endpoint or residency region", code => {
    const value = fixture();
    value.endpoints[0].region = code;
    value.endpoints[0].url = `https://${code.toLowerCase()}.api.terrascale.tech`;
    expect(() => parseTerraBaseRoutingDocument(value, scope, now)).toThrow(/Invalid native region/u);
    const resident = fixture(); resident.residency = [code];
    expect(() => parseTerraBaseRoutingDocument(resident, scope, now)).toThrow(/Invalid native region/u);
  });

  test.each([
    ["GRU", "https://gru.api.terrascale.tech"],
    ["AMS", "https://ams.api.terrascale.tech"],
    ["ABCDEFGHIJKLMNOP", "https://abcdefghijklmnop.api.terrascale.tech"],
    ["9-A", "https://9-a.api.terrascale.tech"],
  ])("preserves canonical native endpoint and residency %s", (code, origin) => {
    const value = fixture(); value.residency = [code];
    value.endpoints[0].region = code; value.endpoints[0].url = origin;
    const parsed = parseTerraBaseRoutingDocument(value, scope, now);
    expect(parsed.residency).toEqual([code]);
    expect(parsed.endpoints[0]).toMatchObject({ region: code, url: origin });
  });

  test("pins derived production-origin success and historical forbidden codec bytes", () => {
    expect(createHash("sha256").update(successBytes).digest("hex")).toBe("82fbc6518a5e8e825879243c45a86e6e66f44323a6dcd3ad8280c8109aa671ec");
    expect(createHash("sha256").update(forbiddenBytes).digest("hex")).toBe("3208d206e45cde22446e0e227c4882bdacb97f2a4926c0341a13580c23e48b9c");
    const doc = parseTerraBaseRoutingDocument(parseDatabaseJson(new TextEncoder().encode(successBytes)), scope, now);
    expect(doc.scope).toEqual(scope);
    expect(doc.authority.publisher_generation).toBe(18446744073709551615n);
    expect(doc.revision).toBe(41n);
    expect(doc.refreshSeconds).toBe(8);
    expect(doc.endpoints[0]).toMatchObject({ region: "GRU", url: "https://gru.api.terrascale.tech", security_writer: null, capabilities: { conditional_mutation: false, atomic_groups: false, security_state: false } });
    expect(Object.isFrozen(doc.endpoints[0]?.capabilities)).toBe(true);
  });

  test("sends the exact private canonical GET without ambient credentials or redirects", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    /** @type {Request | undefined} */
    let request;
    const router = new TerraBaseDiscoveryRouter(scope, async input => { request = /** @type {Request} */ (input); return response(); });
    expect((await router.resolve(key, ["client_v1", "read"])).url).toBe("https://gru.api.terrascale.tech");
    if (request === undefined) throw new Error("Discovery was not dispatched.");
    expect(request.url).toBe("https://atlas.terrascale.tech/api/v1/discovery?tenant_id=10000000-0000-0000-0000-000000000001&database_id=20000000-0000-0000-0000-000000000001&shard_id=18446744073709551615");
    expect([...request.headers]).toEqual([["accept", "application/json"], ["authorization", "Bearer " + key]]);
    expect(request.credentials).toBe("omit"); expect(request.redirect).toBe("manual"); expect(request.cache).toBe("no-store");
    expect(request.referrerPolicy).toBe("no-referrer");
  });

  test("preserves the actual private 403 denial without leaking metadata or credentials", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const router = new TerraBaseDiscoveryRouter(scope, async () => response(forbiddenBytes, 403));
    await expect(router.resolve(key, ["read"])).rejects.toMatchObject({ code: "discovery_forbidden", status: 403 });
  });

  test.each(/** @type {[string, (doc: any) => void][]} */ ([
    ["legacy routing", doc => { doc.v = 1; }],
    ["unknown nested field", doc => { doc.authority.writer = "invented"; }],
    ["foreign scope", doc => { doc.scope.tenant_id = "10000000-0000-0000-0000-000000000002"; }],
    ["nil native UUID", doc => { doc.scope.tenant_id = "00000000-0000-0000-0000-000000000000"; }],
    ["noncanonical shard", doc => { doc.scope.shard_id = "01"; }],
    ["number shard", doc => { doc.scope.shard_id = Number(doc.scope.shard_id); }],
    ["overflow revision", doc => { doc.revision = "18446744073709551616"; }],
    ["zero epoch", doc => { doc.authority.publisher_generation = "0"; }],
    ["publisher identity grammar", doc => { doc.authority.publisher_id = "Publisher.A"; }],
    ["publisher token overflow", doc => { doc.authority.publisher_id = "a".repeat(64); }],
    ["publisher token prefix", doc => { doc.authority.publisher_id = "-publisher"; }],
    ["expired lease", doc => { doc.expires_at = doc.published_at; }],
    ["future publication", doc => { doc.published_at += 1; }],
    ["overlong lease", doc => { doc.expires_at += 2; }],
    ["refresh widening", doc => { doc.refreshSeconds = 60; }],
    ["zero refresh", doc => { doc.refreshSeconds = 0; }],
    ["label residency", doc => { doc.residency = "br-only"; }],
    ["empty residency", doc => { doc.residency = []; }],
    ["sparse residency", doc => { doc.residency = new Array(1); }],
    ["excluded region", doc => { doc.residency = ["FRA"]; }],
    ["duplicate residency", doc => { doc.residency = ["GRU", "GRU"]; }],
    ["API alias", doc => { doc.endpoints[0].url = "https://gru.terrascale.tech"; }],
    ["API host", doc => { doc.endpoints[0].region = "API"; doc.endpoints[0].url = "https://api.terrascale.tech"; }],
    ["nonbare origin", doc => { doc.endpoints[0].url += "/"; }],
    ["noncanonical hostname", doc => { doc.endpoints[0].url = "https://GRU.api.terrascale.tech"; }],
    ["port", doc => { doc.endpoints[0].url += ":443"; }],
    ["ineligible native protocol", doc => { doc.endpoints[0].capabilities.client_v1 = false; }],
    ["no read or write", doc => { doc.endpoints[0].capabilities.read = false; doc.endpoints[0].capabilities.write = false; }],
    ["missing capability", doc => { delete doc.endpoints[0].capabilities.query; }],
    ["nonboolean capability", doc => { doc.endpoints[0].capabilities.read = 1; }],
    ["unknown capability", doc => { doc.endpoints[0].capabilities.sql = true; }],
    ["invented conditional readiness", doc => { doc.endpoints[0].capabilities.conditional_mutation = true; }],
    ["invented group readiness", doc => { doc.endpoints[0].capabilities.atomic_groups = true; }],
    ["invented security readiness", doc => { doc.endpoints[0].capabilities.security_state = true; }],
    ["invented writer", doc => { doc.endpoints[0].security_writer = {}; }],
    ["bad priority", doc => { doc.endpoints[0].priority = 2; }],
    ["sparse endpoints", doc => { doc.endpoints = new Array(1); }],
    ["missing catalog", doc => { delete doc.endpoints[0].catalog; }],
    ["bad catalog digest", doc => { doc.endpoints[0].catalog.digest = "CC".repeat(32); }],
  ]))("rejects %s without widening the frozen descriptor", (_label, change) => {
    const doc = fixture(); change(doc);
    expect(() => parseTerraBaseRoutingDocument(doc, scope, now)).toThrow(TypeError);
  });

  test.each([
    successBytes.replace('"revision":"41"', '"revision":"41","revision":"42"'),
    successBytes.replace('"read":true', '"read":true,"read":false'),
    successBytes.replace('"revision":"41"', '"revision":41'),
    '{"v":1,"database":"old","branch":"main","endpoints":[]}',
  ])("rejects duplicate, number and legacy raw response fields", async bytes => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    await expect(new TerraBaseDiscoveryRouter(scope, async () => response(bytes)).resolve(key, ["read"])).rejects.toMatchObject({ code: "invalid_response", status: 200 });
  });

  test("rejects duplicate endpoint identity and rank instead of guessing an eligible route", () => {
    const value = fixture(); value.endpoints.push(structuredClone(value.endpoints[0]));
    expect(() => parseTerraBaseRoutingDocument(value, scope, now)).toThrow(TypeError);
  });

  test("accepts the actual publisher token maximum without imposing deployment grammar on it", () => {
    const value = fixture(); value.authority.publisher_id = "a".repeat(63);
    expect(parseTerraBaseRoutingDocument(value, scope, now).authority.publisher_id).toBe(value.authority.publisher_id);
  });

  test.each([
    [403, { error: { code: "invalid_key", message: "denied", fix: "retry" } }],
    [503, { error: { code: "discovery_forbidden", message: "denied", fix: "retry" } }],
    [403, { error: { code: "discovery_forbidden", message: "denied", fix: "retry", metadata: {} } }],
  ])("rejects a noncontract error at HTTP %s", async (status, value) => {
    await expect(new TerraBaseDiscoveryRouter(scope, async () => response(JSON.stringify(value), status)).resolve(key, ["read"])).rejects.toMatchObject({ code: "invalid_response", status });
  });

  test.each([301, 302, 307, 308, 201, 204, 401, 404, 500])("rejects unadmitted HTTP %s without following a redirect", async status => {
    let calls = 0;
    const router = new TerraBaseDiscoveryRouter(scope, async () => { calls++; return new Response(status === 204 ? null : successBytes, { status, headers }); });
    await expect(router.resolve(key, ["read"])).rejects.toMatchObject({ code: "invalid_response", status });
    expect(calls).toBe(1);
  });

  test("rejects a custom transport's followed redirect even when it returns HTTP 200", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const value = response(); Object.defineProperty(value, "redirected", { value: true });
    await expect(new TerraBaseDiscoveryRouter(scope, async () => value).resolve(key, ["read"])).rejects.toMatchObject({ code: "invalid_response", status: 200 });
  });

  test.each([
    new Response(successBytes, { headers: { ...headers, "content-type": "text/html" } }),
    new Response(successBytes, { headers: { "content-type": "application/json", "cache-control": "public, max-age=60" } }),
    new Response(" ".repeat(256 * 1024 + 1), { headers }),
    new Response(successBytes.replace('"GRU"', '"\\ud800"'), { headers }),
  ])("rejects wrong media/cache policy, oversized or malformed UTF-8 response", async value => {
    await expect(new TerraBaseDiscoveryRouter(scope, async () => value.clone()).resolve(key, ["read"])).rejects.toMatchObject({ code: "invalid_response" });
  });

  test("validates nonnil native scope and canonical tbk1 bits before dispatch", async () => {
    let calls = 0;
    const router = new TerraBaseDiscoveryRouter(scope, async () => { calls++; return response(); });
    for (const invalid of ["access-token", key + "=", key.slice(0, -1) + "B"]) {
      await expect(router.resolve(invalid, ["read"])).rejects.toMatchObject({ code: "invalid_discovery_credential" });
    }
    expect(() => new TerraBaseDiscoveryRouter({ ...scope, tenant: "00000000-0000-0000-0000-000000000000" })).toThrow();
    expect(calls).toBe(0);
  });

  test("generic full-u64 shard zero can receive denial but cannot become a positive managed route", async () => {
    const zero = { ...scope, shard: 0n };
    /** @type {Request | undefined} */
    let request;
    const router = new TerraBaseDiscoveryRouter(zero, async input => { request = /** @type {Request} */ (input); return response(forbiddenBytes, 403); });
    await expect(router.resolve(key, ["read"])).rejects.toMatchObject({ code: "discovery_forbidden", status: 403 });
    if (request === undefined) throw new Error("Discovery was not dispatched.");
    expect(new URL(request.url).searchParams.get("shard_id")).toBe("0");
    const value = fixture(); value.scope.shard_id = "0";
    expect(() => parseTerraBaseRoutingDocument(value, zero, now)).toThrow(TypeError);
  });
});
