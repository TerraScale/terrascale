import { Suspense } from "react";
import type { Metadata } from "next";
import { redirect } from "next/navigation";

import { TerrabaseApiError } from "@/api/generated/client";
import { IndexCatalogView } from "@/components/queries/IndexCatalogView";
import { buildIndexCatalog, CATALOG_QUERY_TEXT } from "@/components/queries/index-catalog-model";
import { OperationErrorPanel } from "@/components/queries/OperationErrorPanel";
import { errorOutcome } from "@/components/queries/query-console-model";
import { createStudioClient } from "@/lib/server/studio-client";
import { resolveLoginUrl } from "@/lib/server/session";

// Index view (DB-05): the index catalog, read through one typed query
// operation and streamed inside a Suspense boundary so a slow read shows the
// pending state instead of blocking the route.
//
// The contract carries no dedicated index catalog operation yet, so the read is
// a query.execute over a fixed, operator-visible query text; the view renders
// exactly what the host returned — statuses distinct, evidence verbatim, and
// nothing invented when the answer is empty.
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Indexes — TerraBase Studio",
};

export default function IndexesPage() {
  return (
    <div className="grid gap-4">
      <div>
        <h1 className="text-xl font-semibold text-ink">Indexes</h1>
        <p className="mt-1 max-w-2xl text-sm text-ink-muted">
          The index catalog with per-index status. Stale and rebuilding indexes are labeled
          distinctly, each with the evidence the host reported for that state.
        </p>
      </div>
      <Suspense fallback={<IndexCatalogPending />}>
        <IndexCatalogRead />
      </Suspense>
    </div>
  );
}

function IndexCatalogPending() {
  return (
    <section
      aria-busy="true"
      aria-live="polite"
      className="rounded-panel border border-line bg-surface px-4 py-6 text-sm text-ink-muted shadow-panel"
      data-testid="index-catalog-pending"
      role="status"
    >
      <span aria-hidden="true" className="mr-1.5 text-status-warn">
        ◌
      </span>
      Reading the index catalog through the typed query operation…
    </section>
  );
}

async function IndexCatalogRead() {
  const client = createStudioClient();
  let result;
  try {
    result = await client.queryExecute({ input: { kind: "v1", text: CATALOG_QUERY_TEXT } });
  } catch (error) {
    // Only typed API errors become designed error routes. Anything else —
    // including the control-flow error redirect() throws — propagates.
    if (!(error instanceof TerrabaseApiError)) {
      throw error;
    }
    if (
      error.code === "session.unauthenticated" &&
      error.loginUrl !== null &&
      error.loginUrl.length > 0
    ) {
      redirect(resolveLoginUrl(client.baseUrl, error.loginUrl));
    }
    return (
      <OperationErrorPanel
        outcome={errorOutcome({
          code: error.code,
          message: error.message,
          retryable: error.retryable,
        })}
        testId="index-catalog-error"
        title="Index catalog read failed"
      />
    );
  }
  return <IndexCatalogView catalog={buildIndexCatalog(result.rows)} staleness={result.staleness} />;
}
