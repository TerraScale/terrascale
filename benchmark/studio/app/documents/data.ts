// Server-only typed data loaders for the documents routes (DB-03).
//
// Every read goes through the generated typed client — never raw fetch, never
// engine internals. The loaders are thin: one typed operation call plus typed
// row parsing from the shared vocabulary, so the routes stay honest about
// exactly which operations a view used. Host-enforced session failures are
// honored through the same typed session.unauthenticated error as the DB-02
// bootstrap; every other typed failure is a designed, rendered route.

import { redirect } from "next/navigation";

import {
  TerrabaseApiError,
  type CollectionSummary,
  type DocumentRecord,
  type TerrabaseStudioClient,
} from "@/api/generated/client";
import {
  COLLECTIONS_QUERY_TEXT,
  DOCUMENT_PAGE_SIZE,
  documentPageQueryText,
  parseCollectionSummaries,
  parseDocumentRows,
  type PageWindow,
} from "@/components/documents/vocabulary";
import { resolveLoginUrl } from "@/lib/server/session";

export interface CollectionsLoad {
  readonly kind: "collections";
  readonly summaries: CollectionSummary[];
  readonly malformed: number;
}

/** Loads the collections catalog through the typed `query.execute` read. */
export async function loadCollections(client: TerrabaseStudioClient): Promise<CollectionsLoad> {
  const result = await client.queryExecute({
    input: { kind: "v1", text: COLLECTIONS_QUERY_TEXT },
  });
  const parsed = parseCollectionSummaries(result);
  return { kind: "collections", summaries: parsed.summaries, malformed: parsed.malformed };
}

export interface DocumentPageLoad {
  readonly rows: DocumentRecord[];
  readonly malformed: number;
  readonly matched: number;
}

/** Loads one page window of a collection's documents through the typed query. */
export async function loadDocumentPage(
  client: TerrabaseStudioClient,
  collection: string,
  pageWindow: PageWindow = { limit: DOCUMENT_PAGE_SIZE, offset: 0 },
): Promise<DocumentPageLoad> {
  const result = await client.queryExecute({
    input: { kind: "v1", text: documentPageQueryText(collection, pageWindow) },
  });
  const parsed = parseDocumentRows(result);
  return {
    rows: parsed.documents,
    malformed: parsed.malformed,
    matched: Number.isFinite(result.matched) ? Math.max(0, Math.trunc(result.matched)) : 0,
  };
}

export type DocumentLoad =
  | { readonly kind: "found"; readonly document: DocumentRecord }
  | { readonly kind: "missing" };

/** Loads one document through the typed `document.read` operation. */
export async function loadDocument(
  client: TerrabaseStudioClient,
  collection: string,
  documentId: string,
): Promise<DocumentLoad> {
  const result = await client.documentRead({ collection, documentId });
  if (result.document === null || result.document === undefined) {
    return { kind: "missing" };
  }
  return { kind: "found", document: result.document };
}

/**
 * Classifies a failure for the documents routes: the host-enforced session
 * gate redirects exactly where the host says, every other typed failure is
 * returned as the designed error route's payload, and unexpected errors
 * escalate to the segment's error boundary.
 */
export function documentsRouteFailure(
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
