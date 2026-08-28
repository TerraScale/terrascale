// Server-only typed data loaders for the schemas routes (SM-04).
//
// Every read goes through the generated typed client — never raw fetch, never
// engine internals. The loaders are thin: one typed `query.execute` for the
// collections catalog (the same read the documents pages use) plus one typed
// `schema.status` per collection, bounded, so the route stays honest about
// exactly which operations a view used. Host-enforced session failures are
// honored through the same typed session.unauthenticated error as the DB-02
// bootstrap; every other typed failure is a designed, rendered row or route.

import { redirect } from "next/navigation";

import { TerrabaseApiError, type TerrabaseStudioClient } from "@/api/generated/client";
import {
  COLLECTIONS_QUERY_TEXT,
  parseCollectionSummaries,
} from "@/components/documents/vocabulary";
import type { CollectionSchemaRow } from "@/components/schemas/CollectionsSchemaTable";
import { SCHEMA_COLLECTIONS_BOUND, parseSchemaStateResult } from "@/components/schemas/vocabulary";
import { resolveLoginUrl } from "@/lib/server/session";

export interface SchemaCatalogLoad {
  readonly rows: readonly CollectionSchemaRow[];
  readonly truncated: boolean;
  /** Catalog rows that were not the typed collection summary shape. */
  readonly malformedCatalogRows: number;
}

/**
 * Loads the collections catalog and every listed collection's schema state:
 * one typed `query.execute` (`FROM collections`) plus one typed
 * `schema.status` per collection, bounded to {@link SCHEMA_COLLECTIONS_BOUND}
 * reads so one page load cannot fan out unbounded. A collection whose read
 * fails or whose answer is not the contract's shape becomes an honest row,
 * never a guessed state and never a silent omission.
 */
export async function loadSchemaCatalog(client: TerrabaseStudioClient): Promise<SchemaCatalogLoad> {
  const catalog = await client.queryExecute({
    input: { kind: "v1", text: COLLECTIONS_QUERY_TEXT },
  });
  const parsed = parseCollectionSummaries(catalog);
  const bounded = parsed.summaries.slice(0, SCHEMA_COLLECTIONS_BOUND);

  const rows: CollectionSchemaRow[] = [];
  for (const summary of bounded) {
    try {
      const result = await client.schemaStatus({ collection: summary.name });
      const state = parseSchemaStateResult(result);
      rows.push({
        name: summary.name,
        documents: summary.documents,
        sizeBytes: summary.sizeBytes,
        state: state.kind === "recognized" ? state.state : null,
        failureCode: state.kind === "recognized" ? null : "schema.state-unrecognized",
        malformed: state.kind === "recognized" ? state.malformed : 0,
      });
    } catch (error) {
      if (error instanceof TerrabaseApiError) {
        // The session gate is a route-level concern, not a row-level one.
        if (error.code === "session.unauthenticated") {
          throw error;
        }
        rows.push({
          name: summary.name,
          documents: summary.documents,
          sizeBytes: summary.sizeBytes,
          state: null,
          failureCode: error.code,
          malformed: 0,
        });
        continue;
      }
      throw error;
    }
  }

  return {
    rows,
    truncated: parsed.summaries.length > bounded.length,
    malformedCatalogRows: parsed.malformed,
  };
}

/**
 * Classifies a failure for the schemas routes: the host-enforced session gate
 * redirects exactly where the host says, every other typed failure is
 * returned as the designed error route's payload, and unexpected errors
 * escalate to the segment's error boundary.
 */
export function schemasRouteFailure(
  error: unknown,
  client: TerrabaseStudioClient,
): TerrabaseApiError {
  if (error instanceof TerrabaseApiError) {
    if (
      error.code === "session.unauthenticated" &&
      error.loginUrl !== null &&
      error.loginUrl.length > 0
    ) {
      redirect(resolveLoginUrl(client.baseUrl, error.loginUrl));
    }
    return error;
  }
  throw error;
}
