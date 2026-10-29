import { describe, expect, test, vi } from "vitest";
import { TerraBaseV1Error } from "../src/v1/errors.js";
import { decryptSealedShapePage, readShapePage } from "../src/v1/shape-reader.js";
import { V1ShapeSubscription } from "../src/v1/shape-subscription.js";
import { V1Transport } from "../src/v1/transport.js";
import { shapeVectors, skippedShapeVectorIds, vectors } from "./support/shape-vectors.js";

// Every upstream http vector is either driven by a case below or named in `exclusions` with its reason.
// Steps are served in order; a request past the script ends the run and is recorded, never answered.

/** @type {Record<string, string>} */
const exclusions = {
  "presigned-manifest": "Owner deferred manifest/302 delivery to the FR-22 follow-up.",
};

const byId = new Map(shapeVectors.http_vectors.map((/** @type {any} */ vector) => [vector.id, vector]));
const project = shapeVectors.fixture.scope.project_id;
const token = "fixture.payload.signature";
const origin = "https://origin.example.invalid";
const params = { database: "demo", collection: "notes" };
const handle = "h1-f3171a2230109328";
const otherHandle = "h1-d90dddd41bd8282d";
const ciphertextKeyId = "01ARZ3NDEKTSV4RRFFQ69G5FC0";

/** @param {string} id @returns {any} */
function vector(id) {
  const found = byId.get(id);
  if (found === undefined) throw new Error(`Unknown vector ${id}`);
  return found;
}

/** @param {string | { id?: string; replay?: string }} step @returns {string} */
const idOf = step => typeof step === "string" ? step : step.replay ?? step.id ?? "";

/** A response the SDK must answer with the vector's own response, though its request inputs differ. @param {string} id */
const replay = id => ({ replay: id });

/** @param {any} call @returns {Record<string, string>} */
const query = call => Object.fromEntries(call.url.searchParams);

/** @param {any} found @returns {Response} */
function respond({ response }) {
  const text = response.body_utf8 ?? (response.body == null ? null : JSON.stringify(response.body));
  return new Response(text, { status: response.status, headers: response.headers });
}

/** The SDK request a vector's query encodes. @param {any} found */
function requestFor({ request: { query: encoded = {} } }) {
  return {
    offset: encoded.offset,
    handle: encoded.handle,
    cursor: encoded.cursor,
    live: encoded.live === undefined ? undefined : encoded.live === "true",
  };
}

/** @param {typeof globalThis.fetch} fetch */
function transportFor(fetch) {
  return new V1Transport({ project, token, baseUrl: origin, fetch });
}

/** A request that settles only by abort, like a dropped connection. @param {AbortSignal | null | undefined} signal */
function hang(signal) {
  return new Promise((_resolve, reject) => {
    if (signal?.aborted) reject(signal.reason);
    else signal?.addEventListener("abort", () => reject(signal.reason), { once: true });
  });
}

/**
 * The SDK's request must be the vector's request on the wire: method, route, query and headers.
 * `dropped` lists vector headers the SDK must omit; `transport: "query"` expects the vector's header token in the query.
 * @param {any} found @param {URL} url @param {RequestInit} init @param {{ dropped?: string[]; transport?: string }} spec
 */
function assertWire(found, url, init, spec) {
  const { request } = found;
  const headers = new Headers(init.headers);
  const vectorHeaders = Object.entries(request.headers ?? {}).filter(([name]) => name !== "Authorization");
  expect(init.method ?? "GET").toBe(request.method);
  expect(url.pathname).toBe(request.path);
  expect(headers.get("authorization")).toBe(`Bearer ${token}`);
  if (spec.transport === "query") {
    const cursor = request.headers?.["TB-Shape-Resume"] ?? request.query.cursor;
    expect(query({ url })).toEqual({ ...request.query, cursor });
    expect(headers.has("tb-shape-resume")).toBe(false);
    return;
  }
  expect(query({ url })).toEqual(request.query ?? {});
  for (const [name, value] of vectorHeaders) {
    if (spec.dropped?.includes(name)) expect(headers.has(name)).toBe(false);
    else expect(headers.get(name)).toBe(value);
  }
}

/**
 * Serves the steps in order. A step is a vector id or `{ id, dropped, transport }`; `{ replay }` answers the
 * SDK's own request with another vector's response. A request past the script runs `onExhausted`, if given.
 * @param {any[]} steps @param {(() => void) | undefined} [onExhausted]
 */
function upstream(steps, onExhausted) {
  /** @type {{ url: URL; init: RequestInit }[]} */
  const calls = [];
  const queue = [...steps];
  /** @type {typeof globalThis.fetch} */
  const fetch = async (input, init = {}) => {
    const url = new URL(String(input));
    calls.push({ url, init });
    const step = queue.shift();
    if (step === undefined) {
      if (onExhausted === undefined) throw new Error(`Unexpected request ${url}`);
      onExhausted();
      return hang(init.signal);
    }
    const spec = typeof step === "string" ? { id: step } : step;
    if (spec.replay === undefined) assertWire(vector(spec.id), url, init, spec);
    return respond(vector(idOf(spec)));
  };
  return { fetch, calls };
}

/** @param {any} page @param {{ body: any; headers: Record<string, string> }} response */
function expectPage(page, { body, headers }) {
  expect({
    shapeId: page.shapeId, handle: page.handle, offset: page.offset, nextOffset: page.nextOffset, upToDate: page.upToDate,
    coveredOffset: page.coveredOffset, valuesAt: page.valuesAt, cursorResume: page.cursorResume, changes: page.changes,
    cursor: page.cursor, etag: page.etag,
  }).toEqual({
    shapeId: body.shape_id, handle: body.handle, offset: body.offset, nextOffset: body.next_offset, upToDate: body.up_to_date,
    coveredOffset: body.covered_offset, valuesAt: body.values_at, cursorResume: body.cursor_resume, changes: body.changes,
    cursor: headers["TB-Shape-Cursor"], etag: headers.ETag,
  });
}

/** A typed failure carries the vector's status, code, handle and cache headers. @param {unknown} error @param {any} found */
function expectTypedFailure(error, found) {
  const { status, headers, body } = found.response;
  expect(error).toBeInstanceOf(TerraBaseV1Error);
  expect(error).toMatchObject({ status, code: body?.error?.code ?? "not_modified", handle: body?.error?.handle });
  expect(/** @type {TerraBaseV1Error} */ (error).headers.get("cache-control")).toBe(headers["Cache-Control"] ?? null);
  expect(/** @type {TerraBaseV1Error} */ (error).headers.get("tb-shape-handle")).toBe(headers["TB-Shape-Handle"] ?? null);
}

/** The exact vector request through the named route: the typed error mapping on the wire. @param {any} found @returns {Promise<unknown>} */
async function namedFailure(found) {
  const route = found.request.path.endsWith("/shape/keys") ? "getShapeKey" : "readFreeShape";
  const fake = upstream([found.id]);
  const { query: encoded = {}, headers = {} } = found.request;
  const error = await transportFor(fake.fetch).call(route, params, {
    query: new URLSearchParams(encoded),
    headers: Object.fromEntries(Object.entries(headers).filter(([name]) => name !== "Authorization")),
  }).catch(caught => caught);
  expectTypedFailure(error, found);
  expect(fake.calls).toHaveLength(1);
  return error;
}

/** Runs the reader from one request and checks the final page against the last step's body. @param {any} found @param {any[]} steps */
async function readChain(found, steps, request = requestFor(found)) {
  const fake = upstream(steps);
  const page = await readShapePage(transportFor(fake.fetch), params, request, undefined);
  expect(fake.calls).toHaveLength(steps.length);
  expectPage(page, vector(idOf(steps.at(-1))).response);
  return fake;
}

/** The reader must refuse a request the client never sends: TypeError and no HTTP, then the server's typed answer. @param {any} found @param {(transport: V1Transport, found: any) => Promise<unknown>} attempt */
async function expectRefused(found, attempt) {
  const fake = upstream([]);
  const error = await attempt(transportFor(fake.fetch), found).catch(caught => caught);
  expect(error).toBeInstanceOf(TypeError);
  expect(fake.calls).toHaveLength(0);
  await namedFailure(found);
}

/** A reader request that the server answers with a typed failure. @param {any} found @param {any} request @param {any[]} [steps] */
async function expectReaderFailure(found, request = requestFor(found), steps = [found.id]) {
  const fake = upstream(steps);
  const error = await readShapePage(transportFor(fake.fetch), params, request, undefined).catch(caught => caught);
  expectTypedFailure(error, found);
  expect(fake.calls).toHaveLength(steps.length);
  return fake;
}

/** Runs a subscription until its script is exhausted, then closes it. @param {any[]} steps */
async function subscriptionRun(steps) {
  const fake = upstream(steps, () => subscription.close());
  const subscription = new V1ShapeSubscription(transportFor(fake.fetch), params, {}, {});
  await subscription.done;
  return { subscription, calls: fake.calls };
}

/** Reached by a bootstrap, two snapshot pages and the closed commit: a subscription sitting at offset 4. */
const closedCommit = ["bootstrap", "snapshot-first-page", "snapshot-last-page", "closed-commit-upsert-delete"];
/** Reached by a bootstrap and two snapshot pages: a subscription that will request offset 2. */
const snapshotDone = ["bootstrap", "snapshot-first-page", "snapshot-last-page"];
const followup = { id: "resume-header-preserved-on-followup", dropped: ["TB-Shape-Resume"] };

/** A reset or restart after a must-refetch on the subscription's own request. @param {any} subscription @param {any[]} calls @param {number} resumes Cursor requests made before the restart. */
function expectRestart(subscription, calls, resumes) {
  expect(query(calls.at(-1))).toEqual({ offset: "-1" });
  expect(calls.filter(call => call.url.searchParams.has("cursor"))).toHaveLength(resumes);
  expect(subscription.state.size).toBe(0);
}

/** @param {any} found @returns {Promise<void>} */
const restartAfterResumeRefusal = async found => {
  await expectReaderFailure(found);
  const { subscription, calls } = await subscriptionRun([...snapshotDone, "changed-machine-continuation-tries-resume-once", replay(found.id)]);
  expectRestart(subscription, calls, 1);
};

/** @param {any} found @returns {Promise<void>} */
const resetAfterSubscriptionRequest = async found => {
  await expectReaderFailure(found);
  const { subscription, calls } = await subscriptionRun([...closedCommit, replay(found.id)]);
  expectRestart(subscription, calls, 0);
};

const tigris = vector("tigris-sealed-ciphertext");
/** @param {string} hex */
function bytesFromHex(hex) {
  const bytes = new Uint8Array(hex.length / 2);
  for (let index = 0; index < bytes.length; index++) bytes[index] = Number.parseInt(hex.slice(index * 2, index * 2 + 2), 16);
  return bytes;
}
const sealed = bytesFromHex(tigris.response.body_hex);
/** @param {any} found epoch position from a key vector */
const positionFor = found => ({ handle: found.request.query.handle, offset: "2", epoch: found.request.query.epoch, keyId: found.request.query.key_id });

/** @param {any} found */
async function expectKeyFailure(found) {
  const fake = upstream([found.id]);
  const error = await decryptSealedShapePage(transportFor(fake.fetch), params, sealed, positionFor(found), undefined).catch(caught => caught);
  expectTypedFailure(error, found);
  expect(fake.calls).toHaveLength(1);
}

/** @type {Record<string, { action: string; run: (found: any) => Promise<unknown> }>} */
const cases = {
  "bootstrap": { action: "follow_redirect", run: found => readChain(found, ["bootstrap", "snapshot-first-page"]) },
  "snapshot-first-page": { action: "apply_then_continue_snapshot", run: found => readChain(found, ["snapshot-first-page"]) },
  "snapshot-last-page": { action: "apply_then_read_log", run: found => readChain(found, ["snapshot-last-page"]) },
  "closed-commit-upsert-delete": { action: "apply_whole_commit_then_continue", run: found => readChain(found, ["closed-commit-upsert-delete"]) },
  "live-timeout": {
    action: "poll_with_cursor_51",
    run: async found => {
      await readChain(found, ["live-timeout"]);
      // Replayed at the closed commit's offset: the subscription's next poll is live, from the header cursor.
      const { calls } = await subscriptionRun([...closedCommit, replay("live-timeout")]);
      expect(query(calls.at(-1))).toEqual({ handle, offset: "4", live: "true", cursor: "51" });
    },
  },
  "conditional-304": {
    action: "reuse_cached_body",
    run: async found => {
      // The SDK never sends If-None-Match, so it receives the unconditional 200 for the same request.
      const fake = await readChain(found, ["closed-commit-upsert-delete"]);
      expect(new Headers(fake.calls[0]?.init.headers).has("if-none-match")).toBe(false);
      const error = await namedFailure(found);
      expect(/** @type {TerraBaseV1Error} */ (error).etag).toBe(found.response.headers.ETag.slice(1, -1));
    },
  },
  "stale-handle": { action: "discard_state_then_bootstrap", run: found => resetAfterSubscriptionRequest(found) },
  "expired-offset": { action: "discard_state_then_bootstrap", run: found => resetAfterSubscriptionRequest(found) },
  "future-offset": { action: "discard_state_then_bootstrap", run: found => resetAfterSubscriptionRequest(found) },
  "snapshot-page-missing": {
    action: "discard_state_then_bootstrap",
    run: async found => {
      await expectReaderFailure(found);
      const { subscription, calls } = await subscriptionRun(["bootstrap", "snapshot-first-page", replay(found.id)]);
      expectRestart(subscription, calls, 0);
    },
  },
  "proven-cursor-resume-query": {
    action: "preserve_state_then_follow_local_lineage",
    run: found => readChain(found, ["proven-cursor-resume-query", followup]),
  },
  "proven-cursor-resume-header": {
    action: "preserve_state_then_follow_local_lineage",
    run: async found => {
      // Short tokens travel in the query; the header form is used only past the request-line limit (see large-resume-token-uses-header).
      await readChain(found, [{ id: found.id, transport: "query" }, followup], { ...requestFor(found), cursor: found.request.headers["TB-Shape-Resume"] });
    },
  },
  "resume-unproven-prefix": { action: "discard_state_then_bootstrap", run: restartAfterResumeRefusal },
  "resume-behind-destination": { action: "discard_state_then_bootstrap", run: restartAfterResumeRefusal },
  "resume-expired": { action: "discard_state_then_bootstrap", run: restartAfterResumeRefusal },
  "resume-other-collection": { action: "discard_state_then_bootstrap", run: restartAfterResumeRefusal },
  "resume-other-filter": { action: "discard_state_then_bootstrap", run: restartAfterResumeRefusal },
  "resume-other-generation": { action: "discard_state_then_bootstrap", run: restartAfterResumeRefusal },
  "resume-target-below-retention-floor": { action: "discard_state_then_bootstrap", run: restartAfterResumeRefusal },
  "resume-malformed-token": {
    action: "reject_request",
    run: found => expectRefused(found, (transport, value) => readShapePage(transport, params, requestFor(value), undefined)),
  },
  "resume-duplicate-transports": {
    action: "reject_request",
    run: async found => {
      // The SDK cannot send both transports: it sends the token in the query only, and the server's 400 maps to a typed error.
      const fake = upstream([{ id: found.id, transport: "query" }]);
      const error = await readShapePage(transportFor(fake.fetch), params, { offset: "-1", cursor: found.request.query.cursor }, undefined).catch(caught => caught);
      expectTypedFailure(error, found);
      await namedFailure(found);
    },
  },
  "resume-with-handle-refused": {
    action: "reject_request",
    run: found => expectRefused(found, (transport, value) => readShapePage(transport, params, requestFor(value), undefined)),
  },
  "resume-with-live-refused": {
    action: "reject_request",
    run: found => expectRefused(found, (transport, value) => readShapePage(transport, params, requestFor(value), undefined)),
  },
  "oversize-materialized-commit": {
    action: "preserve_state_and_offset",
    run: async found => {
      await expectReaderFailure(found);
      // The retry repeats the same offset and keeps the snapshot documents.
      const { subscription, calls } = await subscriptionRun([...snapshotDone, found.id]);
      expect(query(calls.at(-1))).toEqual({ handle, offset: "2" });
      expect(subscription.state.size).toBeGreaterThan(0);
    },
  },
  "large-resume-token-uses-header": {
    action: "preserve_full_token_and_state_then_follow_redirect",
    run: async found => {
      const controller = new AbortController();
      const fake = upstream([found.id], () => controller.abort());
      const attempt = readShapePage(transportFor(fake.fetch), params, { offset: "-1", cursor: found.request.headers["TB-Shape-Resume"] }, controller.signal);
      await expect(attempt).rejects.toThrow();
      // The 307 target has no upstream body, so the follow-up is checked on the wire and cancelled.
      expect(fake.calls).toHaveLength(2);
      expect(query(fake.calls[1])).toEqual({ handle: otherHandle, offset: "4" });
      expect(new Headers(fake.calls[1]?.init.headers).has("tb-shape-resume")).toBe(false);
    },
  },
  "resume-truncated-envelope": { action: "reject_request", run: async found => { await expectReaderFailure(found); } },
  "resume-bad-envelope-marker": { action: "reject_request", run: async found => { await expectReaderFailure(found); } },
  "resume-bad-inner-marker": { action: "reject_request", run: async found => { await expectReaderFailure(found); } },
  "resume-uncovered-row-above-sealed-head": {
    action: "preserve_state_then_redeliver_from_sealed_head",
    run: found => readChain(found, [found.id, followup]),
  },
  "resume-header-preserved-on-followup": {
    action: "ignore_leftover_resume_header_then_apply_chunk",
    run: found => readChain(found, [followup]),
  },
  "epoch-key": {
    action: "cache_key_in_memory_for_epoch",
    run: async found => {
      const clock = vi.spyOn(Date, "now").mockReturnValue(Number(found.request.query.epoch) * 600_000);
      try {
        const fake = upstream([found.id]);
        const transport = transportFor(fake.fetch);
        const page = await decryptSealedShapePage(transport, params, sealed, positionFor(found), undefined);
        await decryptSealedShapePage(transport, params, sealed, positionFor(found), undefined);
        expect(fake.calls).toHaveLength(1);
        expect(fake.calls[0]?.init.cache).toBe("no-store");
        expectPage(page, { body: tigris.expected_body, headers: {} });
      } finally { clock.mockRestore(); }
    },
  },
  "future-key-refused": { action: "do_not_prefetch_future_key", run: found => expectKeyFailure(found) },
  "retired-epoch-key-pair": { action: "discard_state_then_bootstrap", run: found => expectKeyFailure(found) },
  "key-id-required": {
    action: "reject_request",
    run: found => expectRefused(found, (transport, value) => decryptSealedShapePage(transport, params, sealed, positionFor(value), undefined)),
  },
  "bootstrap-with-handle-refetch": {
    action: "discard_state_then_bootstrap",
    run: found => expectRefused(found, (transport, value) => readShapePage(transport, params, requestFor(value), undefined)),
  },
  "bootstrap-with-handle-ignores-resume-header": {
    action: "discard_state_then_bootstrap",
    run: found => expectRefused(found, (transport, value) => readShapePage(transport, params, requestFor(value), undefined)),
  },
  "changed-machine-continuation-tries-resume-once": {
    action: "try_one_cursor_resume_before_discarding_state",
    run: async found => {
      await expectReaderFailure(found);
      const { subscription, calls } = await subscriptionRun([...snapshotDone, found.id, "proven-cursor-resume-query", followup]);
      // One resume attempt, and its committed follow-up keeps the state (no reset).
      expect(calls.filter(call => call.url.searchParams.has("cursor"))).toHaveLength(1);
      expect(query(calls.at(-1))).toEqual({ handle: otherHandle, offset: "4" });
      expect(subscription.state.size).toBeGreaterThan(0);
    },
  },
  "resume-followup-second-machine-refetch-does-not-loop": {
    action: "discard_state_then_bootstrap_without_second_resume",
    run: async found => {
      await expectReaderFailure(found, requestFor(found), [{ id: found.id, dropped: ["TB-Shape-Resume"] }]);
      const { subscription, calls } = await subscriptionRun([...snapshotDone, "changed-machine-continuation-tries-resume-once", "proven-cursor-resume-query", { ...followup, id: found.id }]);
      expectRestart(subscription, calls, 1);
    },
  },
  "tigris-sealed-ciphertext": {
    action: "decrypt_then_apply",
    run: async () => {
      // No automatic ciphertext delivery yet: the object is passed in and only its named key is fetched.
      const fake = upstream(["epoch-key"]);
      const page = await decryptSealedShapePage(transportFor(fake.fetch), params, sealed, { handle, offset: "2", epoch: "100", keyId: ciphertextKeyId }, undefined);
      expectPage(page, { body: tigris.expected_body, headers: {} });
      expect(fake.calls).toHaveLength(1);
    },
  },
};

test("every upstream http vector is driven by a case or excluded with a reason", () => {
  const fixtureIds = shapeVectors.http_vectors.map((/** @type {any} */ vector) => vector.id).sort();
  expect([...Object.keys(cases), ...Object.keys(exclusions)].sort()).toEqual(fixtureIds);
  expect(Object.keys(cases).sort()).toEqual(vectors("http_vectors").map((/** @type {any} */ vector) => vector.id).sort());
  expect(new Set(Object.keys(exclusions))).toEqual(skippedShapeVectorIds);
});

describe("upstream http vectors over fake HTTP", () => {
  test.each(Object.entries(cases))("%s", async (id, { action, run }) => {
    const found = vector(id);
    expect(found.expected_action).toBe(action);
    await run(found);
  });
});

describe("vector identity", () => {
  test("the fixture carries the scope every request path was generated from", () => {
    expect(project).toBe("01ARZ3NDEKTSV4RRFFQ69G5FAW");
    expect(vector("bootstrap").request.path).toContain(`/v1/projects/${project}/`);
  });
});
