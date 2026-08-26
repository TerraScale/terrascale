// Index catalog view (DB-05).
//
// The catalog is read through one typed `query.execute` operation over a
// reserved, operator-visible query text (the contract has no dedicated index
// catalog operation yet; the engine's IndexCatalog is crate-private). Every
// status label is distinct — stale and rebuilding never share a rendering —
// and every non-fresh entry shows the host's own evidence verbatim. Rows that
// do not parse are counted, never dropped silently.

import type { IndexCatalog, IndexStatus } from "@/components/queries/index-catalog-model";
import { CATALOG_QUERY_TEXT, INDEX_STATUS_LABEL } from "@/components/queries/index-catalog-model";
import type { StalenessObservationLike } from "@/components/queries/query-console-model";
import { formatInteger } from "@/lib/format";

export interface IndexCatalogViewProps {
  readonly catalog: IndexCatalog;
  /** The host's staleness observations for the same catalog read, verbatim. */
  readonly staleness: readonly StalenessObservationLike[];
}

export function IndexCatalogView({ catalog, staleness }: IndexCatalogViewProps) {
  return (
    <section
      aria-labelledby="index-catalog-heading"
      className="grid gap-4"
      data-testid="index-catalog"
    >
      <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <h2 id="index-catalog-heading" className="text-base font-semibold text-ink">
          Index catalog
        </h2>
        <p className="mt-1 text-xs text-ink-muted">
          Read through one typed <code>query.execute</code> operation. The query text is fixed and
          shown to the operator, so the catalog read is never a hidden channel:
        </p>
        <pre
          className="mt-2 overflow-auto rounded bg-surface-raised px-2 py-1 font-mono text-xs text-ink"
          data-testid="index-catalog-query"
        >
          {CATALOG_QUERY_TEXT}
        </pre>
        <dl
          className="mt-3 grid grid-cols-2 gap-x-6 gap-y-1 text-sm sm:grid-cols-4"
          data-testid="index-catalog-counts"
        >
          <Count status="fresh" count={catalog.statusCounts.fresh} />
          <Count status="stale" count={catalog.statusCounts.stale} />
          <Count status="rebuilding" count={catalog.statusCounts.rebuilding} />
          <Count status="unknown" count={catalog.statusCounts.unknown} />
        </dl>
      </div>

      {catalog.entries.length === 0 ? (
        <p
          className="rounded-panel border border-line bg-surface px-4 py-3 text-sm text-ink-muted shadow-panel"
          data-testid="index-catalog-empty"
        >
          The catalog read returned no index entries. The API contract carries no dedicated index
          catalog operation yet, so nothing is listed here until the host answers this query — the
          view never invents entries.
        </p>
      ) : (
        <ul className="grid gap-2" data-testid="index-catalog-entries">
          {catalog.entries.map((entry) => (
            <li
              key={entry.namespace}
              className="grid gap-1 rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
              data-index-namespace={entry.namespace}
            >
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="font-mono text-sm text-ink" data-testid="index-entry-namespace">
                  {entry.namespace}
                </span>
                <StatusBadge status={entry.status} rawStatus={entry.rawStatus} />
              </div>
              {entry.reason === null ? (
                <p className="text-xs text-ink-faint" data-testid="index-entry-no-reason">
                  The host reported no evidence for this state.
                </p>
              ) : (
                <p className="font-mono text-xs text-ink-muted" data-testid="index-entry-reason">
                  {entry.reason}
                </p>
              )}
            </li>
          ))}
        </ul>
      )}

      {catalog.unparsedCount > 0 ? (
        <p
          role="status"
          className="rounded-panel border border-line bg-surface px-4 py-2 text-xs text-status-warn shadow-panel"
          data-testid="index-catalog-unparsed"
        >
          {formatInteger(catalog.unparsedCount)} catalog row
          {catalog.unparsedCount === 1 ? "" : "s"} did not parse and{" "}
          {catalog.unparsedCount === 1 ? "is" : "are"} not shown above.
        </p>
      ) : null}

      <div className="grid gap-2 rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <h3 className="text-sm font-semibold text-ink">Staleness observations for this read</h3>
        {staleness.length === 0 ? (
          <p className="text-sm text-ink-muted" data-testid="index-catalog-staleness-empty">
            The host reported no staleness observations for the catalog read.
          </p>
        ) : (
          <ul className="grid gap-1" data-testid="index-catalog-staleness">
            {staleness.map((observation) => (
              <li
                key={observation.indexId}
                className="grid gap-0.5 rounded bg-surface-raised px-2 py-1 font-mono text-xs text-ink"
                data-index-id={observation.indexId}
              >
                <span className="text-ink">{observation.indexId}</span>
                <span className="text-ink-muted">{observation.reason}</span>
              </li>
            ))}
          </ul>
        )}
        <p className="text-xs text-ink-faint" data-testid="index-catalog-contract-note">
          Per-index rebuild progress and the engine&apos;s Index Selection Record are engine state
          behind typed operations the contract does not carry yet; this view shows only what the
          host returned, labeled with the host&apos;s own values.
        </p>
      </div>
    </section>
  );
}

function Count({ status, count }: { status: IndexStatus; count: number }) {
  return (
    <div className="flex gap-2" data-count={INDEX_STATUS_LABEL[status]}>
      <dt className="text-ink-muted">{INDEX_STATUS_LABEL[status]}</dt>
      <dd className="font-mono tabular-nums text-ink" data-index-status-count={status}>
        {formatInteger(count)}
      </dd>
    </div>
  );
}

function StatusBadge({ status, rawStatus }: { status: IndexStatus; rawStatus: string }) {
  const tone =
    status === "fresh"
      ? "border-line text-status-live"
      : status === "stale"
        ? "border-line text-status-warn"
        : status === "rebuilding"
          ? "border-line text-accent"
          : "border-line text-ink-muted";
  return (
    <span
      className={`rounded-full border bg-surface-raised px-2 py-0.5 font-mono text-xs ${tone}`}
      data-testid="index-entry-status"
      data-index-status={status}
    >
      {INDEX_STATUS_LABEL[status]}
      {status === "unknown" && rawStatus.length > 0 ? (
        <span className="text-ink-faint"> ({rawStatus})</span>
      ) : null}
    </span>
  );
}
