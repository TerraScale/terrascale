import { expect, test } from "vitest";
import { fixture, prefix } from "./helpers/v1-http.js";

test("exportDatabase streams the SQL dump from the recorded route", async () => {
  const dump = "BEGIN;\nINSERT INTO \"todos\" VALUES (1,'ship');\nCOMMIT;";
  const { client, requests } = await fixture((_request, response) => {
    response.writeHead(200, { "content-type": "application/sql" }).end(dump);
  });

  const body = await client.database("app").exportDatabase();
  const text = await new Response(body).text();

  expect(text).toBe(dump);
  expect(requests.map(r => [r.method, r.url.pathname, r.headers.accept])).toEqual([
    ["GET", `${prefix}/databases/app/export`, "text/plain"],
  ]);
});
