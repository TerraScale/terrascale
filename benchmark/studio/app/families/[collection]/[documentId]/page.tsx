import type { Metadata } from "next";

import { DocumentScopeCard, DocumentScopeMissing } from "@/components/families/DocumentScopeCard";
import { FamilyKindList } from "@/components/families/FamilyKindList";
import { FamiliesTypedError } from "@/components/families/FamiliesTypedError";
import { AttachmentsPanel } from "@/components/families/AttachmentsPanel";
import { CountersPanel } from "@/components/families/CountersPanel";
import { TimeSeriesPanel } from "@/components/families/TimeSeriesPanel";
import { familiesHref } from "@/components/families/vocabulary";
import { createStudioClient } from "@/lib/server/studio-client";

import {
  familiesRouteFailure,
  loadAttachmentsWindow,
  loadCountersWindow,
  loadDocumentScope,
  loadTimeSeriesCatalog,
  loadTimeSeriesWindow,
} from "../../data";

// One document's family panels (DB-06): the route proves its scope with one
// typed `document.read` (a missing document is the read's designed null
// outcome and renders its own route), then loads the first windows of the
// three viewable kinds through typed `family.read` operations — counters with
// their update semantics labeled, the time-series catalog plus the first
// series' first window, and attachment metadata. Every panel pages
// incrementally through the same typed operation in its client island; the
// remaining FamilyKinds render explicitly as not yet viewable. Typed failures
// render the designed error route; a host-enforced session gate redirects
// exactly where the host says.
//
// The route is dynamic: the typed reads and the API base URL resolve per
// request against the API host (the dev fixture or SE-08), never prerendered
// into a stale build artifact.
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Document families — TerraBase Studio",
};

export default async function DocumentFamiliesPage({
  params,
}: {
  readonly params: Promise<{ readonly collection: string; readonly documentId: string }>;
}) {
  const { collection, documentId } = await params;
  const client = createStudioClient();
  try {
    const scope = await loadDocumentScope(client, collection, documentId);
    if (scope.kind === "missing") {
      return <DocumentScopeMissing collection={collection} documentId={documentId} />;
    }

    const [counters, attachments, catalog] = await Promise.all([
      loadCountersWindow(client, collection, documentId),
      loadAttachmentsWindow(client, collection, documentId),
      loadTimeSeriesCatalog(client, collection, documentId),
    ]);
    let initialSeries: {
      name: string;
      parse: Awaited<ReturnType<typeof loadTimeSeriesWindow>>;
    } | null = null;
    if (catalog.kind === "recognized" && catalog.state.series.length > 0) {
      const firstName = catalog.state.series[0]?.name ?? "";
      initialSeries = {
        name: firstName,
        parse: await loadTimeSeriesWindow(client, collection, documentId, firstName, null, 0),
      };
    }

    return (
      <div className="grid gap-4">
        <div>
          <h1 className="text-xl font-semibold text-ink">Family panels</h1>
          <p className="mt-1 max-w-2xl text-sm text-ink-muted">
            One document&rsquo;s Data Family state through typed family operations: counters with
            their update semantics labeled, time-series with range selection, and attachment
            metadata — every panel paging incrementally with bounded windows.
          </p>
        </div>
        <DocumentScopeCard
          collection={scope.collection}
          documentId={scope.documentId}
          version={scope.version}
        />
        <CountersPanel
          collection={collection}
          documentId={documentId}
          initial={counters}
          apiBaseUrl={client.baseUrl}
        />
        <TimeSeriesPanel
          collection={collection}
          documentId={documentId}
          catalog={catalog.kind === "recognized" ? catalog.state.series.map((s) => s.name) : null}
          initial={initialSeries}
          apiBaseUrl={client.baseUrl}
        />
        <AttachmentsPanel
          collection={collection}
          documentId={documentId}
          initial={attachments}
          apiBaseUrl={client.baseUrl}
        />
        <FamilyKindList variant="compact" />
      </div>
    );
  } catch (error) {
    const typed = familiesRouteFailure(error, client);
    return (
      <FamiliesTypedError
        scope="panels"
        code={typed.code}
        message={typed.message}
        retryable={typed.retryable}
        retryHref={familiesHref(collection, documentId)}
      />
    );
  }
}
