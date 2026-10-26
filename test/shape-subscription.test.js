import { describe, expect, test, vi } from "vitest";
import { parseExactJson } from "../src/v1/json.js";
import { V1ShapeSubscription } from "../src/v1/shape-subscription.js";
import { V1Transport } from "../src/v1/transport.js";
import { shapeVectors, vectors } from "./support/shape-vectors.js";

// vectors(group) applies the shared skip list, which is where presigned-manifest is skipped.
const httpVectors = new Map(vectors("http_vectors").map((/** @type {any} */ vector) => [vector.id, vector]));

const project = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
const token = "fixture.payload.signature";
const params = { database: "demo", collection: "notes" };
const handle = "h1-f3171a2230109328";
const otherHandle = "h1-d90dddd41bd8282d";
const shapeId = shapeVectors.fixture.shape_id;
const resume = vectors("apply_vectors")[0].body.cursor_resume;
// The token saved at snapshot completion; a resume attempt must echo it verbatim.
const snapshotResume = httpVectors.get("snapshot-last-page").response.body.cursor_resume;

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
/** A request that never answers until the subscription aborts it. @type {Step} */
const hang = (_url, init) => new Promise((_resolve, reject) => init.signal?.addEventListener("abort", () => reject(init.signal?.reason), { once: true }));
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

  test("falling behind sends no live bucket, and the next live request uses only the latest fresh bucket", async () => {
    const { fetch, requests } = fakeFetch([
      vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
      () => log({ next: "2", upToDate: true }),
      () => log({ next: "2", upToDate: true, cursor: "50" }),
      () => log({ next: "4", cursor: "51" }),
      () => log({ offset: "4", next: "4", upToDate: true }),
      () => log({ offset: "4", next: "4", upToDate: true, cursor: "50" }),
    ]);
    const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(7) });
    await subscription.done;
    expect(requests[5]?.url.searchParams.get("cursor")).toBe("50");
    expect(requests[6]?.url.searchParams.has("live")).toBe(false);
    expect(requests[6]?.url.searchParams.has("cursor")).toBe(false);
    expect(requests[7]?.url.searchParams.get("live")).toBe("true");
    expect(requests[7]?.url.searchParams.has("cursor")).toBe(false);
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

  test("a changed-lineage 409 with a usable snapshot cursor makes one resume attempt and keeps state", async () => {
    const { fetch, requests } = fakeFetch([
      vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
      () => conflict(otherHandle),
      () => redirect(otherHandle, "2"),
      () => log({ target: otherHandle, next: "2", upToDate: true }),
    ]);
    const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(3) });
    await subscription.done;
    expect(requests[4]?.url.searchParams.get("cursor")).toBe(snapshotResume);
    expect(requests[4]?.url.searchParams.has("handle")).toBe(false);
    expect(requests[5]?.url.searchParams.get("handle")).toBe(otherHandle);
    expect(subscription.state).toEqual(snapshotDocuments);
  });

  test("the lineage comes from the TB-Shape-Handle header even when the body names nothing", async () => {
    const { fetch, requests } = fakeFetch([
      vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
      () => conflictHeaderOnly(otherHandle),
      () => redirect(otherHandle, "2"),
      () => log({ target: otherHandle, next: "2", upToDate: true }),
    ]);
    const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(3) });
    await subscription.done;
    expect(requests[4]?.url.searchParams.get("cursor")).toBe(snapshotResume);
  });

  test("a body that names a different handle than the header fails closed to a bootstrap", async () => {
    const { fetch, requests } = fakeFetch([
      vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
      () => conflict(otherHandle, "h1-0000000000000000"),
      vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
    ]);
    const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(5) });
    await subscription.done;
    expect(requests[4]?.url.searchParams.has("cursor")).toBe(false);
    expect(requests[4]?.url.searchParams.has("handle")).toBe(false);
    expect(requests[4]?.url.searchParams.get("offset")).toBe("-1");
  });

  test("a failed resume attempt drops state and bootstraps without a token", async () => {
    const { fetch, requests } = fakeFetch([
      vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
      () => conflictHeaderOnly(otherHandle),
      () => conflict(otherHandle),
      vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
    ]);
    const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(5) });
    await subscription.done;
    expect(requests[4]?.url.searchParams.get("cursor")).toBe(snapshotResume);
    expect(requests[5]?.url.searchParams.has("cursor")).toBe(false);
    expect(requests[5]?.url.searchParams.has("handle")).toBe(false);
    expect(requests).toHaveLength(8);
  });

  test("an incomplete snapshot never resumes on another lineage", async () => {
    const { fetch, requests } = fakeFetch([
      vector("bootstrap"), vector("snapshot-first-page"),
      () => conflictHeaderOnly(otherHandle),
      vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
    ]);
    const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(4) });
    await subscription.done;
    expect(requests[3]?.url.searchParams.has("cursor")).toBe(false);
    expect(requests[3]?.url.searchParams.has("handle")).toBe(false);
  });

  test("429 shape_busy waits for Retry-After before the next attempt", async () => {
    vi.useFakeTimers();
    try {
      const { fetch, requests } = fakeFetch([
        () => json(429, { error: { code: "shape_busy", message: "busy" } }, { "retry-after": "2" }),
        vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
      ]);
      const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(2) });
      await vi.advanceTimersByTimeAsync(1999);
      expect(requests).toHaveLength(1);
      await vi.advanceTimersByTimeAsync(1);
      await subscription.done;
      expect(requests).toHaveLength(4);
    } finally {
      vi.useRealTimers();
    }
  });

  test("a huge Retry-After is waited in full, never fired early by timer overflow", async () => {
    vi.useFakeTimers();
    try {
      const { fetch, requests } = fakeFetch([
        () => json(429, { error: { code: "shape_busy", message: "busy" } }, { "retry-after": "999999999" }),
        vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
      ]);
      const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(2) });
      await vi.advanceTimersByTimeAsync(3 * 24 * 60 * 60 * 1000);
      expect(requests).toHaveLength(1);
      subscription.close();
      await expect(subscription.done).resolves.toBeUndefined();
      expect(requests).toHaveLength(1);
    } finally {
      vi.useRealTimers();
    }
  });

  test("503 unavailable continues beyond eight transient failures", async () => {
    vi.useFakeTimers();
    try {
      const unavailable = () => json(503, { error: { code: "unavailable", message: "later" } });
      const { fetch, requests } = fakeFetch([
        ...Array.from({ length: 9 }, () => unavailable),
        vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
      ]);
      const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(2) });
      await vi.runAllTimersAsync();
      await expect(subscription.done).resolves.toBeUndefined();
      expect(subscription.state).toEqual(snapshotDocuments);
      expect(requests).toHaveLength(12);
    } finally {
      vi.useRealTimers();
    }
  });

  test("503 during resume honors Retry-After and keeps the token, state and pending freshness", async () => {
    vi.useFakeTimers();
    try {
      const { fetch, requests } = fakeFetch([
        vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
        () => log({ next: "2", upToDate: true }),
        () => conflict(otherHandle),
        () => json(503, { error: { code: "unavailable", message: "later" } }, { "retry-after": "2" }),
        () => redirect(otherHandle, "2"),
        () => log({ target: otherHandle, next: "2", upToDate: true }),
      ]);
      /** @type {boolean[]} */
      const freshness = [];
      const subscription = subscribe(fetch, {}, { onUpdate: (/** @type {V1ShapeSubscription} */ current) => {
        freshness.push(current.upToDate);
        if (freshness.length === 5) current.close();
      } });
      await vi.advanceTimersByTimeAsync(1999);
      expect(requests).toHaveLength(6);
      expect(subscription.upToDate).toBe(false);
      expect(subscription.state).toEqual(snapshotDocuments);
      expect(freshness).toEqual([false, false, true, false]);
      await vi.advanceTimersByTimeAsync(1);
      await subscription.done;
      expect(requests[5]?.url.searchParams.get("cursor")).toBe(resume);
      expect(requests[6]?.url.searchParams.get("cursor")).toBe(resume);
      expect(subscription.state).toEqual(snapshotDocuments);
      expect(freshness).toEqual([false, false, true, false, true]);
    } finally {
      vi.useRealTimers();
    }
  });

  test("a throwing callback rejects done after the response has been committed", async () => {
    const failure = new Error("callback failed");
    const { fetch, requests } = fakeFetch([
      () => redirect(handle, "snap.2.0"),
      () => snapshot({ ops: operations("snapshot-first-page") }),
    ]);
    const subscription = subscribe(fetch, {}, { onUpdate: () => { throw failure; } });
    await expect(subscription.done).rejects.toBe(failure);
    expect(subscription.state.size).toBeGreaterThan(0);
    expect(requests).toHaveLength(2);
  });

  test("closing or aborting in a callback takes precedence over a later callback exception", async () => {
    const { fetch } = fakeFetch([() => redirect(handle, "snap.2.0"), () => snapshot()]);
    const subscription = subscribe(fetch, {}, { onUpdate: (/** @type {V1ShapeSubscription} */ current) => {
      current.close();
      throw new Error("after close");
    } });
    await expect(subscription.done).resolves.toBeUndefined();
  });

  test("a terminal failure is internally handled while remaining observable through done", async () => {
    const { fetch } = fakeFetch([() => json(401, { error: { code: "unauthorized", message: "no" } })]);
    const subscription = subscribe(fetch, {}, { onUpdate: () => {} });
    // Let an unhandled rejection surface before the consumer attaches its handler.
    await new Promise(resolve => setTimeout(resolve, 0));
    await expect(subscription.done).rejects.toMatchObject({ status: 401 });
  });

  test("terminal completion removes the external abort listener", async () => {
    const controller = new AbortController();
    const add = vi.spyOn(controller.signal, "addEventListener");
    const remove = vi.spyOn(controller.signal, "removeEventListener");
    const { fetch } = fakeFetch([() => json(401, { error: { code: "unauthorized", message: "no" } })]);
    const subscription = subscribe(fetch, {}, { signal: controller.signal });
    await expect(subscription.done).rejects.toMatchObject({ status: 401 });
    expect(remove).toHaveBeenCalledWith("abort", add.mock.calls[0]?.[1]);
  });

  test("terminal auth failures are never retried", async () => {
    const { fetch, requests } = fakeFetch([() => json(401, { error: { code: "unauthorized", message: "no" } })]);
    const subscription = subscribe(fetch);
    await expect(subscription.done).rejects.toMatchObject({ status: 401, code: "unauthorized" });
    expect(requests).toHaveLength(1);
  });

  test("a lost connection keeps state and resumes the same live position after backoff", async () => {
    vi.useFakeTimers();
    try {
      const update = { op: "upsert", id: "01ARZ3NDEKTSV4RRFFQ69G5FB0", version: "tbv1-def52df94d5598d237cf1184a4f5cb00", document: { title: "Updated", read: true } };
      const { fetch, requests } = fakeFetch([
        vector("bootstrap"), vector("snapshot-first-page"), vector("snapshot-last-page"),
        () => log({ next: "2", upToDate: true, cursor: "50" }),
        () => { throw new TypeError("fetch failed"); },
        () => new Response(new ReadableStream({ start(c) { c.error(new Error("reset")); } }), { status: 200, headers: { "content-type": "application/json" } }),
        () => log({ next: "4", upToDate: true, cursor: "51", ops: [update] }),
      ]);
      const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(5) });
      await vi.advanceTimersByTimeAsync(249);
      expect(requests).toHaveLength(5);
      await vi.advanceTimersByTimeAsync(1);
      expect(requests).toHaveLength(6);
      await vi.advanceTimersByTimeAsync(500);
      await subscription.done;
      for (const request of requests.slice(4)) {
        expect(request.url.searchParams.get("offset")).toBe("2");
        expect(request.url.searchParams.get("handle")).toBe(handle);
        expect(request.url.searchParams.get("cursor")).toBe("50");
      }
      expect(subscription.state.get(update.id)?.document).toEqual(update.document);
      expect(subscription.state.size).toBe(snapshotDocuments.size + (snapshotDocuments.has(update.id) ? 0 : 1));
    } finally {
      vi.useRealTimers();
    }
  });

  test("validation failures after a response are never retried", async () => {
    const { fetch, requests } = fakeFetch([() => new Response("<html>", { status: 307, headers: { location: "https://elsewhere.invalid/" } })]);
    const subscription = subscribe(fetch);
    await expect(subscription.done).rejects.toThrow("malformed shape redirect target");
    expect(requests).toHaveLength(1);
  });

  test("a browser follows its opaque bootstrap redirect and holds the final URL to the Location rules", async () => {
    const target = `https://shapes.test.invalid/v1/projects/${project}/databases/demo/collections/notes/shape?handle=${handle}&offset=snap.2.0`;
    /** @type {RequestInit[]} */
    const inits = [];
    const opaque = () => Object.defineProperties(new Response(null, { status: 200 }), { type: { value: "opaqueredirect" }, status: { value: 0 } });
    const { fetch } = fakeFetch([
      (_url, init) => { inits.push(init); return opaque(); },
      (url, init) => {
        inits.push(init);
        expect(url.searchParams.get("offset")).toBe("-1");
        return Object.defineProperties(vector("snapshot-first-page")(new URL(target)), { redirected: { value: true }, url: { value: target } });
      },
    ]);
    const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(1) });
    await subscription.done;
    expect(inits.map(init => init.redirect)).toEqual(["manual", "follow"]);
    expect(new Headers(inits[1]?.headers).get("authorization")).toBe(`Bearer ${token}`);
    expect(subscription.state.size).toBeGreaterThan(0);
  });

  test("a followed browser redirect to another origin is malformed", async () => {
    const opaque = () => Object.defineProperties(new Response(null, { status: 200 }), { type: { value: "opaqueredirect" }, status: { value: 0 } });
    const elsewhere = `https://elsewhere.invalid/v1/projects/${project}/databases/demo/collections/notes/shape?handle=${handle}&offset=snap.2.0`;
    const { fetch } = fakeFetch([opaque, () => Object.defineProperties(json(200, {}), { redirected: { value: true }, url: { value: elsewhere } })]);
    await expect(subscribe(fetch).done).rejects.toThrow("malformed shape redirect target");
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

  test("must_refetch discards the replica with the state", async () => {
    const replica = memoryReplica();
    await fillReplica(replica);
    const { fetch } = fakeFetch([() => conflict(otherHandle), () => conflict(otherHandle), hang]);
    let updates = 0;
    const subscription = subscribe(fetch, {}, { replica, onUpdate: (/** @type {V1ShapeSubscription} */ s) => { if (++updates === 2) s.close(); } });
    await subscription.done;
    expect(subscription.state.size).toBe(0);
    expect(replica.text).toBeUndefined();
  });

  test("a replica saved for another filter or in an unknown format is not restored", async () => {
    const replica = memoryReplica();
    await fillReplica(replica);
    const saved = replica.text;
    for (const [text, shape] of [[saved, { where: { eq: ["/read", true] } }], ["{\"format\":2}", {}], ["not json", {}]]) {
      replica.text = /** @type {string} */ (text);
      const { fetch, requests } = fakeFetch([hang]);
      const subscription = subscribe(fetch, shape, { replica });
      await vi.waitFor(() => expect(requests).toHaveLength(1));
      expect(requests[0]?.url.searchParams.get("offset")).toBe("-1");
      expect(subscription.state.size).toBe(0);
      subscription.close();
      await subscription.done;
    }
  });

  test("close cancels a pending read and resolves done without a further request", async () => {
    const { fetch, requests } = fakeFetch([hang]);
    const subscription = subscribe(fetch);
    subscription.close();
    await expect(subscription.done).resolves.toBeUndefined();
    expect(requests).toHaveLength(0);
  });

  test("an aborted external signal closes the subscription", async () => {
    const { fetch, requests } = fakeFetch([hang]);
    const controller = new AbortController();
    const subscription = subscribe(fetch, {}, { signal: controller.signal });
    controller.abort();
    await expect(subscription.done).resolves.toBeUndefined();
    expect(requests).toHaveLength(0);
  });

  test("rejects unknown shape and option members before starting", () => {
    const { fetch, requests } = fakeFetch([]);
    expect(() => subscribe(fetch, { where: undefined, extra: 1 })).toThrow(/Unknown shape member extra/u);
    expect(() => subscribe(fetch, {}, { onUpdate: undefined, bogus: true })).toThrow(/Unknown shape subscribe option member bogus/u);
    expect(() => subscribe(fetch, { where: { eq: ["/n"] } })).toThrow(TypeError);
    expect(requests).toHaveLength(0);
  });

  test("captures the filter exactly, so caller mutation and large integers cannot change the request", async () => {
    const { fetch, requests } = fakeFetch([hang]);
    const where = /** @type {any} */ (parseExactJson('{"eq":["/n",9007199254740993]}'));
    const subscription = subscribe(fetch, { where });
    where.eq[0] = "/other";
    await vi.waitFor(() => expect(requests).toHaveLength(1));
    subscription.close();
    await subscription.done;
    expect(requests[0]?.url.searchParams.get("where")).toBe('{"eq":["/n",9007199254740993]}');
  });

  test("state is a copy and its documents are frozen", async () => {
    const { fetch } = fakeFetch([() => redirect(handle, "snap.2.0"), () => snapshot({ ops: [{ op: "upsert", id: "01ARZ3NDEKTSV4RRFFQ69G5FB0", version: "tbv1-b803db62170176ffadaa0ee88b8cb332", document: { title: "Hi", read: false } }] })]);
    const subscription = subscribe(fetch, {}, { onUpdate: stopAfter(1) });
    await subscription.done;
    subscription.state.clear();
    expect(subscription.state.size).toBe(1);
    expect(Object.isFrozen(subscription.state.get("01ARZ3NDEKTSV4RRFFQ69G5FB0")?.document)).toBe(true);
  });
});
