import { expect, test } from "vitest";
import { entity, fixture, prefix } from "./helpers/v1-http.js";

// Native loopback HTTP verifies the SDK NDJSON reader, not a C# producer qualification.
const first = { collection: "items", id: entity, document: { name: "é😀", big: 9007199254740993 } };
const second = { collection: "users", id: "01ARZ3NDEKTSV4RRFFQ69G5FA3", document: { name: "Ada" } };
const ndjson = `{"collection":"items","id":"${entity}","document":{"name":"é😀","big":9007199254740993}}\n${JSON.stringify(second)}\n`;

/** @param {AsyncIterable<unknown>} iterable */
async function collect(iterable) {
  const items = [];
  for await (const item of iterable) items.push(item);
  return items;
}

test("parses NDJSON lines and UTF-8 split across chunks", async () => {
  const bytes = new TextEncoder().encode(ndjson);
  const { client, requests } = await fixture((_request, response) => {
    response.writeHead(200, { "content-type": "application/x-ndjson" });
    // One byte per chunk splits lines, JSON tokens and multi-byte characters.
    for (const byte of bytes) response.write(Uint8Array.of(byte));
    response.end();
  });
  const items = /** @type {any[]} */ (await collect(client.database("app").export()));
  expect(items).toHaveLength(2);
  expect(items[0]).toMatchObject({ collection: first.collection, id: first.id, document: { name: "é😀" } });
  expect(String(items[0].document.big)).toBe("9007199254740993");
  expect(items[1]).toEqual(second);
  expect(requests[0]?.url.pathname).toBe(`${prefix}/databases/app/export`);
  expect(requests[0]?.url.search).toBe("");
  expect(requests[0]?.headers.accept).toBe("application/x-ndjson");
});

