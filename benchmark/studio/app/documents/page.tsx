import type { Metadata } from "next";

import { CollectionsView } from "@/components/documents/CollectionsView";
import { DocumentsTypedError } from "@/components/documents/DocumentsTypedError";
import { createStudioClient } from "@/lib/server/studio-client";

import { documentsRouteFailure, loadCollections } from "./data";

// Documents: the collections listing (DB-03). The catalog arrives through the
// typed client's `query.execute` read (the `query-v1-ok` conformance text);
// loading and error states are real routes (loading.tsx streams during the
// in-flight read; typed failures render the designed documents error route,
// with error.tsx as the last-resort boundary); a host-enforced session gate
// redirects exactly where the host says.
//
// The route is dynamic: the typed read and the API base URL resolve per
// request against the API host (the dev fixture or SE-08), never
// prerendered into a stale build artifact.
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Documents — TerraBase Studio",
};

export default async function DocumentsPage() {
  const client = createStudioClient();
  try {
    const load = await loadCollections(client);
    return (
      <div className="grid gap-4">
        <div>
          <h1 className="text-xl font-semibold text-ink">Documents</h1>
          <p className="mt-1 max-w-2xl text-sm text-ink-muted">
            Browse collections, page through their documents, and open a document with its metadata
            — all through the typed client.
          </p>
        </div>
        <CollectionsView summaries={load.summaries} malformed={load.malformed} />
      </div>
    );
  } catch (error) {
    const typed = documentsRouteFailure(error, client);
    return (
      <DocumentsTypedError
        scope="collections"
        code={typed.code}
        message={typed.message}
        retryable={typed.retryable}
        retryHref="/documents"
      />
    );
  }
}
