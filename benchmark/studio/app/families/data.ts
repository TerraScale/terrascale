// Server-only typed data loaders for the data-families routes (DB-06).
//
// Every read goes through the generated typed client — never raw fetch, never
// engine internals. The loaders are thin: one typed `family.read` (or
// `document.read` for the scope identity) plus typed state parsing from the
// shared family vocabulary, so the routes stay honest about exactly which
// operations a view used. Host-enforced session failures are honored through
// the same typed session.unauthenticated error as the DB-02 bootstrap; every
// other typed failure is a designed, rendered route.

import { redirect } from "next/navigation";

import { TerrabaseApiError, type Json, type TerrabaseStudioClient } from "@/api/generated/client";
import { resolveLoginUrl } from "@/lib/server/session";
import {
  FAMILY_ENTRY_PAGE_SIZE,
  attachmentEntriesScope,
  counterEntriesScope,
  parseAttachmentFamilyState,
  parseCounterFamilyState,
  parseTimeSeriesCatalogState,
  parseTimeSeriesPageState,
  timeSeriesCatalogScope,
  timeSeriesEntriesScope,
  type AttachmentFamilyState,
  type CounterFamilyState,
  type FamilyKindWire,
  type FamilyStateParse,
  type TimeSeriesCatalogState,
  type TimeSeriesPageState,
  type TimeSeriesRangeSelection,
} from "@/components/families/vocabulary";

/**
 * Reads one family state through the typed `family.read` operation, failing
 * closed when the host's echo does not name the requested kind and scope.
 */
export async function readFamilyState(
  client: TerrabaseStudioClient,
  kind: FamilyKindWire,
  scope: string,
): Promise<Json> {
  const result = await client.familyRead({ kind, scope });
  if (result.kind !== kind) {
    throw new TerrabaseApiError(
      "family.kind-mismatch",
      `The family read sent for kind ${kind} was answered as kind ${JSON.stringify(result.kind)}.`,
      { retryable: false },
    );
  }
  if (result.scope !== scope) {
    throw new TerrabaseApiError(
      "family.scope-mismatch",
      `The family read for kind ${kind} was answered for a different scope than the one requested.`,
      { retryable: false },
    );
  }
  return result.state;
}

/** Loads the first window of a document's counter group. */
export async function loadCountersWindow(
  client: TerrabaseStudioClient,
  collection: string,
  documentId: string,
): Promise<FamilyStateParse<CounterFamilyState>> {
  const scope = counterEntriesScope(collection, documentId, {
    limit: FAMILY_ENTRY_PAGE_SIZE,
    offset: 0,
  });
  return parseCounterFamilyState(await readFamilyState(client, "counters", scope));
}

/** Loads the first window of a document's attachment metadata. */
export async function loadAttachmentsWindow(
  client: TerrabaseStudioClient,
  collection: string,
  documentId: string,
): Promise<FamilyStateParse<AttachmentFamilyState>> {
  const scope = attachmentEntriesScope(collection, documentId, {
    limit: FAMILY_ENTRY_PAGE_SIZE,
    offset: 0,
  });
  return parseAttachmentFamilyState(await readFamilyState(client, "attachments", scope));
}

/** Loads a document's time-series catalog. */
export async function loadTimeSeriesCatalog(
  client: TerrabaseStudioClient,
  collection: string,
  documentId: string,
): Promise<FamilyStateParse<TimeSeriesCatalogState>> {
  const scope = timeSeriesCatalogScope(collection, documentId);
  return parseTimeSeriesCatalogState(await readFamilyState(client, "time-series", scope));
}

/** Loads one window of one series' entries, optionally over a selected range. */
export async function loadTimeSeriesWindow(
  client: TerrabaseStudioClient,
  collection: string,
  documentId: string,
  seriesName: string,
  range: TimeSeriesRangeSelection | null,
  offset: number,
): Promise<FamilyStateParse<TimeSeriesPageState>> {
  const scope = timeSeriesEntriesScope(
    collection,
    documentId,
    seriesName,
    { limit: FAMILY_ENTRY_PAGE_SIZE, offset },
    range,
  );
  return parseTimeSeriesPageState(await readFamilyState(client, "time-series", scope));
}

export type DocumentScopeLoad =
  | {
      readonly kind: "found";
      readonly collection: string;
      readonly documentId: string;
      readonly version: number;
    }
  | { readonly kind: "missing" };

/** Loads the scoped document's identity through the typed `document.read`. */
export async function loadDocumentScope(
  client: TerrabaseStudioClient,
  collection: string,
  documentId: string,
): Promise<DocumentScopeLoad> {
  const result = await client.documentRead({ collection, documentId });
  if (result.document === null || result.document === undefined) {
    return { kind: "missing" };
  }
  return {
    kind: "found",
    collection: result.document.collection,
    documentId: result.document.id,
    version: result.document.version,
  };
}

/**
 * Classifies a failure for the families routes: the host-enforced session
 * gate redirects exactly where the host says, every other typed failure is
 * returned as the designed error route's payload, and unexpected errors
 * escalate to the segment's error boundary.
 */
export function familiesRouteFailure(
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
