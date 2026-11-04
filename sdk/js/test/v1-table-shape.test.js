import { readFileSync } from "node:fs";
import { expect, test } from "vitest";
import { fixture, prefix, token } from "./helpers/v1-http.js";

const recorded = JSON.parse(readFileSync(new URL("./fixtures/table-shape/todos.json", import.meta.url), "utf8"));

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
  expect(requests.every(request => request.url.pathname === `${prefix}/databases/app/tables/todos/shape` && request.headers.authorization === `Bearer ${token}`)).toBe(true);
  expect(states).toEqual([
    { offset: "2", upToDate: false, rows: { 3: { id: 3, title: "Old", data: null }, 5: { id: 5, title: "Gone", data: null } } },
    { offset: "4", upToDate: true, rows: { 3: { id: 3, title: "Renamed", data: null }, 7: { id: 7, title: "New", data: new Uint8Array([0, 1]) } } },
    { offset: "6", upToDate: true, rows: { 3: { id: 3, title: "Renamed", data: null }, 7: { id: 7, title: "Live", data: null } } },
  ]);
  expect(requests.at(-1)?.url.searchParams.get("cursor")).toBe("88400000");
});
