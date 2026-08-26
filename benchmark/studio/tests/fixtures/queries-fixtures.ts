// Page-owned fixtures for the queries and indexes views (DB-05).
//
// These payloads mirror the studio API contract types
// (contract/terrabase-api.contract.json): QueryResult, QueryRowResult,
// Projection, StaleIndexObservation, and the ApiError vocabulary. They exist
// because the dev fixture host (scripts/fixture-server.mjs) answers
// query.execute with collection summaries only — it does not produce
// staleness observations, index catalog rows, or query failures — so the
// view models are proved against typed fixture payloads instead.
//
// Error codes and staleness reasons are host-authored strings. The studio
// renders them VERBATIM: nothing here may be reworded, mapped, or collapsed
// on the way to the operator. The codes below are illustrative until SE-08
// defines the host's mapping of the engine vocabulary (QueryError and
// RequestValidationError in src/query/ and src/operations.rs).

/** A QueryResult whose staleness report carries one stale and one rebuilding index. */
export const fixtureQueryResultWithStaleness = {
  rows: [
    {
      document: {
        id: "seed-001",
        collection: "studio-seed",
        version: 3,
        value: { kind: "studio-seed", label: "first" },
      },
      projected: [
        { name: "label", value: "first" },
        { name: "kind", value: "studio-seed" },
      ],
    },
    {
      document: {
        id: "seed-002",
        collection: "studio-seed",
        version: 1,
        value: { kind: "studio-seed", label: "second" },
      },
      projected: [{ name: "label", value: "second" }],
    },
  ],
  matched: 2,
  staleness: [
    {
      indexId: "Auto/studio-seed|by-label",
      reason: "observed generation 41 is newer than the used generation 39",
    },
    {
      indexId: "Auto/studio-seed|by-kind",
      reason: "rebuilding: epoch 3, generation 12 of 18 published",
    },
  ],
};

/** Rows an index-catalog read returns: fresh, stale, rebuilding, unknown, and one malformed row. */
export const fixtureIndexCatalogRows = [
  {
    document: {
      id: "index:Auto/studio-seed|by-label",
      collection: "__index_catalog__",
      version: 41,
      value: {
        namespace: "Auto/studio-seed|by-label",
        status: "stale",
        reason: "observed generation 41 is newer than the used generation 39",
      },
    },
    projected: [{ name: "namespace", value: "Auto/studio-seed|by-label" }],
  },
  {
    document: {
      id: "index:Auto/studio-seed|by-kind",
      collection: "__index_catalog__",
      version: 12,
      value: {
        namespace: "Auto/studio-seed|by-kind",
        status: "rebuilding",
        reason: "rebuilding: epoch 3, generation 12 of 18 published",
      },
    },
    projected: [{ name: "namespace", value: "Auto/studio-seed|by-kind" }],
  },
  {
    document: {
      id: "index:orders|by-status",
      collection: "__index_catalog__",
      version: 88,
      value: { namespace: "orders|by-status", status: "fresh" },
    },
    projected: [{ name: "namespace", value: "orders|by-status" }],
  },
  {
    document: {
      id: "index:events|by-ts",
      collection: "__index_catalog__",
      version: 7,
      value: { namespace: "events|by-ts", status: "warming-up" },
    },
    projected: [{ name: "namespace", value: "events|by-ts" }],
  },
  {
    // Malformed on purpose: no namespace. The catalog must count it as
    // unparsed rather than silently dropping it.
    document: {
      id: "index:not-a-catalog-row",
      collection: "__index_catalog__",
      version: 1,
      value: { status: "fresh" },
    },
    projected: [],
  },
];

/**
 * Typed error payloads the views must render unmodified. Codes are
 * illustrative of the three error paths named by the ticket: invalid query,
 * engine closed, and scope mismatch.
 */
export const fixtureTypedErrors = {
  invalidQuery: {
    code: "query.invalid-spec",
    message: "The typed query specification is invalid.",
    retryable: false,
    status: 400,
  },
  engineClosed: {
    code: "engine.closed",
    message: "The Engine has closed.",
    retryable: false,
    status: 503,
  },
  scopeMismatch: {
    code: "query.scope-mismatch",
    message: "The snapshot names another tenant than the query's tenant claim.",
    retryable: false,
    status: 409,
  },
};

// ---------------------------------------------------------------------------
// A page-owned stand-in host for the index catalog read.
//
// The dev fixture host (scripts/fixture-server.mjs, owned by DB-02) answers
// query.execute with collection summaries only: it has no index catalog rows,
// no staleness observations, and no query failures. Until the contract carries
// a typed catalog read and the host implements it, this stand-in serves the
// fixture payloads above through the same envelope discipline, so the catalog
// view's distinct stale/rebuilding rendering is provable end to end. It binds
// loopback, keeps no state, and speaks only the two operations the views use.
// ---------------------------------------------------------------------------

import { createServer } from "node:http";

/**
 * Starts a loopback host answering engine.status and the index catalog
 * query.execute with the fixture payloads. Returns the base URL.
 */
export async function startIndexCatalogHost() {
  const server = createServer(async (req, res) => {
    if (req.method !== "POST" || !req.url?.startsWith("/api/v2/operations/")) {
      res.writeHead(404, { "content-type": "application/json" });
      res.end(
        JSON.stringify({
          error: { code: "route.unknown", message: "not found", retryable: false },
        }),
      );
      return;
    }
    const chunks = [];
    for await (const chunk of req) {
      chunks.push(chunk);
    }
    const body = JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
    const op = req.url.slice("/api/v2/operations/".length);
    const result =
      op === "engine.status"
        ? {
            engineId: "index-catalog-fixture",
            engineVersion: "0.0.0-fixture",
            role: "single",
            startedAt: new Date().toISOString(),
            uptimeMs: 0,
            storage: { collections: 1, documents: 1, walBytes: 512 },
            counters: { reads: 0, writes: 0, queries: 0, familyOperations: 0 },
          }
        : op === "query.execute"
          ? {
              rows: fixtureIndexCatalogRows,
              matched: fixtureIndexCatalogRows.length,
              staleness: fixtureQueryResultWithStaleness.staleness,
            }
          : null;
    if (result === null) {
      res.writeHead(404, { "content-type": "application/json" });
      res.end(
        JSON.stringify({
          v: 2,
          op,
          correlationId: body.correlationId ?? null,
          error: {
            code: "operation.unknown",
            message: `Operation '${op}' is not part of the stand-in.`,
            retryable: false,
          },
        }),
      );
      return;
    }
    res.writeHead(200, { "content-type": "application/json", "cache-control": "no-store" });
    res.end(JSON.stringify({ v: 2, op, correlationId: body.correlationId ?? null, result }));
  });
  await new Promise<void>((resolve) => {
    server.listen({ port: 0, host: "127.0.0.1" }, () => resolve());
  });
  const address = server.address();
  if (address === null || typeof address === "string") {
    server.close();
    throw new Error("the stand-in host did not allocate a port");
  }
  const url = `http://127.0.0.1:${address.port}`;
  return {
    url,
    async close() {
      await new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}
