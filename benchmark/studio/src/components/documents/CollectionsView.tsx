"use client";

// Collections listing island (DB-03): the typed catalog rows rendered as a
// table with a client-side name filter. The catalog arrives through the
// server component's typed `query.execute` read; the island only filters what
// is already loaded — the typed request has no server-side search field, and
// the scope label says so.

import { useState } from "react";

import type { CollectionSummary } from "@/api/generated/client";
import { formatBytes, formatInteger } from "@/lib/format";
import { collectionHref, filterCollections } from "@/components/documents/vocabulary";

export interface CollectionsViewProps {
  readonly summaries: readonly CollectionSummary[];
  readonly malformed: number;
}

export function CollectionsView({ summaries, malformed }: CollectionsViewProps) {
  const [filter, setFilter] = useState("");
  const filtered = filterCollections(summaries, filter);
  const filtering = filter.trim().length > 0;

  return (
    <section aria-labelledby="collections-heading" className="grid gap-3">
      <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 id="collections-heading" className="text-base font-semibold text-ink">
            Collections
          </h2>
          <label className="flex items-center gap-2 text-sm text-ink-muted">
            <span>Name filter</span>
            <input
              type="search"
              value={filter}
              onChange={(event) => setFilter(event.target.value)}
              aria-label="Filter collections by name"
              placeholder="e.g. studio"
              className="w-48 rounded-panel border border-line bg-surface-raised px-2 py-1 font-mono text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
            />
          </label>
        </div>
        <p className="mt-1 text-xs text-ink-faint">
          The filter narrows the loaded catalog client-side; server-side search joins with the typed
          query surface when SE-08 lands.
        </p>
        {filtering ? (
          <p className="mt-2 text-sm text-ink-muted" data-testid="collections-filter-status">
            {formatInteger(filtered.length)} of {formatInteger(summaries.length)} collections
          </p>
        ) : null}
        {malformed > 0 ? (
          <p
            className="mt-2 font-mono text-xs text-status-warn"
            data-testid="catalog-malformed"
            role="status"
          >
            {formatInteger(malformed)} catalog rows were malformed and are not shown.
          </p>
        ) : null}
      </div>

      {filtered.length === 0 ? (
        <div
          className="rounded-panel border border-line bg-surface px-4 py-6 text-center shadow-panel"
          data-testid="collections-empty"
        >
          <p className="text-sm text-ink-muted">
            {summaries.length === 0
              ? "The typed catalog query matched no collections."
              : "The name filter matched no collections."}
          </p>
        </div>
      ) : (
        <div className="overflow-x-auto rounded-panel border border-line bg-surface shadow-panel">
          <table className="w-full text-sm" data-testid="collections-table">
            <thead>
              <tr className="border-b border-line text-left text-xs tracking-wide text-ink-muted uppercase">
                <th scope="col" className="px-4 py-2 font-medium">
                  Collection
                </th>
                <th scope="col" className="px-4 py-2 font-medium">
                  Documents
                </th>
                <th scope="col" className="px-4 py-2 font-medium">
                  Size
                </th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((summary) => (
                <tr
                  key={summary.name}
                  className="border-b border-line last:border-b-0"
                  data-testid={`collection-row-${summary.name}`}
                >
                  <td className="px-4 py-2 font-mono text-ink">
                    <a
                      href={collectionHref(summary.name)}
                      className="text-ink underline-offset-4 transition-colors hover:text-accent hover:underline"
                    >
                      {summary.name}
                    </a>
                  </td>
                  <td
                    className="px-4 py-2 font-mono text-ink-muted tabular-nums"
                    data-testid={`collection-doc-count-${summary.name}`}
                  >
                    {formatInteger(summary.documents)}
                  </td>
                  <td className="px-4 py-2 font-mono text-ink-muted tabular-nums">
                    {formatBytes(summary.sizeBytes)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
