import type { Metadata } from "next";

import { DocumentMissing } from "@/components/documents/DocumentMissing";
import { DocumentViewer } from "@/components/documents/DocumentViewer";
import { DocumentsTypedError } from "@/components/documents/DocumentsTypedError";
import { documentHref } from "@/components/documents/vocabulary";
import { createStudioClient } from "@/lib/server/studio-client";

import { documentsRouteFailure, loadDocument } from "../../data";

// The document viewer (DB-03): one typed `document.read` per render — pretty
// JSON with metadata (identity, sequence as the published version, size),
// truncated at an explicit limit for large values. A missing document is the
// typed read's designed `null` outcome and renders its own route, not an
// error; typed failures render the designed error route.
//
// The route is dynamic: the typed read and the API base URL resolve per
// request.
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Document — TerraBase Studio",
};

export default async function DocumentPage({
  params,
}: {
  readonly params: Promise<{ readonly collection: string; readonly documentId: string }>;
}) {
  const { collection, documentId } = await params;
  const client = createStudioClient();
  try {
    const load = await loadDocument(client, collection, documentId);
    if (load.kind === "missing") {
      return <DocumentMissing collection={collection} documentId={documentId} />;
    }
    return <DocumentViewer document={load.document} />;
  } catch (error) {
    const typed = documentsRouteFailure(error, client);
    return (
      <DocumentsTypedError
        scope="document"
        code={typed.code}
        message={typed.message}
        retryable={typed.retryable}
        retryHref={documentHref(collection, documentId)}
      />
    );
  }
}
