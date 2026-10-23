import assert from "node:assert/strict";
import { createServer } from "node:http";

const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
const keyId = "01ARZ3NDEKTSV4RRFFQ69G5FC0";
const id = "01ARZ3NDEKTSV4RRFFQ69G5FA2";
const handle = "h1-0000000000000001";
const first = "first.payload.signature";
const second = "second.payload.signature";

/** A synthetic single-frame object; no producer process or real credential. @param {string} epoch */
async function sealedPage(epoch) {
  const key = new Uint8Array(32).fill(7);
  const page = { shape_id: "a".repeat(32), handle, offset: "2", next_offset: "3", up_to_date: false,
    covered_offset: "3", values_at: "3", cursor_resume: "opaque_resume",
    changes: [{ offset: "3", ops: [{ op: "upsert", id, version: "opaque", document: { private: "first-user-data" } }] }] };
  const plaintext = Buffer.from(JSON.stringify(page));
  const header = Buffer.alloc(70);
  header.write("TBS1");
  header.writeBigUInt64BE(BigInt(epoch), 4);
  header.write(keyId, 12);
  const length = Buffer.alloc(4);
  length.writeUInt32BE(plaintext.byteLength + 0x80000000);
  const { subtle } = globalThis.crypto;
  const base = await subtle.importKey("raw", key, "HKDF", false, ["deriveKey"]);
  const objectKey = await subtle.deriveKey({ name: "HKDF", hash: "SHA-256", salt: header.subarray(38),
    info: Buffer.from(`TBS1 object\u0000${handle}\u00002`) }, base, { name: "AES-GCM", length: 256 }, false, ["encrypt"]);
  const sealed = await subtle.encrypt({ name: "AES-GCM", iv: new Uint8Array(12),
    additionalData: Buffer.concat([header, Buffer.alloc(8), length]), tagLength: 128 }, objectKey, plaintext);
  return { bytes: Buffer.concat([header, length, new Uint8Array(sealed)]), page,
    key: { handle, epoch, key_id: keyId, key: Buffer.from(key).toString("base64url") } };
}

/**
 * Exercise the public API against an HTTP fake, both in source tests and an installed package.
 * @param {typeof import("../../src/index.js").createTerraBaseClient} createClient
 * @param {boolean} [asynchronous]
 */
export async function verifyShapeTokenIdentity(createClient, asynchronous = false) {
  // A future epoch ensures clock expiry cannot hide improper credential-cache reuse.
  const epoch = String(Math.floor(Date.now() / 600_000) + 1);
  const sealed = await sealedPage(epoch);
  const position = { handle, offset: "2", epoch, keyId };
  /** @type {{ path: string; authorization: string | undefined }[]} */
  const requests = [];
  let holdNextKey = false;
  let entered = Promise.withResolvers();
  let release = () => {};
  const server = createServer((request, response) => {
    const path = new URL(request.url ?? "/", "http://localhost").pathname;
    requests.push({ path, authorization: request.headers.authorization });
    response.setHeader("content-type", "application/json");
    if (request.headers.authorization !== `Bearer ${first}`) {
      response.writeHead(403).end('{"error":{"code":"forbidden_scope","message":"Denied"}}');
    } else if (path.endsWith("/shape/keys")) {
      if (holdNextKey) {
        holdNextKey = false;
        release = () => { response.end(JSON.stringify(sealed.key)); };
        entered.resolve(undefined);
      } else response.end(JSON.stringify(sealed.key));
    } else if (path.endsWith("/shape/manifest")) response.end(JSON.stringify({ handle, objects: [] }));
    else if (path.endsWith("/shape")) response.end(JSON.stringify(sealed.page));
    else response.writeHead(404).end();
  });
  try {
    await new Promise((resolve, reject) => {
      server.once("error", reject);
      server.listen(0, "127.0.0.1", () => resolve(undefined));
    });
    const address = server.address();
    assert.ok(address !== null && typeof address !== "string");
    let current = first;
    const collection = createClient({ project, baseUrl: `http://127.0.0.1:${address.port}`,
      token: () => asynchronous ? Promise.resolve(current) : current,
      fetch: (input, init) => {
        if (/\/shape\/(?:keys|manifest)(?:\?|$)/u.test(String(input))) assert.equal(init?.cache, "no-store");
        return globalThis.fetch(input, init);
      },
    }).database("app").collection("users");
    await collection.shapeManifest({ handle, offset: "2" });
    assert.equal((await collection.shape({ handle, offset: "2" })).changes[0]?.ops[0]?.id, id);
    await collection.shapeKey({ handle, epoch, keyId });
    for (let n = 0; n < 2; n++) {
      const page = await collection.decryptShapeObject(sealed.bytes, position);
      assert.deepEqual(page.changes[0]?.ops[0], {
        op: "upsert", id, version: "opaque", document: { private: "first-user-data" },
      });
    }
    assert.equal(requests.filter(request => request.path.endsWith("/keys")).length, 3);

    current = second;
    for (const read of [
      () => collection.shapeManifest({ handle, offset: "2" }),
      () => collection.shape({ handle, offset: "2" }),
      () => collection.shapeKey({ handle, epoch, keyId }),
      () => collection.decryptShapeObject(sealed.bytes, position),
    ]) {
      await assert.rejects(read, { status: 403, code: "forbidden_scope" });
      assert.equal(requests.at(-1)?.authorization, `Bearer ${second}`);
    }

    for (const cancelled of [false, true]) {
      current = first;
      holdNextKey = true;
      entered = Promise.withResolvers();
      const controller = new AbortController();
      const reason = new Error("cancel prior-user key request");
      const pending = collection.decryptShapeObject(sealed.bytes, position, { signal: controller.signal });
      await entered.promise;
      if (cancelled) {
        controller.abort(reason);
        await assert.rejects(pending, error => error === reason);
      }
      current = second;
      await assert.rejects(() => collection.decryptShapeObject(sealed.bytes, position), { status: 403 });
      release();
      // An old authorized read may finish, but it must never authorize a subsequent user's read.
      if (!cancelled) await pending;
      for (let n = 0; n < 2; n++) {
        await assert.rejects(() => collection.decryptShapeObject(sealed.bytes, position), { status: 403 });
        assert.equal(requests.at(-1)?.authorization, `Bearer ${second}`);
      }
    }
  } finally {
    server.closeAllConnections();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve(undefined)));
  }
}
