// Paginated result rows for the query console (DB-05).
//
// Paging happens over the rows the host returned for this execution: the wire
// contract's query request carries no paging fields, so the engine's own
// QueryPaging cannot be requested through it (recorded as a contract gap). The
// pager states that explicitly rather than implying server-side paging.

import type { ConsoleRowLike, PagerDescriptor } from "@/components/queries/query-console-model";
import { formatInteger } from "@/lib/format";

export interface QueryResultsProps {
  readonly rows: readonly ConsoleRowLike[];
  readonly pager: PagerDescriptor;
}

export function QueryResults({ rows, pager }: QueryResultsProps) {
  return (
    <section
      aria-labelledby="query-results-heading"
      className="grid gap-3 rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
      data-testid="query-results"
    >
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 id="query-results-heading" className="text-base font-semibold text-ink">
          Results
        </h2>
        <p className="text-xs text-ink-muted" data-testid="query-results-window">
          {pager.firstRowIndex === null || pager.lastRowIndex === null
            ? "0 rows"
            : `rows ${formatInteger(pager.firstRowIndex)}–${formatInteger(pager.lastRowIndex)} of ${formatInteger(pager.rowCount)}`}
        </p>
      </div>

      {rows.length === 0 ? (
        <p className="text-sm text-ink-muted" data-testid="query-results-empty">
          The host returned no rows for this query.
        </p>
      ) : (
        <ul className="grid gap-2" data-testid="query-results-rows">
          {rows.map((row) => (
            <li
              key={`${row.document.collection}/${row.document.id}`}
              className="grid gap-1 rounded bg-surface-raised px-3 py-2"
              data-row-document-id={row.document.id}
            >
              <div className="flex flex-wrap gap-x-4 gap-y-0.5 font-mono text-xs text-ink">
                <span className="text-ink" data-testid="query-row-id">
                  {row.document.id}
                </span>
                <span className="text-ink-muted" data-testid="query-row-collection">
                  {row.document.collection}
                </span>
                <span className="text-ink-faint">v{formatInteger(row.document.version)}</span>
              </div>
              {row.projected.length === 0 ? null : (
                <dl className="flex flex-wrap gap-x-4 gap-y-0.5 font-mono text-xs">
                  {row.projected.map((projection) => (
                    <div key={projection.name} className="flex gap-1.5">
                      <dt className="text-ink-faint">{projection.name}</dt>
                      <dd className="text-ink" data-testid="query-row-projection">
                        {renderProjectionValue(projection.value)}
                      </dd>
                    </div>
                  ))}
                </dl>
              )}
              <details className="text-xs text-ink-muted">
                <summary className="cursor-default text-ink-faint">document value</summary>
                <pre className="mt-1 max-h-48 overflow-auto rounded bg-surface px-2 py-1 text-xs text-ink">
                  {JSON.stringify(row.document.value, null, 2)}
                </pre>
              </details>
            </li>
          ))}
        </ul>
      )}

      <p className="text-xs text-ink-faint" data-testid="query-paging-note">
        Paging is applied over the rows this execution returned. The contract&apos;s query request
        carries no paging fields, so the engine&apos;s own query paging cannot be requested through
        it yet.
      </p>
    </section>
  );
}

function renderProjectionValue(value: unknown): string {
  if (typeof value === "string") {
    return value;
  }
  return JSON.stringify(value) ?? "null";
}
