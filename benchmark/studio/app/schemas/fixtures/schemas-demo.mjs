// Schema-surface demo fixture for the studio schemas page (SM-04,
// TerraBase/TerraBaseV2#28).
//
// The page's one typed read is `schema.status` — PublicOperation::
// Schema(SchemaOperation::Status), the operation SM-04 added to the engine's
// closed registry, the TBEP registry, and the studio contract. The shared
// fixture core (DB-02) predates that operation and answers it with the
// designed `operation.unknown`, which is exactly the honest pre-integration
// state; this module composes the UNMODIFIED core with exactly that one gap
// filled, the same shape the families fixture (DB-06) established for
// per-document family state and the topology route namespace (DB-07)
// established for its reads. When the engine-backed host integration lands,
// STUDIO_API_BASE_URL flips to the real host and this fixture retires.
//
// The states served are the engine's `MigrationStatus` projection
// (src/families/migrations/record.rs): every kind of the closed set is
// seeded — a completed drive, a forward drive applying pages, a rollback
// drive restoring pages, and collections with no record — and an unknown
// collection answers schema-free, because the engine's absent-record read is
// `MigrationStatus::None` with no recorded policy, never an error.
//
// Nothing here is engine machinery and nothing here makes a production
// claim: the drive facts below are fabricated demo values shaped exactly like
// the engine's public status fields (manifest digest, from/to versions,
// documents scanned, documents transformed, pages committed).
//
// Plain Node, no dependencies. Run directly with:
//   node app/schemas/fixtures/schemas-demo.mjs
// (SCHEMAS_FIXTURE_HOST / SCHEMAS_FIXTURE_PORT, defaults 127.0.0.1:3035)

import { createServer } from "node:http";
import { pathToFileURL } from "node:url";

import { createFixtureServer } from "../../../scripts/fixture-server.mjs";

const CONTRACT_VERSION = 2;
const OPERATIONS_PATH_PREFIX = "/api/v2/operations/";
const SCHEMA_ROUTE_PREFIXES = ["/api/v2/operations/schema.", "/api/v2/operations/migration."];

/** The schema-surface operations this fixture serves (SM-04 route namespace). */
export const SCHEMA_OPERATIONS = ["schema.status"];

/**
 * The demo schema state per collection. The drive facts mirror the engine's
 * `MigrationStatus` payload fields; the labels are the variant docs of
 * src/families/migrations/record.rs.
 */
export const SCHEMA_FIXTURE_COLLECTIONS = {
  // A completed drive: version 2 enforced, the recorded receipt's counts.
  users: {
    validation: "enforced",
    enforcedVersion: 2,
    ruleCount: 3,
    migrationKind: "complete",
    manifest: "sha256:5f2b91c0d8e4a7f1",
    from: 1,
    to: 2,
    documents: 128,
    transformed: 128,
    pages: 3,
    seedDocuments: 2,
  },
  // A forward drive applying pages: version 1 still enforced while it runs.
  events: {
    validation: "enforced",
    enforcedVersion: 1,
    ruleCount: 2,
    migrationKind: "in_progress",
    manifest: "sha256:9c41d0aa37be6018",
    from: 1,
    to: 2,
    documents: 4096,
    transformed: 1024,
    pages: 2,
    seedDocuments: 1,
  },
  // A rollback drive restoring pages: the to-version is being abandoned, so
  // the collection still enforces it until the rollback completes (the
  // engine's RollingBack carries manifest/from/to/pages only).
  audit: {
    validation: "enforced",
    enforcedVersion: 3,
    ruleCount: 4,
    migrationKind: "rolling_back",
    manifest: "sha256:1e77b3f0c2a95d84",
    from: 2,
    to: 3,
    pages: 1,
    seedDocuments: 1,
  },
  // A schema-free collection of the core's own seed data: no recorded policy,
  // no migration record.
  "studio-seed": {
    validation: "none",
    ruleCount: 0,
    migrationKind: "none",
  },
  // An enforced schema with no drive: the steady state between migrations.
  parity: {
    validation: "enforced",
    enforcedVersion: 1,
    ruleCount: 1,
    migrationKind: "none",
  },
};

function corsHeaders() {
  // Dev fixture only (mirrors the core): the studio app and the fixture live
  // on separate local ports during development.
  return {
    "access-control-allow-origin": "*",
    "access-control-allow-methods": "GET, POST, OPTIONS",
    "access-control-allow-headers": "content-type, last-event-id",
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

function fail(res, op, correlationId, code, message, retryable, status, extra = {}) {
  const failure = errorEnvelope(op, correlationId, code, message, retryable, status, extra);
  sendJson(res, failure.status, failure.body);
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

/** The `schema.status` result of one seed, omitting absent optional facts. */
function schemaStateResultOf(collection, seed) {
  const result = {
    collection,
    ruleCount: seed.ruleCount,
    validation: seed.validation,
    migrationKind: seed.migrationKind,
  };
  if (seed.validation === "enforced") {
    result.enforcedVersion = seed.enforcedVersion;
  }
  if (seed.migrationKind !== "none") {
    result.migrationManifest = seed.manifest;
    result.migrationFromVersion = seed.from;
    result.migrationToVersion = seed.to;
    if (seed.documents !== undefined) {
      result.migrationDocuments = seed.documents;
    }
    if (seed.transformed !== undefined) {
      result.migrationTransformed = seed.transformed;
    }
    if (seed.pages !== undefined) {
      result.migrationPages = seed.pages;
    }
  }
  return result;
}

/**
 * Creates the composed schemas fixture: the unmodified DB-02 core (already
 * listening on an ephemeral loopback port) fronted by this extension. The
 * demo collections are seeded through the core's own typed `document.write`,
 * so the collections catalog the page reads (`FROM collections`) lists them
 * with real counts; the caller listens on the returned wrapper `server`, and
 * `coreBaseUrl` is exposed for tests that also want to talk to the core.
 */
export function createSchemasFixtureServer(options = {}) {
  const host = options.host ?? "127.0.0.1";
  const core = createFixtureServer({ host, port: 0 });
  const coreServer = core.server;
  let coreBaseUrl = "";

  const state = {
    // The demo schema state (read-only facts shaped like the engine's
    // MigrationStatus projection).
    schemaBehavior: "ok", // "ok" | "fail"
    sessionGate: false, // mirrors the forwarded core control
    reads: 0,
    dispose() {
      core.state.dispose();
    },
  };

  async function proxyToCore(res, path, rawBody) {
    const response = await fetch(`${coreBaseUrl}${path}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: rawBody,
    });
    const body = await response.json();
    sendJson(res, response.status, body);
    return { status: response.status, body };
  }

  /** Seeds one demo collection's catalog presence through the core's typed write. */
  async function seedCollection(name, seed) {
    for (let index = 0; index < (seed.seedDocuments ?? 0); index += 1) {
      await fetch(`${coreBaseUrl}/api/v2/operations/document.write`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          v: CONTRACT_VERSION,
          op: "document.write",
          correlationId: `schemas-seed-${name}-${index}`,
          causationId: null,
          idempotencyKey: null,
          request: {
            operationId: `schemas-seed-${name}-${index}`,
            document: {
              collection: name,
              id: `seed-${String(index + 1).padStart(3, "0")}`,
              value: { kind: "schemas-demo", collection: name },
            },
          },
        }),
      });
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

      if (req.method === "GET" && path === "/api/v2/_fixture/schemas-health") {
        sendJson(res, 200, { ok: true, fixture: "schemas-demo" });
        return;
      }

      if (req.method === "POST" && path === "/api/v2/_fixture/schemas-control") {
        const body = JSON.parse((await readBody(req)) || "{}");
        // Forward the core-recognized controls verbatim (session gate,
        // status behaviors, resets); apply the schemas-fixture ones here.
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
        if (body.schemaBehavior === "ok" || body.schemaBehavior === "fail") {
          state.schemaBehavior = body.schemaBehavior;
        }
        if (body.reset === true) {
          state.schemaBehavior = "ok";
          state.sessionGate = false;
        }
        sendJson(res, 200, {
          ok: true,
          schemaBehavior: state.schemaBehavior,
          sessionGate: state.sessionGate,
        });
        return;
      }

      if (SCHEMA_ROUTE_PREFIXES.some((prefix) => path.startsWith(prefix))) {
        const op = path.slice(OPERATIONS_PATH_PREFIX.length);
        if (req.method !== "POST") {
          fail(res, op, null, "method.not-allowed", "Operations accept POST only.", false, 405);
          return;
        }
        const raw = await readBody(req);
        let body;
        try {
          body = JSON.parse(raw || "null");
        } catch {
          fail(res, op, null, "protocol.envelope-invalid", "Request body is not JSON.", false, 400);
          return;
        }
        const correlationId = body?.correlationId;
        if (body?.v !== CONTRACT_VERSION) {
          fail(
            res,
            op,
            typeof correlationId === "string" ? correlationId : null,
            "protocol.version-unsupported",
            `Envelope version ${JSON.stringify(body?.v)} is not supported; this host speaks v${CONTRACT_VERSION}.`,
            false,
            400,
          );
          return;
        }
        if (typeof correlationId !== "string" || correlationId.length === 0 || body.op !== op) {
          fail(
            res,
            op,
            typeof correlationId === "string" ? correlationId : null,
            "protocol.envelope-invalid",
            "Envelope requires v, op matching the route, and a non-empty correlationId.",
            false,
            400,
          );
          return;
        }
        if (!SCHEMA_OPERATIONS.includes(op)) {
          fail(
            res,
            op,
            correlationId,
            "operation.unknown",
            `Operation '${op}' is not part of terrabase.api.v2.`,
            false,
            404,
          );
          return;
        }

        // The core's host-enforced session gate applies to the schema route
        // exactly as it applies to the proxied operations.
        if (state.sessionGate || core.state.sessionGate) {
          fail(
            res,
            op,
            correlationId,
            "session.unauthenticated",
            "The host requires an operator session (fixture behavior: session-gate).",
            false,
            401,
            { loginUrl: "/fixture/login" },
          );
          return;
        }
        if (state.schemaBehavior === "fail" || core.state.statusBehavior === "status-fail") {
          fail(
            res,
            op,
            correlationId,
            "engine.unavailable",
            "The engine is not reachable (fixture behavior: status-fail).",
            true,
            503,
          );
          return;
        }

        const request = body.request;
        if (
          !isJsonObject(request) ||
          typeof request.collection !== "string" ||
          request.collection.length === 0
        ) {
          fail(
            res,
            op,
            correlationId,
            "request.invalid",
            "schema.status requires a collection string.",
            false,
            400,
          );
          return;
        }

        state.reads += 1;
        const seed = SCHEMA_FIXTURE_COLLECTIONS[request.collection];
        const result =
          seed === undefined
            ? // The engine's absent-record read: schema-free, no drive, never
              // an error (families::migrations::status answers None).
              {
                collection: request.collection,
                ruleCount: 0,
                validation: "none",
                migrationKind: "none",
              }
            : schemaStateResultOf(request.collection, seed);
        sendJson(res, 200, { v: CONTRACT_VERSION, op, correlationId, result });
        return;
      }

      if (path.startsWith(OPERATIONS_PATH_PREFIX)) {
        if (req.method !== "POST") {
          const op = path.slice(OPERATIONS_PATH_PREFIX.length);
          fail(res, op, null, "method.not-allowed", "Operations accept POST only.", false, 405);
          return;
        }
        await proxyToCore(res, path, await readBody(req));
        return;
      }

      fail(res, null, null, "route.unknown", `No route for ${req.method} ${path}.`, false, 404);
    } catch (error) {
      if (!res.headersSent) {
        fail(res, null, null, "fixture.internal", String(error?.message ?? error), false, 500);
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

  const ready = (async () => {
    await listenCore();
    // Seed the demo collections' catalog presence through the core's own
    // typed write, so the page's `FROM collections` read lists them.
    for (const [name, seed] of Object.entries(SCHEMA_FIXTURE_COLLECTIONS)) {
      await seedCollection(name, seed);
    }
    return coreBaseUrl;
  })();

  return {
    server,
    coreServer,
    coreState: core.state,
    state,
    /** Resolves once the embedded core is listening and seeded. */
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
  const schemas = createSchemasFixtureServer({
    host: process.env.SCHEMAS_FIXTURE_HOST ?? "127.0.0.1",
  });
  const port = Number(process.env.SCHEMAS_FIXTURE_PORT ?? 3035);
  const host = process.env.SCHEMAS_FIXTURE_HOST ?? "127.0.0.1";
  await schemas.ready();
  schemas.server.listen(port, host, () => {
    console.log(
      `[schemas-fixture] composed on http://${host}:${port} (core ${schemas.coreBaseUrl})`,
    );
    console.log(
      "[schemas-fixture] typed schema.status over the seeded migration states; every other operation proxied to the core",
    );
  });
  const shutdown = () => {
    schemas.state.dispose();
    schemas.server.closeAllConnections();
    schemas.coreServer.closeAllConnections();
    schemas.server.close(() => process.exit(0));
    schemas.coreServer.close(() => process.exit(0));
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}
