import assert from "node:assert/strict";
import { Agent, getGlobalDispatcher, setGlobalDispatcher } from "undici";
import { createTerraBaseV1Client } from "../../src/index.js";
import { createTsAuthClient } from "../../src/ts-auth.js";

assert.notEqual(process.env.NODE_TLS_REJECT_UNAUTHORIZED, "0");
const [origin, surface, protocol] = process.argv.slice(2);
if (origin === undefined) throw new Error("Missing transport fixture origin.");
const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
const token = "fixture.payload.signature";
// This isolated process owns its application dispatcher and CA trust.
// The SDK must use that policy without replacing it or creating a private pool.
const dispatcher = new Agent({ allowH2: true, connections: 1 });
setGlobalDispatcher(dispatcher);
// Two clients and native fetch must share the application-owned connection.
const documents = Array.from({ length: 2 }, () => createTerraBaseV1Client({ project, token, baseUrl: origin }).database("app").collection("users"));
const auth = Array.from({ length: 2 }, () => createTsAuthClient({ apiBaseUrl: origin, instanceSlug: "acme", accessToken: token }));
/** @param {number} index */
async function read(index) {
  if (surface === "documents") {
    const client = documents[index % 2];
    assert.ok(client);
    assert.equal((await client.get(project)).document?.value, 1);
  } else {
    const client = auth[index % 2];
    assert.ok(client);
    const result = await client.users.list();
    assert.equal(result.ok, true);
    if (result.ok) assert.deepEqual(result.value.data, []);
  }
}
try {
  const response = await fetch(origin);
  assert.equal(response.status, 200);
  await response.arrayBuffer();
  await read(0); // Establish the SDK path before testing concurrent streams.
  if (protocol === "h2") await Promise.all(Array.from({ length: 16 }, (_, index) => read(index)));
  else for (let index = 0; index < 16; index++) await read(index);
  assert.equal(getGlobalDispatcher(), dispatcher);
} finally {
  await dispatcher.close();
}
