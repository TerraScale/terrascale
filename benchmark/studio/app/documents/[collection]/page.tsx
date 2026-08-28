import type { Metadata } from "next";

import { DocumentListView } from "@/components/documents/DocumentListView";
import { DocumentsTypedError } from "@/components/documents/DocumentsTypedError";
import { DOCUMENT_PAGE_SIZE, collectionHref } from "@/components/documents/vocabulary";
import { createStudioClient, studioBackendContext } from "@/lib/server/studio-client";

import { documentsRouteFailure, loadDocumentPage } from "../data";

// One collection's documents (DB-03): the first page window arrives through
// the typed client's `query.execute` read (explicit LIMIT/OFFSET in the v1
// text grammar — the typed request has no structured paging field until
// SE-08), and the list island appends further pages incrementally through the
// same typed client: pagination never triggers a full route reload, and the
// retained window is bounded with an explicit indicator. Until the fixture
// core grows per-collection listing semantics (a shared-route change recorded
// as a collision action), this route honestly renders its empty state against
// the unmodified fixture; the documents fixture composes the core with them.
//
// DB-04: the list is live-enabled — the island subscribes to this
// collection's backbone changes and refreshes the loaded windows on events
// (no polling), surfacing the live state and any host-shed drops.
//
// The route is dynamic: the typed read and the API base URL resolve per
// request.
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Collection documents — TerraBase Studio",
};

export default async function CollectionDocumentsPage({
  params,
}: {
  readonly params: Promise<{ readonly collection: string }>;
}) {
  const { collection } = await params;
  const client = createStudioClient();
  try {
    const page = await loadDocumentPage(client, collection, {
      limit: DOCUMENT_PAGE_SIZE,
      offset: 0,
    });
    return (
      <div className="grid gap-4">
        <div>
          <h1 className="font-mono text-xl font-semibold text-ink">{collection}</h1>
          <p className="mt-1 max-w-2xl text-sm text-ink-muted">
            Documents in this collection, one page window at a time through the typed client.
          </p>
        </div>
        <DocumentListView
          collection={collection}
          initialRows={page.rows}
          matched={page.matched}
          malformed={page.malformed}
          apiBaseUrl={client.baseUrl}
          live={{ apiBaseUrl: client.baseUrl, backendContext: studioBackendContext() }}
        />
      </div>
    );
  } catch (error) {
    const typed = documentsRouteFailure(error, client);
    return (
      <DocumentsTypedError
        scope="documents"
        code={typed.code}
        message={typed.message}
        retryable={typed.retryable}
        retryHref={collectionHref(collection)}
      />
    );
  }
}
