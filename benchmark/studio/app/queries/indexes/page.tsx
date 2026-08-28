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
//
// IX-04 adds the optimizer section below on the same terms: the engine's Index
// Selection Records and lifecycle reads are engine state behind typed reads
// (src/query/optimizer), the studio contract carries no dedicated operation for
// them, so the section rides the same query.execute operation over its own
// fixed, operator-visible text and renders only what the host returned.
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Indexes — TerraBase Studio",
};

/** The optimizer read's fixed, operator-visible query text (IX-04). */
const OPTIMIZER_QUERY_TEXT = "from __optimizer_selections__";

/** Selection outcomes the section labels. Anything else is unknown, never coerced. */
type SelectionOutcome = "created" | "chosen" | "collection-scan" | "refused" | "unknown";

const OUTCOME_LABEL: Readonly<Record<SelectionOutcome, string>> = {
  created: "Created auto index",
  chosen: "Served by index",
  "collection-scan": "Collection scan",
  refused: "Creation refused",
  unknown: "Unknown",
};

interface SelectionRow {
  readonly index: string;
  readonly outcome: SelectionOutcome;
  readonly rawOutcome: string;
  readonly reason: string | null;
}

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
      <Suspense fallback={<OptimizerPending />}>
        <OptimizerRead />
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

function OptimizerPending() {
  return (
    <section
      aria-busy="true"
      aria-live="polite"
      className="rounded-panel border border-line bg-surface px-4 py-6 text-sm text-ink-muted shadow-panel"
      data-testid="optimizer-pending"
      role="status"
    >
      <span aria-hidden="true" className="mr-1.5 text-status-warn">
        ◌
      </span>
      Reading the optimizer&apos;s recent selections through the typed query operation…
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

async function OptimizerRead() {
  const client = createStudioClient();
  let result;
  try {
    result = await client.queryExecute({ input: { kind: "v1", text: OPTIMIZER_QUERY_TEXT } });
  } catch (error) {
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
        testId="optimizer-error"
        title="Optimizer read failed"
      />
    );
  }
  return <OptimizerSelectionsView rows={parseSelectionRows(result.rows)} />;
}

/**
 * Parses the host's selection rows. Only the exact known outcome spellings
 * classify; a row without a non-empty string index does not parse and is
 * counted by the view rather than dropped.
 */
function parseSelectionRows(
  rows: ReadonlyArray<{ document: { value: unknown } }>,
): { rows: SelectionRow[]; unparsed: number } {
  const parsed: SelectionRow[] = [];
  let unparsed = 0;
  for (const row of rows) {
    const value = row.document.value;
    if (typeof value !== "object" || value === null || Array.isArray(value)) {
      unparsed += 1;
      continue;
    }
    const record = value as Record<string, unknown>;
    const index = typeof record.index === "string" ? record.index : "";
    if (index.trim().length === 0) {
      unparsed += 1;
      continue;
    }
    const rawOutcome = typeof record.outcome === "string" ? record.outcome : "";
    const reason =
      typeof record.reason === "string" && record.reason.length > 0 ? record.reason : null;
    parsed.push({
      index,
      outcome: classifyOutcome(rawOutcome),
      rawOutcome,
      reason,
    });
  }
  return { rows: parsed, unparsed };
}

function classifyOutcome(raw: string): SelectionOutcome {
  if (
    raw === "created" ||
    raw === "chosen" ||
    raw === "collection-scan" ||
    raw === "refused"
  ) {
    return raw;
  }
  return "unknown";
}

function OptimizerSelectionsView({ rows, unparsed }: { rows: SelectionRow[]; unparsed: number }) {
  return (
    <section
      aria-labelledby="optimizer-heading"
      className="grid gap-4"
      data-testid="optimizer-selections"
    >
      <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <h2 id="optimizer-heading" className="text-base font-semibold text-ink">
          Optimizer selections
        </h2>
        <p className="mt-1 text-xs text-ink-muted">
          The optimizer&apos;s recent index selections — what each dynamic query was served by, and
          the recorded reason when a candidate lost. Read through the same one typed{" "}
          <code>query.execute</code> operation over a fixed, operator-visible text:
        </p>
        <pre
          className="mt-2 overflow-auto rounded bg-surface-raised px-2 py-1 font-mono text-xs text-ink"
          data-testid="optimizer-query"
        >
          {OPTIMIZER_QUERY_TEXT}
        </pre>
      </div>

      {rows.length === 0 ? (
        <p
          className="rounded-panel border border-line bg-surface px-4 py-3 text-sm text-ink-muted shadow-panel"
          data-testid="optimizer-empty"
        >
          The optimizer read returned no selections. The API contract carries no dedicated
          selection-record operation yet, so nothing is listed here until the host answers this
          query — the view never invents a selection.
        </p>
      ) : (
        <ul className="grid gap-2" data-testid="optimizer-entries">
          {rows.map((row) => (
            <li
              key={`${row.index}-${row.rawOutcome}`}
              className="grid gap-1 rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
              data-index={row.index}
            >
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="font-mono text-sm text-ink" data-testid="optimizer-entry-index">
                  {row.index}
                </span>
                <span
                  className="rounded-full border border-line bg-surface-raised px-2 py-0.5 font-mono text-xs text-ink-muted"
                  data-testid="optimizer-entry-outcome"
                  data-outcome={row.outcome}
                >
                  {OUTCOME_LABEL[row.outcome]}
                  {row.outcome === "unknown" && row.rawOutcome.length > 0 ? (
                    <span className="text-ink-faint"> ({row.rawOutcome})</span>
                  ) : null}
                </span>
              </div>
              {row.reason === null ? (
                <p className="text-xs text-ink-faint" data-testid="optimizer-entry-no-reason">
                  The host reported no recorded reason for this selection.
                </p>
              ) : (
                <p className="font-mono text-xs text-ink-muted" data-testid="optimizer-entry-reason">
                  {row.reason}
                </p>
              )}
            </li>
          ))}
        </ul>
      )}

      {unparsed > 0 ? (
        <p
          role="status"
          className="rounded-panel border border-line bg-surface px-4 py-2 text-xs text-status-warn shadow-panel"
          data-testid="optimizer-unparsed"
        >
          {unparsed} selection row{unparsed === 1 ? "" : "s"} did not parse and{" "}
          {unparsed === 1 ? "is" : "are"} not shown above.
        </p>
      ) : null}
    </section>
  );
}
