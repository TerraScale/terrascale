import { describe, expect, test } from "vitest";
import { V1ShapeSubscription } from "../src/v1/shape-subscription.js";
import { V1Transport } from "../src/v1/transport.js";
import { shapeVectors, vectors } from "./support/shape-vectors.js";

// vectors(group) applies the shared skip list, which is where presigned-manifest is skipped.
const httpVectors = new Map(vectors("http_vectors").map((/** @type {any} */ vector) => [vector.id, vector]));

const project = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
const token = "fixture.payload.signature";
const params = { database: "demo", collection: "notes" };
const handle = "h1-f3171a2230109328";
const shapeId = shapeVectors.fixture.shape_id;
const resume = vectors("apply_vectors")[0].body.cursor_resume;
// The token saved at snapshot completion; a resume attempt must echo it verbatim.

/** @param {string} id @returns {(url: URL) => Response} */
function vector(id) {
  const found = httpVectors.get(id);
  if (found === undefined) throw new Error(`Unknown vector ${id}`);
  return (url) => {
    for (const [key, value] of Object.entries(found.request.query))
      if (url.searchParams.get(key) !== value) throw new Error(`${id}: expected ${key}=${value}, got ${url.search}`);
    return respond(found.response);
  };
}
/** @param {{ status: number; headers?: Record<string, string>; body?: unknown; body_utf8?: string }} response */
function respond({ status, headers = {}, body, body_utf8 }) {
  const text = body_utf8 ?? (body === undefined || body === null ? null : JSON.stringify(body));
  return new Response(text, { status, headers });
}
/** @param {number} status @param {unknown} body @param {Record<string, string>} [headers] */
const json = (status, body, headers = {}) => respond({ status, headers: { "content-type": "application/json", ...headers }, body });
/** @param {string} target @param {string} offset @returns {Response} */
const redirect = (target, offset) => new Response(null, {
  status: 307,
  headers: { location: `/v1/projects/${project}/databases/demo/collections/notes/shape?handle=${target}&offset=${offset}`, "tb-shape-handle": target },
});
/** @param {{ ops?: unknown[]; next?: string }} [page] */
const snapshot = ({ ops = [], next = "2" } = {}) => json(200, {
  shape_id: shapeId, handle, offset: "snap.2.0", next_offset: next, covered_offset: "2", values_at: "2", up_to_date: false,
  cursor_resume: resume, changes: [{ offset: "2", ops }],
}, { "tb-shape-handle": handle });
/** @param {{ offset?: string; next: string; upToDate?: boolean; ops?: unknown[]; cursor?: string; target?: string }} page */
const log = ({ offset = "2", next, upToDate = false, ops = [], cursor, target = handle }) => json(200, {
  shape_id: shapeId, handle: target, offset, next_offset: next, covered_offset: next, values_at: next, up_to_date: upToDate,
  cursor_resume: resume, changes: ops.length === 0 ? [] : [{ offset: next, ops }],
}, { "tb-shape-handle": target, ...(cursor === undefined ? {} : { "tb-shape-cursor": cursor }) });
/** Must-refetch carrying the lineage in the header; the body names it too unless `bodyHandle` is given. @param {string} target @param {string} [bodyHandle] */
const conflict = (target, bodyHandle = target) => json(409, { error: { code: "must_refetch", message: "stale", handle: bodyHandle } }, { "cache-control": "no-store", "tb-shape-handle": target });
/** Must-refetch whose body omits the handle. @param {string} target */
const conflictHeaderOnly = target => json(409, { error: { code: "must_refetch", message: "stale" } }, { "cache-control": "no-store", "tb-shape-handle": target });
/** @typedef {(url: URL, init: RequestInit) => Response | Promise<Response>} Step */
/** @param {Step[]} steps */
function fakeFetch(steps) {
  /** @type {{ url: URL; headers: Headers }[]} */
  const requests = [];
  const remaining = [...steps];
  /** @type {typeof globalThis.fetch} */
  const fetch = async (input, init = {}) => {
    if (init.signal?.aborted) throw init.signal.reason;
    const url = new URL(String(input));
    requests.push({ url, headers: new Headers(init.headers) });
    const step = remaining.shift();
    if (step === undefined) throw new Error(`Unexpected request ${url}`);
    return step(url, init);
  };
  return { fetch, requests };
}
/** @param {typeof globalThis.fetch} fetch @param {any} [shape] @param {any} [options] */
function subscribe(fetch, shape = {}, options = {}) {
  const transport = new V1Transport({ project, token, baseUrl: "https://shapes.test.invalid", fetch });
  return new V1ShapeSubscription(transport, params, shape, options);
}
/** @param {number} count @returns {(subscription: V1ShapeSubscription) => void} */
function stopAfter(count) {
  let seen = 0;
  return subscription => {
    seen += 1;
    if (seen === count) subscription.close();
  };
}
/** @param {string} id */
const operations = id => httpVectors.get(id).response.body.changes.flatMap((/** @type {any} */ change) => change.ops);
const snapshotDocuments = new Map([...operations("snapshot-first-page"), ...operations("snapshot-last-page")].map((/** @type {any} */ op) => [op.id, { version: op.version, document: op.document }]));

describe("V1ShapeSubscription", () => {
  test.each(/** @type {[string, any][]} */ (vectors("apply_vectors").map((/** @type {any} */ apply) => [apply.id, apply])))("applies %s as section 7 requires", async (_id, apply) => {
    const ops = Object.entries(apply.initial_state).map(([id, { version, document }]) => ({ op: "upsert", id, version, document }));
    const { fetch } = fakeFetch([
      () => redirect(handle, "snap.2.0"),
      () => snapshot({ ops }),
      // A body from another lineage arrives only through a resume attempt on that handle.
      ...(apply.body.handle === handle ? [() => json(200, apply.body, { "tb-shape-handle": handle })] : [
        () => conflictHeaderOnly(apply.body.handle),
        () => redirect(apply.body.handle, apply.body.offset),
        () => json(200, apply.body, { "tb-shape-handle": apply.body.handle }),
      ]),
    ]);
    const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(2) });
    await subscription.done;
    expect(subscription.state).toEqual(new Map(Object.entries(apply.expected_state)));
  });

  test("snapshot pages, a fresh head and the live tail echo the cursor bucket verbatim", async () => {
    const { fetch, requests } = fakeFetch([
      vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
      () => log({ next: "2", upToDate: true }),
      () => log({ next: "2", upToDate: true, cursor: "50" }),
      () => log({ next: "4", upToDate: true, cursor: "51", ops: [{ op: "upsert", id: "01ARZ3NDEKTSV4RRFFQ69G5FB0", version: "tbv1-def52df94d5598d237cf1184a4f5cb00", document: { title: "Updated", read: true } }] }),
    ]);
    const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(5) });
    await subscription.done;
    expect(requests[3]?.url.searchParams.has("live")).toBe(false);
    expect(requests[4]?.url.searchParams.get("live")).toBe("true");
    expect(requests[4]?.url.searchParams.has("cursor")).toBe(false);
    expect(requests[5]?.url.searchParams.get("cursor")).toBe("50");
    expect(subscription.state.get("01ARZ3NDEKTSV4RRFFQ69G5FB0")?.document).toEqual({ title: "Updated", read: true });
    expect(subscription.upToDate).toBe(true);
  });

  test("a same-handle must_refetch resets state and bootstraps again", async () => {
    const { fetch, requests } = fakeFetch([
      vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
      vector("closed-commit-upsert-delete"),
      () => conflict(handle),
      vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
    ]);
    const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(6) });
    await subscription.done;
    expect(requests[5]?.url.searchParams.has("handle")).toBe(false);
    expect(subscription.state).toEqual(snapshotDocuments);
  });

  test("terminal auth failures are never retried", async () => {
    const { fetch, requests } = fakeFetch([() => json(401, { error: { code: "unauthorized", message: "no" } })]);
    const subscription = subscribe(fetch);
    await expect(subscription.done).rejects.toMatchObject({ status: 401, code: "unauthorized" });
    expect(requests).toHaveLength(1);
  });

  /** @returns {{ text: string | undefined; saves: (string | undefined)[]; load: () => string | undefined; save: (text: string | undefined) => void }} */
  const memoryReplica = () => {
    const store = { text: /** @type {string | undefined} */ (undefined), saves: /** @type {(string | undefined)[]} */ ([]),
      load: () => store.text, save: (/** @type {string | undefined} */ text) => { store.text = text; store.saves.push(text); } };
    return store;
  };
  /** @param {ReturnType<typeof memoryReplica>} replica @param {any} [shape] */
  const fillReplica = async (replica, shape = {}) => {
    const { fetch } = fakeFetch([vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"), () => log({ next: "2", upToDate: true, cursor: "50" })]);
    await subscribe(fetch, shape, { replica, onUpdate: stopAfter(3) }).done;
  };

  test("a replica restores documents before any response and catches up from its saved position", async () => {
    const replica = memoryReplica();
    await fillReplica(replica);
    expect(replica.saves.filter(text => text !== undefined)).toHaveLength(2);
    /** @type {{ size: number; upToDate: boolean }[]} */
    const seen = [];
    const { fetch, requests } = fakeFetch([() => log({ next: "2", upToDate: true })]);
    const subscription = subscribe(fetch, {}, { replica, onUpdate: (/** @type {V1ShapeSubscription} */ s) => { seen.push({ size: s.state.size, upToDate: s.upToDate }); if (seen.length === 2) s.close(); } });
    await subscription.done;
    expect(seen).toEqual([{ size: snapshotDocuments.size, upToDate: false }, { size: snapshotDocuments.size, upToDate: true }]);
    expect(subscription.state).toEqual(snapshotDocuments);
    expect(requests[0]?.url.searchParams.get("handle")).toBe(handle);
    expect(requests[0]?.url.searchParams.get("offset")).toBe("2");
    expect(requests[0]?.url.searchParams.has("live")).toBe(false);
  });
});
