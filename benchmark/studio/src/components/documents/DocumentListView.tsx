"use client";

// Per-collection document list island (DB-03): the first page arrives through
// the server component's typed `query.execute` read, and Load more fetches
// the next page window through the same typed client and APPENDS it —
// pagination is incremental, never a full route reload. Client memory stays
// bounded: at most MAX_LOADED_PAGES pages are retained, and reaching the cap
// disables Load more with an explicit indicator instead of accumulating.
//
// DB-04 live rows: when `live` is set, the island subscribes to the backbone
// changes stream scoped to this collection and refreshes the LOADED page
// windows on matching change events — event-driven, never a timer (no
// polling exists on this page). Rows update live in the same session as
// browsing: puts appear and update, deletes drop out, and the next refresh
// rides the next event. Host-shed drops surface with their resume boundary.

import { useEffect, useRef, useState } from "react";

import {
  TerrabaseStudioClient,
  TerrabaseApiError,
  type BackendContext,
  type DocumentRecord,
} from "@/api/generated/client";
import { formatInteger } from "@/lib/format";
import {
  DOCUMENT_PAGE_SIZE,
  MAX_LOADED_PAGES,
  documentHref,
  documentPageQueryText,
  documentPreview,
  filterDocuments,
  paginationStatus,
  parseDocumentRows,
} from "@/components/documents/vocabulary";

export interface DocumentLiveConfig {
  readonly apiBaseUrl: string;
  readonly backendContext: BackendContext;
}

export interface DocumentListViewProps {
  readonly collection: string;
  readonly initialRows: readonly DocumentRecord[];
  readonly matched: number;
  readonly malformed: number;
  readonly apiBaseUrl: string;
  readonly live?: DocumentLiveConfig;
}

interface LoadMoreFailure {
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
}

export function DocumentListView({
  collection,
  initialRows,
  matched,
  malformed,
  apiBaseUrl,
  live,
}: DocumentListViewProps) {
  const [rows, setRows] = useState<readonly DocumentRecord[]>(initialRows);
  const [matchedCount, setMatchedCount] = useState(matched);
  const [malformedCount] = useState(malformed);
  const [filter, setFilter] = useState("");
  const [loadingMore, setLoadingMore] = useState(false);
  const [failure, setFailure] = useState<LoadMoreFailure | null>(null);
  const [liveState, setLiveState] = useState<
    "off" | "connecting" | "live" | "reconnecting" | "failed"
  >(live === undefined ? "off" : "connecting");
  const [liveDropped, setLiveDropped] = useState<number | null>(null);
  const [liveUpdates, setLiveUpdates] = useState(0);
  // The refresh trigger: change events for this collection set a counter;
  // a trailing debounce coalesces bursts into one typed re-read of the
  // loaded windows. No interval anywhere — the only driver is events.
  const refreshDeadline = useRef<ReturnType<typeof setTimeout> | null>(null);

  const rowsRef = useRef<readonly DocumentRecord[]>(initialRows);
  rowsRef.current = rows;

  useEffect(() => {
    if (live === undefined) {
      return;
    }
    const client = new TerrabaseStudioClient({
      baseUrl: live.apiBaseUrl,
      backendContext: live.backendContext,
    });
    const refreshLoaded = async () => {
      try {
        const pages = Math.max(1, Math.ceil(rowsRef.current.length / DOCUMENT_PAGE_SIZE));
        const windows: DocumentRecord[][] = [];
        let matchedNow: number | null = null;
        for (let page = 0; page < pages; page += 1) {
          const result = await client.queryExecute({
            input: {
              kind: "v1",
              text: documentPageQueryText(collection, {
                limit: DOCUMENT_PAGE_SIZE,
                offset: page * DOCUMENT_PAGE_SIZE,
              }),
            },
          });
          windows.push(parseDocumentRows(result).documents);
          if (Number.isFinite(result.matched)) {
            matchedNow = Math.max(0, Math.trunc(result.matched));
          }
        }
        setRows(windows.flat());
        if (matchedNow !== null) {
          setMatchedCount(matchedNow);
        }
        setLiveUpdates((count) => count + 1);
      } catch {
        // A failed live refresh leaves the loaded rows in place; the next
        // change event drives the next attempt (no polling, no spin).
      }
    };
    const subscription = client.subscribeChanges(
      {
        onState: (state) => setLiveState(state),
        onEvent: () => {
          if (refreshDeadline.current !== null) {
            return;
          }
          refreshDeadline.current = setTimeout(() => {
            refreshDeadline.current = null;
            void refreshLoaded();
          }, 250);
        },
        onDrop: (drop) => setLiveDropped(drop.dropped),
        onFatal: () => setLiveState("failed"),
      },
      { collections: [collection] },
    );
    return () => {
      subscription.close();
      if (refreshDeadline.current !== null) {
        clearTimeout(refreshDeadline.current);
        refreshDeadline.current = null;
      }
    };
    // rowsRef carries the loaded-window count; the subscription itself is
    // independent of the row state (no re-subscribe on refresh).
  }, [live, collection]);

  const status = paginationStatus({
    loadedRows: rows.length,
    matched: matchedCount,
    pageSize: DOCUMENT_PAGE_SIZE,
    maxPages: MAX_LOADED_PAGES,
  });
  const filtered = filterDocuments(rows, filter);

  async function loadMore(): Promise<void> {
    if (loadingMore || status.kind !== "more") {
      return;
    }
    setLoadingMore(true);
    setFailure(null);
    try {
      const client = new TerrabaseStudioClient({ baseUrl: apiBaseUrl });
      const result = await client.queryExecute({
        input: {
          kind: "v1",
          text: documentPageQueryText(collection, {
            limit: DOCUMENT_PAGE_SIZE,
            offset: status.nextPageOffset,
          }),
        },
      });
      const parsed = parseDocumentRows(result);
      setRows((previous) => [...previous, ...parsed.documents]);
      setMatchedCount(
        Number.isFinite(result.matched) ? Math.max(0, Math.trunc(result.matched)) : 0,
      );
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

  const loadMoreDisabled = loadingMore || status.kind !== "more";

  return (
    <section aria-labelledby="document-list-heading" className="grid gap-3">
      <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 id="document-list-heading" className="text-base font-semibold text-ink">
            Documents
          </h2>
          <label className="flex items-center gap-2 text-sm text-ink-muted">
            <span>Id filter</span>
            <input
              type="search"
              value={filter}
              onChange={(event) => setFilter(event.target.value)}
              aria-label="Filter loaded documents by id"
              placeholder="e.g. browse-01"
              className="w-48 rounded-panel border border-line bg-surface-raised px-2 py-1 font-mono text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
            />
          </label>
        </div>
        <p className="mt-1 text-xs text-ink-faint" data-testid="document-filter-scope">
          The filter narrows loaded pages only; server-side search joins with the typed query
          surface when SE-08 lands.
        </p>
        {live === undefined ? null : (
          <p
            className="mt-2 flex flex-wrap items-center gap-2 text-xs"
            data-testid="document-live-status"
            data-connection-state={liveState}
          >
            <span
              aria-hidden="true"
              className={
                liveState === "live"
                  ? "text-status-live"
                  : liveState === "failed"
                    ? "text-status-critical"
                    : "text-status-warn"
              }
            >
              {liveState === "live" ? "●" : liveState === "failed" ? "✕" : "◌"}
            </span>
            <span className="text-ink">
              {liveState === "live"
                ? "Live rows"
                : liveState === "failed"
                  ? "Live rows failed"
                  : liveState === "reconnecting"
                    ? "Live rows reconnecting"
                    : "Live rows connecting"}
            </span>
            <span className="text-ink-faint">
              rows refresh on this collection&apos;s change events ({formatInteger(liveUpdates)}{" "}
              refresh{liveUpdates === 1 ? "" : "es"} this session; no polling)
            </span>
            {liveDropped !== null && liveDropped > 0 ? (
              <span className="font-mono text-status-warn" data-testid="document-live-drops">
                {formatInteger(liveDropped)} notifications dropped (host bounded queue; resume from
                the surfaced boundary)
              </span>
            ) : null}
          </p>
        )}
        <p className="mt-2 text-sm text-ink-muted" data-testid="document-list-status" role="status">
          {status.kind === "more"
            ? `Showing ${formatInteger(status.loadedRows)} of ${formatInteger(status.matched)} matched documents`
            : status.kind === "complete"
              ? `All ${formatInteger(status.matched)} matched documents are loaded`
              : `Bounded view window reached: ${formatInteger(status.retainedPages)} pages retained, ${formatInteger(status.matched - status.loadedRows)} of ${formatInteger(status.matched)} matched documents not loaded`}
        </p>
        <div className="mt-2 flex flex-wrap items-center gap-3">
          <button
            type="button"
            onClick={() => void loadMore()}
            disabled={loadMoreDisabled}
            className="rounded-panel border border-line bg-surface-raised px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent disabled:cursor-default disabled:border-line disabled:text-ink-faint disabled:hover:border-line disabled:hover:text-ink-faint"
          >
            Load more documents
          </button>
          {loadingMore ? (
            <span className="text-sm text-ink-muted" role="status">
              Loading more…
            </span>
          ) : null}
        </div>
        {failure !== null ? (
          <p
            className="mt-2 font-mono text-xs text-status-critical"
            data-testid="document-load-error"
            role="alert"
          >
            Load more failed — code {failure.code}, retryable {String(failure.retryable)}:{" "}
            {failure.message}
          </p>
        ) : null}
        {malformedCount > 0 ? (
          <p className="mt-2 font-mono text-xs text-status-warn" role="status">
            {formatInteger(malformedCount)} rows on the first page were malformed and are not shown.
          </p>
        ) : null}
      </div>

      {filtered.length === 0 ? (
        <div
          className="rounded-panel border border-line bg-surface px-4 py-6 text-center shadow-panel"
          data-testid="documents-empty"
        >
          <p className="text-sm text-ink-muted">
            No documents are shown: the typed listing query matched none in this collection, or the
            id filter matched none of the loaded pages.
          </p>
        </div>
      ) : (
        <div className="overflow-x-auto rounded-panel border border-line bg-surface shadow-panel">
          <table className="w-full text-sm" data-testid="document-list">
            <thead>
              <tr className="border-b border-line text-left text-xs tracking-wide text-ink-muted uppercase">
                <th scope="col" className="px-4 py-2 font-medium">
                  Document
                </th>
                <th scope="col" className="px-4 py-2 font-medium">
                  Sequence
                </th>
                <th scope="col" className="px-4 py-2 font-medium">
                  Value
                </th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((document) => (
                <tr
                  key={document.id}
                  className="border-b border-line last:border-b-0"
                  data-testid={`document-row-${document.id}`}
                >
                  <td className="px-4 py-2 font-mono text-ink">
                    <a
                      href={documentHref(collection, document.id)}
                      className="text-ink underline-offset-4 transition-colors hover:text-accent hover:underline"
                    >
                      {document.id}
                    </a>
                  </td>
                  <td className="px-4 py-2 font-mono text-ink-muted tabular-nums">
                    {formatInteger(document.version)}
                  </td>
                  <td className="max-w-md truncate px-4 py-2 font-mono text-xs text-ink-muted">
                    {documentPreview(document.value)}
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
