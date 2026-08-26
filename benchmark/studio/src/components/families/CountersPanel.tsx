"use client";

// Counters family panel (DB-06): one document's counter group through the
// typed family.read operation — the first window arrives through the server
// component's typed read, and Load more fetches the next entry window through
// the same typed client and APPENDS it: paging is incremental, never a full
// route reload. Client memory stays bounded: at most MAX_LOADED_ENTRY_PAGES
// windows are retained, and reaching the cap disables Load more with an
// explicit indicator instead of accumulating. Each value carries the engine's
// update semantics verbatim — a signed increment creating from zero, and
// delete by name (CounterOperation), never a put or a set.

import { useState } from "react";

import { TerrabaseApiError, TerrabaseStudioClient, type Json } from "@/api/generated/client";
import { formatInteger } from "@/lib/format";
import {
  COUNTER_DELETE_SEMANTICS,
  COUNTER_INCREMENT_SEMANTICS,
  FAMILY_ENTRY_PAGE_SIZE,
  MAX_LOADED_ENTRY_PAGES,
  counterEntriesScope,
  entryPagingStatus,
  familyEchoMismatch,
  parseCounterFamilyState,
  type CounterFamilyEntry,
  type CounterFamilyState,
  type FamilyStateParse,
} from "@/components/families/vocabulary";

export interface CountersPanelProps {
  readonly collection: string;
  readonly documentId: string;
  readonly initial: FamilyStateParse<CounterFamilyState>;
  readonly apiBaseUrl: string;
}

interface LoadFailure {
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
}

export function CountersPanel({ collection, documentId, initial, apiBaseUrl }: CountersPanelProps) {
  const [entries, setEntries] = useState<readonly CounterFamilyEntry[]>(
    initial.kind === "recognized" ? initial.state.entries : [],
  );
  const [matched, setMatched] = useState(initial.kind === "recognized" ? initial.state.matched : 0);
  const [malformedCount] = useState(initial.kind === "recognized" ? initial.malformed : 0);
  const [recognized, setRecognized] = useState(initial.kind === "recognized");
  const [loadingMore, setLoadingMore] = useState(false);
  const [failure, setFailure] = useState<LoadFailure | null>(null);

  const status = recognized
    ? entryPagingStatus({
        loadedEntries: entries.length,
        matched,
        pageSize: FAMILY_ENTRY_PAGE_SIZE,
        maxPages: MAX_LOADED_ENTRY_PAGES,
      })
    : null;

  async function loadMore(): Promise<void> {
    if (loadingMore || status === null || status.kind !== "more") {
      return;
    }
    setLoadingMore(true);
    setFailure(null);
    try {
      const client = new TerrabaseStudioClient({ baseUrl: apiBaseUrl });
      const scope = counterEntriesScope(collection, documentId, {
        limit: FAMILY_ENTRY_PAGE_SIZE,
        offset: status.nextOffset,
      });
      const result = await client.familyRead({ kind: "counters", scope });
      const mismatch = familyEchoMismatch(result, "counters", scope);
      if (mismatch !== null) {
        setFailure({ code: "family.kind-mismatch", message: mismatch, retryable: false });
        return;
      }
      const parsed = parseCounterFamilyState(result.state as Json);
      if (parsed.kind === "unrecognized") {
        setRecognized(false);
        return;
      }
      setEntries((previous) => [...previous, ...parsed.state.entries]);
      setMatched(parsed.state.matched);
    } catch (error) {
      if (error instanceof TerrabaseApiError) {
        setFailure({ code: error.code, message: error.message, retryable: error.retryable });
      } else {
        setFailure({
          code: "error.unknown",
          message: error instanceof Error ? error.message : String(error),
          retryable: false,
        });
      }
    } finally {
      setLoadingMore(false);
    }
  }

  const loadMoreDisabled = loadingMore || status === null || status.kind !== "more";

  return (
    <section
      aria-labelledby="counters-panel-heading"
      className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
      data-testid="counters-panel"
      data-family-kind="counters"
    >
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 id="counters-panel-heading" className="text-base font-semibold text-ink">
          Counters
        </h2>
        <span className="font-mono text-xs text-ink-faint">family.read · counters</span>
      </div>
      <p className="mt-1 text-xs text-ink-muted" data-testid="counters-update-semantics">
        Update semantics: {COUNTER_INCREMENT_SEMANTICS} {COUNTER_DELETE_SEMANTICS}
      </p>
      {!recognized ? (
        <p
          className="mt-2 font-mono text-xs text-status-warn"
          data-testid="counters-unrecognized"
          role="status"
        >
          The typed counters state is not the shape this view reads (the host may serve the
          pre-SE-08 family shape); nothing is coerced.
        </p>
      ) : (
        <>
          <p className="mt-2 text-sm text-ink-muted" data-testid="counters-status" role="status">
            {status?.kind === "more"
              ? `Showing ${formatInteger(status.loadedEntries)} of ${formatInteger(status.matched)} counter entries`
              : status?.kind === "complete"
                ? `All ${formatInteger(status.matched)} counter entries are loaded`
                : `Bounded view window reached: ${formatInteger(status?.retainedPages ?? 0)} pages retained, ${formatInteger((status?.matched ?? 0) - (status?.loadedEntries ?? 0))} of ${formatInteger(status?.matched ?? 0)} counter entries not loaded`}
          </p>
          <div className="mt-2 flex flex-wrap items-center gap-3">
            <button
              type="button"
              onClick={() => void loadMore()}
              disabled={loadMoreDisabled}
              className="rounded-panel border border-line bg-surface-raised px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent disabled:cursor-default disabled:border-line disabled:text-ink-faint disabled:hover:border-line disabled:hover:text-ink-faint"
            >
              Load more counters
            </button>
            {loadingMore ? (
              <span className="text-sm text-ink-muted" role="status">
                Loading more…
              </span>
            ) : null}
          </div>
          {entries.length === 0 ? (
            <p className="mt-3 text-sm text-ink-muted" data-testid="counters-empty">
              No counter entries are shown: the typed counters read matched none for this document.
            </p>
          ) : (
            <div className="mt-3 overflow-x-auto rounded-panel border border-line">
              <table className="w-full text-sm" data-testid="counters-table">
                <thead>
                  <tr className="border-b border-line text-left text-xs tracking-wide text-ink-muted uppercase">
                    <th scope="col" className="px-4 py-2 font-medium">
                      Counter
                    </th>
                    <th scope="col" className="px-4 py-2 font-medium">
                      Value
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {entries.map((entry) => (
                    <tr
                      key={entry.name}
                      className="border-b border-line last:border-b-0"
                      data-testid={`counter-entry-${entry.name}`}
                    >
                      <td className="px-4 py-2 font-mono text-ink">{entry.name}</td>
                      <td className="px-4 py-2 font-mono text-ink tabular-nums">
                        {entry.value === null ? (
                          <>
                            <span aria-hidden="true">—</span>
                            <span className="sr-only"> the counter does not exist</span>
                          </>
                        ) : (
                          formatInteger(entry.value)
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {malformedCount > 0 ? (
            <p className="mt-2 font-mono text-xs text-status-warn" role="status">
              {formatInteger(malformedCount)} entries in the loaded windows were malformed and are
              not shown.
            </p>
          ) : null}
        </>
      )}
      {failure !== null ? (
        <p
          className="mt-2 font-mono text-xs text-status-critical"
          data-testid="counters-error"
          role="alert"
        >
          Family read failed — code {failure.code}, retryable {String(failure.retryable)}:{" "}
          {failure.message}
        </p>
      ) : null}
    </section>
  );
}
