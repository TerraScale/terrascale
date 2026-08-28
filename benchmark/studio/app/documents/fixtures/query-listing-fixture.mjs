// Test-only fixture extension for the DB-03 documents pages.
//
// The DB-02 fixture core (scripts/fixture-server.mjs) is read-only for this
// ticket and serves exactly one query shape pre-SE-08: the collections
// catalog (`FROM collections`). The documents pages additionally list
// documents per collection through the same typed `query.execute` operation
// with explicit LIMIT/OFFSET windows in the v1 text grammar — semantics the
// real SE-08 C# API host will own. Until the fixture core grows them (a
// shared-route change recorded as a collision action for the integrator),
// this module composes the UNMODIFIED core with exactly that gap filled:
//
//   - `FROM collections` and every other query text proxy to the core, so
//     catalog behavior stays the core's own;
//   - `FROM <collection> LIMIT <n> OFFSET <m>` answers from documents written
//     through this fixture's typed `document.write` (forwarded to the core, so
//     `document.read` serves the same data), ordered by ascending document id
//     in code-unit order, deterministically;
//   - fixture controls: queryBehavior ok|fail, queryDelayMs, readDelayMs, and
//     the core's own controls (session-gate, reset) forwarded verbatim.
//
// Plain Node, no dependencies. Run directly with:
//   node app/documents/fixtures/query-listing-fixture.mjs
// (DOCUMENTS_FIXTURE_HOST / DOCUMENTS_FIXTURE_PORT, defaults 127.0.0.1:3033)

import { createServer } from "node:http";
import { Readable } from "node:stream";
import { pathToFileURL } from "node:url";

import { createFixtureServer } from "../../../scripts/fixture-server.mjs";

const CONTRACT_VERSION = 2;
const PAGE_QUERY_PATTERN = /^FROM (.+) LIMIT (\d+) OFFSET (\d+)$/;
const MAX_PAGE_LIMIT = 500;
const OPERATIONS_PATH_PREFIX = "/api/v2/operations/";

function corsHeaders() {
  // Dev fixture only (mirrors the core): the studio app and the fixture live
  // on separate local ports during development. The header allowlist
  // includes the backend-context headers the DB-04 live stream's gate
  // requires (mirroring the core fixture).
  return {
    "access-control-allow-origin": "*",
    "access-control-allow-methods": "GET, POST, OPTIONS",
    "access-control-allow-headers": "content-type, last-event-id, x-tb-principal, x-tb-tenant",
  };
}

function sendJson(res, status, body, extraHeaders = {}) {
  const payload = JSON.stringify(body);
  res.writeHead(status, {
    "content-type": "application/json",
    "cache-control": "no-store",
    ...corsHeaders(),
    ...extraHeaders,
  });
  res.end(payload);
}

function errorEnvelope(op, correlationId, code, message, retryable, status, extra = {}) {
  return {
    status,
    body: {
      v: CONTRACT_VERSION,
      op,
      correlationId,
      error: { code, message, retryable, ...extra },
    },
  };
}

async function readBody(req, limitBytes = 1024 * 1024) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > limitBytes) {
      throw new Error("body too large");
    }
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString("utf8");
}

function isJsonObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Creates the composed documents fixture: the unmodified DB-02 core (already
 * listening on an ephemeral loopback port) fronted by this extension. The
 * caller listens on the returned wrapper `server`; `coreBaseUrl` is exposed
 * for tests that also want to talk to the core directly.
 */
export function createDocumentsFixtureServer(options = {}) {
  const host = options.host ?? "127.0.0.1";
  const core = createFixtureServer({ host, port: 0 });
  const coreServer = core.server;
  let coreBaseUrl = "";

  const state = {
    // Documents written through this fixture's typed document.write — the
    // same data the core serves through document.read.
    documents: new Map(), // collection -> Map(id -> { value, version })
    queryBehavior: "ok", // "ok" | "fail"
    queryDelayMs: 0,
    readDelayMs: 0,
    sessionGate: false, // mirrors the forwarded core control, for intercepted routes
    dispose() {
      core.state.dispose();
    },
  };

  async function proxyToCore(res, path, rawBody, delayMs = 0) {
    if (delayMs > 0) {
      await sleep(delayMs);
    }
    const response = await fetch(`${coreBaseUrl}${path}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: rawBody,
    });
    const body = await response.json();
    sendJson(res, response.status, body);
    return { status: response.status, body };
  }

  function answerPage(res, op, correlationId, collection, limitText, offsetText) {
    const limit = Math.min(Math.max(Number.parseInt(limitText, 10) || 1, 1), MAX_PAGE_LIMIT);
    const offset = Math.max(Number.parseInt(offsetText, 10) || 0, 0);
    const docs = state.documents.get(collection) ?? new Map();
    // Deterministic listing order: ascending document id, code-unit order.
    const entries = [...docs.entries()].sort((a, b) => (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0));
    const matched = entries.length;
    const rows = entries.slice(offset, offset + limit).map(([id, document]) => ({
      document: { id, collection, version: document.version, value: document.value },
      projected: [],
    }));
    sendJson(res, 200, {
      v: CONTRACT_VERSION,
      op,
      correlationId,
      result: { rows, matched, staleness: [] },
    });
  }

  function recordWrite(requestBody, outcomeBody) {
    const document = isJsonObject(requestBody) ? requestBody.request?.document : undefined;
    const version = isJsonObject(outcomeBody) ? outcomeBody.result?.version : undefined;
    if (
      isJsonObject(document) &&
      typeof document.id === "string" &&
      typeof document.collection === "string" &&
      Number.isInteger(version)
    ) {
      let docs = state.documents.get(document.collection);
      if (docs === undefined) {
        docs = new Map();
        state.documents.set(document.collection, docs);
      }
      docs.set(document.id, { value: document.value ?? null, version });
    }
  }

  const server = createServer(async (req, res) => {
    const url = new URL(req.url ?? "/", `http://${req.headers.host ?? "localhost"}`);
    const path = url.pathname;

    try {
      if (req.method === "OPTIONS") {
        res.writeHead(204, corsHeaders());
        res.end();
        return;
      }

      if (req.method === "GET" && path === "/api/v2/_fixture/documents-health") {
        sendJson(res, 200, { ok: true, fixture: "documents-query-listing" });
        return;
      }

      // DB-04: the core owns the backbone changes stream; this fixture
      // composes the core, so the studio's live-rows subscription proxies
      // through verbatim — the SSE bytes, the id/event/data lines, and the
      // backend-context gate are all the core's own.
      if (req.method === "GET" && path === "/v1/streams") {
        const forwarded = { accept: "text/event-stream" };
        for (const header of ["x-tb-principal", "x-tb-tenant", "last-event-id"]) {
          const value = req.headers[header];
          if (typeof value === "string" && value.length > 0) {
            forwarded[header] = value;
          }
        }
        const upstream = await fetch(`${coreBaseUrl}/v1/streams${url.search ?? ""}`, {
          headers: forwarded,
        });
        if (upstream.status !== 200 || upstream.body === null) {
          const body = await upstream.text();
          sendJson(res, upstream.status, body.length > 0 ? JSON.parse(body) : {});
          return;
        }
        res.writeHead(200, {
          "content-type": "text/event-stream",
          "cache-control": "no-cache, no-transform",
          connection: "keep-alive",
          "x-accel-buffering": "no",
          ...corsHeaders(),
        });
        Readable.fromWeb(upstream.body).pipe(res);
        return;
      }

      if (req.method === "POST" && path === "/api/v2/_fixture/documents-control") {
        const body = JSON.parse((await readBody(req)) || "{}");
        // Forward the core-recognized controls verbatim (session gate,
        // status behaviors, resets); apply the documents-fixture ones here.
        await fetch(`${coreBaseUrl}/api/v2/_fixture/control`, {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify(body),
        }).catch(() => {
          // The core may be gone (a test may have killed the wrapper first);
          // the wrapper's own controls still apply.
        });
        if (body.behavior === "session-gate") {
          state.sessionGate = true;
        }
        if (body.behavior === "session-open" || body.behavior === "session-ungate") {
          state.sessionGate = false;
        }
        if (body.queryBehavior === "ok" || body.queryBehavior === "fail") {
          state.queryBehavior = body.queryBehavior;
        }
        if (Number.isFinite(body.queryDelayMs)) {
          state.queryDelayMs = Math.max(0, body.queryDelayMs);
        }
        if (Number.isFinite(body.readDelayMs)) {
          state.readDelayMs = Math.max(0, body.readDelayMs);
        }
        if (body.reset === true) {
          state.queryBehavior = "ok";
          state.queryDelayMs = 0;
          state.readDelayMs = 0;
          state.sessionGate = false;
        }
        sendJson(res, 200, {
          ok: true,
          queryBehavior: state.queryBehavior,
          queryDelayMs: state.queryDelayMs,
          readDelayMs: state.readDelayMs,
          sessionGate: state.sessionGate,
          collections: [...state.documents.keys()].sort(),
        });
        return;
      }

      if (path.startsWith(OPERATIONS_PATH_PREFIX)) {
        const op = path.slice(OPERATIONS_PATH_PREFIX.length);
        if (req.method !== "POST") {
          sendJson(
            res,
            405,
            errorEnvelope(
              op,
              null,
              "method.not-allowed",
              "Operations accept POST only.",
              false,
              405,
            ).body,
          );
          return;
        }
        const raw = await readBody(req);
        let body;
        try {
          body = JSON.parse(raw || "null");
        } catch {
          const failure = errorEnvelope(
            op,
            null,
            "protocol.envelope-invalid",
            "Request body is not JSON.",
            false,
            400,
          );
          sendJson(res, failure.status, failure.body);
          return;
        }
        const correlationId = body?.correlationId;
        if (body?.v !== CONTRACT_VERSION) {
          const failure = errorEnvelope(
            op,
            typeof correlationId === "string" ? correlationId : null,
            "protocol.version-unsupported",
            `Envelope version ${JSON.stringify(body?.v)} is not supported; this host speaks v${CONTRACT_VERSION}.`,
            false,
            400,
          );
          sendJson(res, failure.status, failure.body);
          return;
        }
        if (typeof correlationId !== "string" || correlationId.length === 0 || body.op !== op) {
          const failure = errorEnvelope(
            op,
            typeof correlationId === "string" ? correlationId : null,
            "protocol.envelope-invalid",
            "Envelope requires v, op matching the route, and a non-empty correlationId.",
            false,
            400,
          );
          sendJson(res, failure.status, failure.body);
          return;
        }

        if (op === "query.execute") {
          // The core's host-enforced session gate applies to intercepted
          // listings exactly as it applies to the proxied operations.
          if (state.sessionGate) {
            const failure = errorEnvelope(
              op,
              correlationId,
              "session.unauthenticated",
              "The host requires an operator session (fixture behavior: session-gate).",
              false,
              401,
              { loginUrl: "/fixture/login" },
            );
            sendJson(res, failure.status, failure.body);
            return;
          }
          if (state.queryBehavior === "fail") {
            const failure = errorEnvelope(
              op,
              correlationId,
              "query.unavailable",
              "The query could not be served (fixture behavior: query-fail).",
              true,
              503,
            );
            sendJson(res, failure.status, failure.body);
            return;
          }
          const text =
            body.request?.input?.kind === "v1" ? String(body.request.input.text ?? "") : "";
          const page = PAGE_QUERY_PATTERN.exec(text);
          if (page === null) {
            // The catalog and any other query text keep core behavior.
            await proxyToCore(res, path, raw, state.queryDelayMs);
            return;
          }
          if (state.queryDelayMs > 0) {
            await sleep(state.queryDelayMs);
          }
          answerPage(res, op, correlationId, page[1], page[2], page[3]);
          return;
        }

        if (op === "document.read" || op === "document.write") {
          const outcome = await proxyToCore(
            res,
            path,
            raw,
            op === "document.read" ? state.readDelayMs : 0,
          );
          if (op === "document.write" && outcome.status === 200) {
            recordWrite(body, outcome.body);
          }
          return;
        }

        await proxyToCore(res, path, raw);
        return;
      }

      const failure = errorEnvelope(
        null,
        null,
        "route.unknown",
        `No route for ${req.method} ${path}.`,
        false,
        404,
      );
      sendJson(res, failure.status, failure.body);
    } catch (error) {
      const failure = errorEnvelope(
        null,
        null,
        "fixture.internal",
        String(error?.message ?? error),
        false,
        500,
      );
      if (!res.headersSent) {
        sendJson(res, failure.status, failure.body);
      } else {
        res.end();
      }
    }
  });

  const listenCore = () =>
    new Promise((resolve) => {
      coreServer.listen(0, host, () => {
        const address = coreServer.address();
        const port = typeof address === "object" && address !== null ? address.port : 0;
        coreBaseUrl = `http://${host}:${port}`;
        resolve(undefined);
      });
    });

  const ready = listenCore();

  return {
    server,
    coreServer,
    coreState: core.state,
    state,
    /** Resolves once the embedded core is listening; proxying needs it. */
    async ready() {
      await ready;
      return coreBaseUrl;
    },
    get coreBaseUrl() {
      return coreBaseUrl;
    },
  };
}

const isDirectRun =
  process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isDirectRun) {
  const documents = createDocumentsFixtureServer({
    host: process.env.DOCUMENTS_FIXTURE_HOST ?? "127.0.0.1",
  });
  const port = Number(process.env.DOCUMENTS_FIXTURE_PORT ?? 3033);
  const host = process.env.DOCUMENTS_FIXTURE_HOST ?? "127.0.0.1";
  await documents.ready();
  documents.server.listen(port, host, () => {
    console.log(
      `[documents-fixture] composed on http://${host}:${port} (core ${documents.coreBaseUrl})`,
    );
    console.log(
      "[documents-fixture] per-collection listings for documents written through this fixture; catalog proxied to the core",
    );
  });
  const shutdown = () => {
    documents.state.dispose();
    documents.server.closeAllConnections();
    documents.coreServer.closeAllConnections();
    documents.server.close(() => process.exit(0));
    documents.coreServer.close(() => process.exit(0));
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}
