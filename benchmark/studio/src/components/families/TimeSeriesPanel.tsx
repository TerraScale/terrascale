"use client";

// Time-series family panel (DB-06): one document's series through the typed
// family.read operation. The catalog and the first series' first window arrive
// through the server component's typed reads; selecting another series or
// applying a range performs a fresh typed read (never a full route reload),
// and Load more APPENDS the next entry window. Bounded on every axis: the
// selected range span is capped by the view's explicit bound (a studio view
// bound, not an engine rule), every read carries an explicit entry window,
// and at most MAX_LOADED_ENTRY_PAGES windows are retained with an explicit
// indicator when the cap is reached. The range semantics label is the
// engine's TimeSeriesRange doc verbatim, and the host's typed range failures
// (family.range-invalid) render unmodified — never paraphrased.

import { useState } from "react";

import { TerrabaseApiError, TerrabaseStudioClient, type Json } from "@/api/generated/client";
import { formatInteger } from "@/lib/format";
import {
  FAMILY_ENTRY_PAGE_SIZE,
  MAX_LOADED_ENTRY_PAGES,
  TIME_SERIES_RANGE_SEMANTICS,
  TIME_SERIES_RANGE_VIEW_BOUND_MS,
  entryPagingStatus,
  familyEchoMismatch,
  parseEpochMsInput,
  parseTimeSeriesPageState,
  rangeWithinViewBound,
  timeSeriesEntriesScope,
  type FamilyStateParse,
  type TimeSeriesFamilyEntry,
  type TimeSeriesPageState,
  type TimeSeriesRangeSelection,
} from "@/components/families/vocabulary";

export interface TimeSeriesInitialLoad {
  readonly name: string;
  readonly parse: FamilyStateParse<TimeSeriesPageState>;
}

export interface TimeSeriesPanelProps {
  readonly collection: string;
  readonly documentId: string;
  /** The document's series names, or null when the catalog state was unrecognized. */
  readonly catalog: readonly string[] | null;
  readonly initial: TimeSeriesInitialLoad | null;
  readonly apiBaseUrl: string;
}

interface LoadFailure {
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
}

type RangeNotice =
  | { readonly kind: "unparseable" }
  | { readonly kind: "span" }
  | { readonly kind: "inverted" };

export function TimeSeriesPanel({
  collection,
  documentId,
  catalog,
  initial,
  apiBaseUrl,
}: TimeSeriesPanelProps) {
  const [seriesName, setSeriesName] = useState(initial?.name ?? catalog?.[0] ?? "");
  const [entries, setEntries] = useState<readonly TimeSeriesFamilyEntry[]>(
    initial?.parse.kind === "recognized" ? initial.parse.state.entries : [],
  );
  const [matched, setMatched] = useState(
    initial?.parse.kind === "recognized" ? initial.parse.state.matched : 0,
  );
  const [malformedCount] = useState(
    initial?.parse.kind === "recognized" ? initial.parse.malformed : 0,
  );
  const [recognized, setRecognized] = useState(initial?.parse.kind === "recognized");
  const [appliedRange, setAppliedRange] = useState<TimeSeriesRangeSelection | null>(
    initial?.parse.kind === "recognized" ? initial.parse.state.range : null,
  );
  const [rangeStart, setRangeStart] = useState(
    initial?.parse.kind === "recognized" && initial.parse.state.range !== null
      ? String(initial.parse.state.range.start)
      : "",
  );
  const [rangeEnd, setRangeEnd] = useState(
    initial?.parse.kind === "recognized" && initial.parse.state.range !== null
      ? String(initial.parse.state.range.end)
      : "",
  );
  const [rangeNotice, setRangeNotice] = useState<RangeNotice | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<LoadFailure | null>(null);

  const status = recognized
    ? entryPagingStatus({
        loadedEntries: entries.length,
        matched,
        pageSize: FAMILY_ENTRY_PAGE_SIZE,
        maxPages: MAX_LOADED_ENTRY_PAGES,
      })
    : null;

  async function readWindow(
    name: string,
    range: TimeSeriesRangeSelection | null,
    offset: number,
    mode: "replace" | "append",
  ): Promise<void> {
    setBusy(true);
    setFailure(null);
    setRangeNotice(null);
    try {
      const client = new TerrabaseStudioClient({ baseUrl: apiBaseUrl });
      const scope = timeSeriesEntriesScope(
        collection,
        documentId,
        name,
        { limit: FAMILY_ENTRY_PAGE_SIZE, offset },
        range,
      );
      const result = await client.familyRead({ kind: "time-series", scope });
      const mismatch = familyEchoMismatch(result, "time-series", scope);
      if (mismatch !== null) {
        setFailure({ code: "family.kind-mismatch", message: mismatch, retryable: false });
        return;
      }
      const parsed = parseTimeSeriesPageState(result.state as Json);
      if (parsed.kind === "unrecognized") {
        setRecognized(false);
        return;
      }
      setSeriesName(parsed.state.name);
      setAppliedRange(parsed.state.range);
      if (mode === "append") {
        setEntries((previous) => [...previous, ...parsed.state.entries]);
      } else {
        setEntries(parsed.state.entries);
      }
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
      setBusy(false);
    }
  }

  async function selectSeries(name: string): Promise<void> {
    if (name === seriesName || busy) {
      return;
    }
    setRangeStart("");
    setRangeEnd("");
    await readWindow(name, null, 0, "replace");
  }

  async function applyRange(): Promise<void> {
    if (busy || seriesName.length === 0) {
      return;
    }
    const start = parseEpochMsInput(rangeStart);
    const end = parseEpochMsInput(rangeEnd);
    if (start === null || end === null) {
      setRangeNotice({ kind: "unparseable" });
      return;
    }
    if (start > end) {
      setRangeNotice({ kind: "inverted" });
      return;
    }
    const selection = { start, end };
    if (!rangeWithinViewBound(selection)) {
      setRangeNotice({ kind: "span" });
      return;
    }
    await readWindow(seriesName, selection, 0, "replace");
  }

  async function clearRange(): Promise<void> {
    if (busy || seriesName.length === 0) {
      return;
    }
    setRangeStart("");
    setRangeEnd("");
    await readWindow(seriesName, null, 0, "replace");
  }

  async function loadMore(): Promise<void> {
    if (busy || status === null || status.kind !== "more") {
      return;
    }
    await readWindow(seriesName, appliedRange, status.nextOffset, "append");
  }

  const loadMoreDisabled = busy || status === null || status.kind !== "more";

  return (
    <section
      aria-labelledby="timeseries-panel-heading"
      className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
      data-testid="timeseries-panel"
      data-family-kind="time-series"
    >
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 id="timeseries-panel-heading" className="text-base font-semibold text-ink">
          Time series
        </h2>
        <span className="font-mono text-xs text-ink-faint">family.read · time-series</span>
      </div>
      <p className="mt-1 text-xs text-ink-muted" data-testid="timeseries-range-semantics">
        Range semantics: {TIME_SERIES_RANGE_SEMANTICS} The view&rsquo;s span bound is{" "}
        {formatInteger(TIME_SERIES_RANGE_VIEW_BOUND_MS)} ms (7 days).
      </p>
      {catalog === null ? (
        <p
          className="mt-2 font-mono text-xs text-status-warn"
          data-testid="timeseries-unrecognized"
          role="status"
        >
          The typed time-series catalog state is not the shape this view reads (the host may serve
          the pre-SE-08 family shape); nothing is coerced.
        </p>
      ) : catalog.length === 0 ? (
        <p className="mt-2 text-sm text-ink-muted" data-testid="timeseries-empty">
          No time-series entries are shown: the typed catalog read found no series for this
          document.
        </p>
      ) : (
        <>
          <div className="mt-3 flex flex-wrap items-end gap-3" data-testid="timeseries-controls">
            <label className="flex flex-col gap-1 text-sm text-ink-muted">
              <span>Series</span>
              <select
                value={seriesName}
                onChange={(event) => void selectSeries(event.target.value)}
                disabled={busy}
                aria-label="Select the document's series to read"
                className="rounded-panel border border-line bg-surface-raised px-2 py-1 font-mono text-sm text-ink focus:border-accent focus:outline-none"
              >
                {catalog.map((name) => (
                  <option key={name} value={name}>
                    {name}
                  </option>
                ))}
              </select>
            </label>
            <label className="flex flex-col gap-1 text-sm text-ink-muted">
              <span>Start (epoch ms)</span>
              <input
                type="text"
                inputMode="numeric"
                value={rangeStart}
                onChange={(event) => setRangeStart(event.target.value)}
                disabled={busy}
                aria-label="Range start in epoch milliseconds"
                placeholder="1690000000000"
                className="w-44 rounded-panel border border-line bg-surface-raised px-2 py-1 font-mono text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-ink-muted">
              <span>End (epoch ms)</span>
              <input
                type="text"
                inputMode="numeric"
                value={rangeEnd}
                onChange={(event) => setRangeEnd(event.target.value)}
                disabled={busy}
                aria-label="Range end in epoch milliseconds"
                placeholder="1690003600000"
                className="w-44 rounded-panel border border-line bg-surface-raised px-2 py-1 font-mono text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
              />
            </label>
            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                onClick={() => void applyRange()}
                disabled={busy}
                className="rounded-panel border border-line bg-surface-raised px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent disabled:cursor-default disabled:border-line disabled:text-ink-faint disabled:hover:border-line disabled:hover:text-ink-faint"
              >
                Apply range
              </button>
              <button
                type="button"
                onClick={() => void clearRange()}
                disabled={busy}
                className="rounded-panel border border-line bg-surface-raised px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent disabled:cursor-default disabled:border-line disabled:text-ink-faint disabled:hover:border-line disabled:hover:text-ink-faint"
              >
                Clear range
              </button>
            </div>
          </div>
          {rangeNotice !== null ? (
            <p
              className="mt-2 font-mono text-xs text-status-warn"
              data-testid="timeseries-range-invalid"
              role="status"
            >
              {rangeNotice.kind === "unparseable"
                ? "Both range bounds must be epoch-millisecond integers."
                : rangeNotice.kind === "inverted"
                  ? "The range start must not be later than its end."
                  : `The selected span exceeds the view bound of ${formatInteger(TIME_SERIES_RANGE_VIEW_BOUND_MS)} ms; narrow the range.`}
            </p>
          ) : null}
          <p className="mt-2 text-xs text-ink-faint" data-testid="timeseries-applied-range">
            {appliedRange === null
              ? "No range applied: the read covers the whole series, bounded by the entry window."
              : `Applied range: ${formatInteger(appliedRange.start)} to ${formatInteger(appliedRange.end)} (inclusive).`}
          </p>
          {!recognized ? (
            <p
              className="mt-2 font-mono text-xs text-status-warn"
              data-testid="timeseries-unrecognized"
              role="status"
            >
              The typed time-series state is not the shape this view reads (the host may serve the
              pre-SE-08 family shape); nothing is coerced.
            </p>
          ) : (
            <>
              <p
                className="mt-2 text-sm text-ink-muted"
                data-testid="timeseries-status"
                role="status"
              >
                {status?.kind === "more"
                  ? `Showing ${formatInteger(status.loadedEntries)} of ${formatInteger(status.matched)} series entries`
                  : status?.kind === "complete"
                    ? `All ${formatInteger(status.matched)} series entries in this read are loaded`
                    : `Bounded view window reached: ${formatInteger(status?.retainedPages ?? 0)} pages retained, ${formatInteger((status?.matched ?? 0) - (status?.loadedEntries ?? 0))} of ${formatInteger(status?.matched ?? 0)} series entries not loaded`}
              </p>
              <div className="mt-2 flex flex-wrap items-center gap-3">
                <button
                  type="button"
                  onClick={() => void loadMore()}
                  disabled={loadMoreDisabled}
                  className="rounded-panel border border-line bg-surface-raised px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent disabled:cursor-default disabled:border-line disabled:text-ink-faint disabled:hover:border-line disabled:hover:text-ink-faint"
                >
                  Load more entries
                </button>
                {busy ? (
                  <span className="text-sm text-ink-muted" role="status">
                    Reading…
                  </span>
                ) : null}
              </div>
              {entries.length === 0 ? (
                <p className="mt-3 text-sm text-ink-muted" data-testid="timeseries-no-entries">
                  No series entries are shown: the typed read matched none in this series for the
                  applied range.
                </p>
              ) : (
                <div className="mt-3 overflow-x-auto rounded-panel border border-line">
                  <table className="w-full text-sm" data-testid="timeseries-table">
                    <thead>
                      <tr className="border-b border-line text-left text-xs tracking-wide text-ink-muted uppercase">
                        <th scope="col" className="px-4 py-2 font-medium">
                          Timestamp (epoch ms)
                        </th>
                        <th scope="col" className="px-4 py-2 font-medium">
                          Values
                        </th>
                        <th scope="col" className="px-4 py-2 font-medium">
                          Tag
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {entries.map((entry) => (
                        <tr
                          key={`${entry.timestamp}-${entry.tag ?? ""}`}
                          className="border-b border-line last:border-b-0"
                          data-testid={`timeseries-entry-${entry.timestamp}`}
                        >
                          <td className="px-4 py-2 font-mono text-ink tabular-nums">
                            {formatInteger(entry.timestamp)}
                          </td>
                          <td className="px-4 py-2 font-mono text-xs text-ink-muted">
                            [{entry.values.join(", ")}]
                          </td>
                          <td className="px-4 py-2 font-mono text-xs text-ink-muted">
                            {entry.tag ?? "—"}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
              {malformedCount > 0 ? (
                <p className="mt-2 font-mono text-xs text-status-warn" role="status">
                  {formatInteger(malformedCount)} entries in the loaded windows were malformed and
                  are not shown.
                </p>
              ) : null}
            </>
          )}
        </>
      )}
      {failure !== null ? (
        <p
          className="mt-2 font-mono text-xs text-status-critical"
          data-testid="timeseries-error"
          role="alert"
        >
          Family read failed — code {failure.code}, retryable {String(failure.retryable)}:{" "}
          {failure.message}
        </p>
      ) : null}
    </section>
  );
}
