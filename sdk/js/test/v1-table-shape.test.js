import { readFileSync } from "node:fs";
import { expect, test } from "vitest";
import { fixture, prefix, token } from "./helpers/v1-http.js";
import { createTerraBaseClient } from "../src/index.js";

const recorded = JSON.parse(readFileSync(new URL("./fixtures/table-shape/todos.json", import.meta.url), "utf8"));
const filtered = JSON.parse(readFileSync(new URL("./fixtures/table-shape/todos-where.json", import.meta.url), "utf8"));
const cdn = JSON.parse(readFileSync(new URL("./fixtures/table-shape/cdn.json", import.meta.url), "utf8"));

test("shape(table).subscribe() applies the snapshot, then live changes", async () => {
  const { client, requests } = await fixture((request, response) => {
    const exchange = recorded.exchanges.find((/** @type {{ query: string }} */ entry) => entry.query === request.url.searchParams.toString());
    if (exchange === undefined) { response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ ...recorded.exchanges[3].body, offset: "6", next_offset: "6", changes: [] })); return; }
    if (exchange.status === 307) { response.writeHead(307, { location: `${request.url.pathname}${exchange.location}`, "tb-shape-handle": "h1-0123456789abcdef" }).end(); return; }
    response.writeHead(200, { "content-type": "application/json", ...(exchange.cursor ? { "tb-shape-cursor": exchange.cursor } : {}) }).end(JSON.stringify(exchange.body));
  });
  /** @type {{ rows: Record<string, unknown>; upToDate: boolean; offset: string }[]} */
  const states = [];
  const subscription = client.database("app").shape("todos").subscribe(state => {
    states.push({ rows: Object.fromEntries(state.rows), upToDate: state.upToDate, offset: state.offset });
    if (state.offset === "6") subscription.close();
  });
  await subscription.done;
  expect(requests.every(request => ["", "/manifest"].some(suffix => request.url.pathname === `${prefix}/databases/app/tables/todos/shape${suffix}`) && request.headers.authorization === `Bearer ${token}`)).toBe(true);
  expect(states).toEqual([
    { offset: "2", upToDate: false, rows: { 3: { id: 3, title: "Old", data: null }, 5: { id: 5, title: "Gone", data: null } } },
    { offset: "4", upToDate: true, rows: { 3: { id: 3, title: "Renamed", data: null }, 7: { id: 7, title: "New", data: new Uint8Array([0, 1]) } } },
    { offset: "6", upToDate: true, rows: { 3: { id: 3, title: "Renamed", data: null }, 7: { id: 7, title: "Live", data: null } } },
  ]);
  expect(requests.at(-1)?.url.searchParams.get("cursor")).toBe("88400000");
});

test("shape(table, { where, columns }) repeats the filter on every call and applies move-in and move-out", async () => {
  const { client, requests } = await fixture((request, response) => {
    const exchange = filtered.exchanges.find((/** @type {{ offset: string }} */ entry) => entry.offset === request.url.searchParams.get("offset"));
    if (exchange === undefined) { response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ ...filtered.exchanges[2].body, offset: "10", changes: [] })); return; }
    if (exchange.status === 307) { response.writeHead(307, { location: `${request.url.pathname}?where=${encodeURIComponent(filtered.where)}&columns=${filtered.columns}&${exchange.location}` }).end(); return; }
    response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify(exchange.body));
  });
  /** @type {Record<string, unknown>[]} */
  const views = [];
  const subscription = client.database("app").shape("todos", { where: filtered.where, columns: ["id", "title"] }).subscribe(state => {
    views.push(Object.fromEntries(state.rows));
    if (state.offset === "10") subscription.close();
  });
  await subscription.done;
  expect(requests.every(request => request.url.searchParams.get("where") === filtered.where && request.url.searchParams.get("columns") === filtered.columns)).toBe(true);
  expect(views).toEqual([
    { 3: { id: 3, title: "Write docs" }, 4: { id: 4, title: "Ship" } },
    { 4: { id: 4, title: "Ship it" }, 9: { id: 9, title: "Reopened" } },
  ]);
});

test("shape reads authenticated sealed snapshot and catch-up objects without credentials, then tails the origin", async () => {
  for (const corrupt of [false, true]) {
    /** @type {URL[]} */
    const paths = [];
    let keys = 0;
    const client = createTerraBaseClient({ project: "01ARZ3NDEKTSV4RRFFQ69G5FA1", token, baseUrl: "https://api.terrascale.tech",
      fetch: async (input, init) => {
        const url = new URL(String(input));
        paths.push(url);
        if (url.hostname.startsWith("shapes-")) {
          expect(new Headers(init?.headers).has("authorization")).toBe(false);
          expect(init?.credentials).toBe("omit");
          expect(init?.redirect).toBe("error");
          const entry = cdn.objects.find((/** @type {{ manifest: { url: string } }} */ item) => item.manifest.url === url.href);
          const bytes = Buffer.from(entry.base64, "base64");
          if (corrupt && entry.manifest.offset === "snap.2.0") bytes[bytes.length - 1] = (bytes.at(-1) ?? 0) ^ 1;
          return new Response(bytes);
        }
        expect(new Headers(init?.headers).get("authorization")).toBe(`Bearer ${token}`);
        expect(url.searchParams.get("where")).toBe("done = 0");
        expect(url.searchParams.get("columns")).toBe("id,title,data");
        if (url.pathname.endsWith("/manifest")) return Response.json({ handle: cdn.handle, objects: cdn.objects.filter((/** @type {{ manifest: { offset: string } }} */ entry) => entry.manifest.offset === url.searchParams.get("offset") || (url.searchParams.get("offset") === "snap.2.0" && entry.manifest.offset === "2")).map((/** @type {{ manifest: object }} */ entry) => entry.manifest) });
        if (url.pathname.endsWith("/keys")) { keys++; return Response.json(cdn); }
        const offset = url.searchParams.get("offset");
        if (offset === "-1") return new Response(null, { status: 307, headers: { location: `${url.pathname}?handle=${cdn.handle}&offset=snap.2.0` } });
        if (offset === "snap.2.0") { expect(corrupt).toBe(true); return Response.json(cdn.objects[0].page); }
        if (url.searchParams.has("live")) return Response.json(recorded.exchanges[3].body);
        expect(offset).toBe("4");
        return Response.json({ ...cdn.objects[1].page, offset: "4", next_offset: "4", up_to_date: true, changes: [] }, { headers: { "tb-shape-cursor": "88400000" } });
      },
    });
    /** @type {{ offset: string; rows: Record<string, unknown> }[]} */
    const states = [];
    const subscription = client.database("app").shape("todos", { where: "done = 0", columns: ["id", "title", "data"] }).subscribe(state => {
      states.push({ offset: state.offset, rows: Object.fromEntries(state.rows) });
      if (state.offset === "6") subscription.close();
    });
    await subscription.done;
    expect(keys).toBe(1);
    expect(paths.filter(url => url.hostname.startsWith("shapes-"))).toHaveLength(2);
    expect(paths.filter(url => url.pathname.endsWith("/shape") && !["-1", "4"].includes(url.searchParams.get("offset") ?? ""))).toHaveLength(corrupt ? 1 : 0);
    expect(states.at(-1)).toEqual({ offset: "6", rows: { 3: { id: 3, title: "Renamed", data: null }, 7: { id: 7, title: "Live", data: null } } });
  }
});

const hints = JSON.parse(readFileSync(new URL("./fixtures/table-shape/hints.json", import.meta.url), "utf8"));

test("identical subscriptions share one hint socket and fetch hinted changes from the CDN", async () => {
  let sockets = 0;
  let origin = 0;
  let objects = 0;
  class HintSocket extends EventTarget {
    protocol = hints.protocol;
    readyState = 0;
    constructor() {
      super(); sockets++;
      queueMicrotask(() => {
        this.readyState = 1;
        this.dispatchEvent(new Event("open"));
        this.dispatchEvent(new MessageEvent("message", { data: JSON.stringify(hints.ready) }));
        this.dispatchEvent(new MessageEvent("message", { data: JSON.stringify({ ...hints.changed, manifest: { handle: cdn.handle, objects: [cdn.objects[1].manifest] } }) }));
      });
    }
    send() {}
    close() { this.readyState = 3; this.dispatchEvent(new Event("close")); }
  }
  const client = createTerraBaseClient({ project: "01ARZ3NDEKTSV4RRFFQ69G5FA1", token, baseUrl: "https://api.terrascale.tech", WebSocket: HintSocket,
    fetch: async (input, init) => {
      const url = new URL(String(input));
      if (url.hostname.startsWith("shapes-")) {
        objects++;
        expect(new Headers(init?.headers).has("authorization")).toBe(false);
        const entry = cdn.objects.find((/** @type {{ manifest: { url: string } }} */ item) => item.manifest.url === url.href);
        return new Response(Buffer.from(entry.base64, "base64"));
      }
      origin++;
      if (url.pathname.endsWith("/keys")) return Response.json(cdn);
      const offset = url.searchParams.get("offset");
      if (url.pathname.endsWith("/manifest")) return Response.json({ handle: cdn.handle, objects: offset === "snap.2.0" ? [cdn.objects[0].manifest] : [] });
      if (offset === "-1") return new Response(null, { status: 307, headers: { location: `${url.pathname}?handle=${cdn.handle}&offset=snap.2.0` } });
      expect(offset).toBe("2");
      expect(url.searchParams.has("live")).toBe(false);
      return Response.json({ ...cdn.objects[0].page, offset: "2", next_offset: "2", changes: [], up_to_date: true }, { headers: { "tb-shape-hints": hints.protocol } });
    },
  });
  /** @type {string[][]} */
  const seen = [[], []];
  const subscriptions = seen.map((states, index) => client.database("app").shape("todos").subscribe(state => {
    states.push(state.offset);
    if (state.offset === "2" && state.upToDate && index === 0) subscriptions[0]?.close();
    if (state.offset === "4") subscriptions[index]?.close();
  }));
  await Promise.all(subscriptions.map(subscription => subscription.done));
  expect(seen).toEqual([["2", "2"], ["2", "2", "4"]]);
  expect(sockets).toBe(1);
  expect(origin).toBe(5);
  expect(objects).toBe(2);
});

test("a hint authentication refusal ends the subscription without a payload fallback", async () => {
  let calls = 0;
  class RefusedSocket extends EventTarget {
    protocol = hints.protocol;
    readyState = 0;
    constructor() {
      super();
      queueMicrotask(() => {
        this.dispatchEvent(new Event("open"));
        this.dispatchEvent(new MessageEvent("message", { data: JSON.stringify(hints.refused) }));
      });
    }
    send() {}
    close() { this.readyState = 3; this.dispatchEvent(new Event("close")); }
  }
  const client = createTerraBaseClient({ project: "01ARZ3NDEKTSV4RRFFQ69G5FA1", token, baseUrl: "https://api.terrascale.tech", WebSocket: RefusedSocket,
    fetch: async () => { calls++; return Response.json({ ...recorded.exchanges[2].body, up_to_date: true }, { headers: { "tb-shape-hints": hints.protocol } }); },
  });
  const subscription = client.database("app").shape("todos").subscribe(() => {});
  await expect(subscription.done).rejects.toMatchObject({ status: 401, code: "invalid_key" });
  expect(calls).toBe(1);
});
