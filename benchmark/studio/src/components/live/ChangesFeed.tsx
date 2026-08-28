"use client";

// The DB-04 live recent-changes feed: the studio's dogfooding Live View over
// the C# API host's backbone subscription. Every selector kind the backbone
// exposes renders (documents — including the wire put/delete forms —
// indexes, counters, time series, operations, database-level changes, and
// conflicts), filterable like V1's Changes selectors. Conflict events render
// under the ConflictRecord content restriction: the metadata projection
// (tenant, operations, members, clocks, outcome) — a document body never
// crosses, structurally (changes-model's allowlist). Backpressure honesty:
// host-shed drops surface in-band with their resume boundary; replayed
// backlogs and at-least-once duplicates are counted, never hidden. The
// rendered list is bounded (the newest identities); the counters are the
// full accounting.

import { useEffect, useMemo, useRef, useState } from "react";

import {
  TerrabaseStudioClient,
  type BackendContext,
  type ChangesSubscription,
  type ChangesSubscriptionSnapshot,
  type StreamChangeEvent,
} from "@/api/generated/client";
import { formatInteger } from "@/lib/format";
import {
  FILTER_KINDS,
  appendFeedEvent,
  collectionMatches,
  dropNotice,
  feedKindCounts,
  kindMatchesFilter,
  normalizeStreamEvent,
  type ChangeKind,
  type FeedEvent,
} from "@/components/live/changes-model";

const KIND_LABEL: Record<ChangeKind, string> = {
  put: "put",
  delete: "delete",
  document: "document",
  index: "index",
  counter: "counter",
  "time-series": "time-series",
  operation: "operation",
  database: "database",
  conflict: "conflict",
};

const STATE_LABEL: Record<ChangesSubscriptionSnapshot["state"], string> = {
  connecting: "Connecting",
  live: "Live",
  reconnecting: "Reconnecting",
  failed: "Failed",
};

const STATE_ICON: Record<ChangesSubscriptionSnapshot["state"], string> = {
  connecting: "◌",
  live: "●",
  reconnecting: "⟳",
  failed: "✕",
};

export interface ChangesFeedProps {
  readonly apiBaseUrl: string;
  readonly backendContext: BackendContext;
}

export function ChangesFeed({ apiBaseUrl, backendContext }: ChangesFeedProps) {
  const subscriptionRef = useRef<ChangesSubscription | null>(null);
  const [snapshot, setSnapshot] = useState<ChangesSubscriptionSnapshot | null>(null);
  const [feed, setFeed] = useState<readonly FeedEvent[]>([]);
  const [restricted, setRestricted] = useState(0);
  const [lastDrop, setLastDrop] = useState<{ dropped: number; resumeFrom: number } | null>(null);
  // Every selector kind renders by default (the V1 forAll* selectors with no
  // refinement); unchecking hides a kind. An empty selection falls back to
  // accepting everything — a filter that accepts nothing is not a view.
  const [selected, setSelected] = useState<ReadonlySet<ChangeKind>>(
    () => new Set(FILTER_KINDS.map((kind) => kind.id)),
  );
  const [collectionFilter, setCollectionFilter] = useState("");

  useEffect(() => {
    const client = new TerrabaseStudioClient({ baseUrl: apiBaseUrl, backendContext });
    const subscription = client.subscribeChanges({
      onState: (_state, next) => setSnapshot({ ...next }),
      onEvent: (event: StreamChangeEvent, _meta: { replayed: boolean }) => {
        // The model's normalization is the content-restriction boundary:
        // a conflict event projects only its metadata allowlist, and an
        // event that violates the shape is counted restricted, not shown.
        const answer = normalizeStreamEvent(event);
        if (!answer.ok) {
          setRestricted((count) => count + 1);
          return;
        }
        setFeed((previous) => appendFeedEvent(previous, answer.event).feed);
        setSnapshot({ ...subscription.snapshot() });
      },
      onDrop: (drop) => {
        setLastDrop({ dropped: drop.dropped, resumeFrom: drop.resumeFrom });
        setSnapshot({ ...subscription.snapshot() });
      },
      onRejected: () => setSnapshot({ ...subscription.snapshot() }),
      onFatal: () => setSnapshot({ ...subscription.snapshot() }),
    });
    subscriptionRef.current = subscription;
    setSnapshot(subscription.snapshot());
    return () => {
      subscription.close();
      subscriptionRef.current = null;
    };
  }, [apiBaseUrl, backendContext]);

  const state = snapshot?.state ?? "connecting";
  const counts = useMemo(() => feedKindCounts(feed), [feed]);
  const filtered = useMemo(
    () =>
      feed.filter(
        (event) => kindMatchesFilter(event, selected) && collectionMatches(event, collectionFilter),
      ),
    [feed, selected, collectionFilter],
  );

  function toggleKind(kind: ChangeKind): void {
    setSelected((previous) => {
      const next = new Set(previous);
      if (next.has(kind)) {
        next.delete(kind);
      } else {
        next.add(kind);
      }
      return next;
    });
  }

  return (
    <section aria-labelledby="changes-feed-heading" className="grid gap-4">
      <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="changes-feed-heading" className="text-base font-semibold text-ink">
            Recent changes
          </h2>
          <p
            className="flex items-center gap-1.5 text-sm"
            data-testid="changes-connection-state"
            data-connection-state={state}
          >
            <span
              aria-hidden="true"
              className={
                state === "live"
                  ? "text-status-live"
                  : state === "failed"
                    ? "text-status-critical"
                    : "text-status-warn"
              }
            >
              {STATE_ICON[state]}
            </span>
            <span className="text-ink">{STATE_LABEL[state]}</span>
            <span className="sr-only">connection</span>
          </p>
        </div>
        <p className="mt-1 text-xs text-ink-faint">
          The backbone changes stream through the API host&apos;s subscription endpoint.
          Content-free events only; conflicts render their ConflictRecord metadata, never bodies.
        </p>
        <dl
          className="mt-3 grid grid-cols-2 gap-x-6 gap-y-1 text-sm sm:grid-cols-4"
          data-testid="changes-counters"
        >
          <Counter label="Events" value={formatInteger(snapshot?.eventCount ?? 0)} />
          <Counter label="Replayed" value={formatInteger(snapshot?.replayedCount ?? 0)} />
          <Counter label="Duplicates" value={formatInteger(snapshot?.duplicateCount ?? 0)} />
          <Counter
            label="Rejected"
            value={formatInteger((snapshot?.rejectedCount ?? 0) + restricted)}
          />
          <Counter label="Reconnects" value={formatInteger(snapshot?.reconnectCount ?? 0)} />
          <Counter
            label="Drops"
            value={formatInteger(snapshot?.droppedTotal ?? snapshot?.dropCount ?? 0)}
          />
          <Counter
            label="Last seq"
            value={
              snapshot?.lastSeq === null || snapshot === null || snapshot.lastSeq === null
                ? "—"
                : formatInteger(snapshot.lastSeq)
            }
          />
          <Counter label="Restricted" value={formatInteger(restricted)} />
        </dl>
        {lastDrop !== null ? (
          <p
            className="mt-2 rounded-panel border border-status-warn/40 bg-surface-raised px-3 py-2 font-mono text-xs text-status-warn"
            data-testid="changes-drop-notice"
            role="status"
          >
            {dropNotice(lastDrop)}
          </p>
        ) : null}
        {snapshot !== null && snapshot.lastError !== null ? (
          <p
            className="mt-2 font-mono text-xs text-status-warn"
            data-testid="changes-last-error"
            role="status"
          >
            {snapshot.lastError}
          </p>
        ) : null}
      </div>

      <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <h3 className="text-sm font-semibold text-ink">Filters</h3>
        <p className="mt-1 text-xs text-ink-faint">
          The V1 Changes selector families (TerraBase.Client/Documents/Changes); an empty selection
          accepts every kind.
        </p>
        <div className="mt-2 flex flex-wrap gap-2" data-testid="changes-kind-filters">
          {FILTER_KINDS.map((kind) => (
            <label
              key={kind.id}
              className="flex cursor-pointer items-center gap-1.5 rounded-panel border border-line bg-surface-raised px-2.5 py-1 text-sm text-ink transition-colors hover:border-accent"
              title={`V1 selector: ${kind.v1Selector}`}
            >
              <input
                type="checkbox"
                checked={selected.has(kind.id)}
                onChange={() => toggleKind(kind.id)}
                data-testid={`changes-filter-${kind.id}`}
              />
              <span>{kind.label}</span>
              <span className="font-mono text-xs text-ink-faint tabular-nums">
                {formatInteger(counts[kind.id] ?? 0)}
              </span>
            </label>
          ))}
        </div>
        <label className="mt-3 flex items-center gap-2 text-sm text-ink-muted">
          <span>Collection</span>
          <input
            type="search"
            value={collectionFilter}
            onChange={(event) => setCollectionFilter(event.target.value)}
            aria-label="Filter changes by collection prefix"
            placeholder="e.g. orders"
            className="w-48 rounded-panel border border-line bg-surface-raised px-2 py-1 font-mono text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
          />
        </label>
        <p className="mt-1 text-xs text-ink-faint" data-testid="changes-filter-note">
          Conflict events carry no collection identity, so a collection filter does not observe them
          (the backbone&apos;s recorded limit).
        </p>
      </div>

      <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <h3 className="text-sm font-semibold text-ink">
          Latest changes (bounded to the newest {filtered.length} shown)
        </h3>
        {filtered.length === 0 ? (
          <p className="mt-2 text-sm text-ink-muted" data-testid="changes-empty">
            No changes match the filters yet — waiting for the backbone.
          </p>
        ) : (
          <ul className="mt-2 grid gap-1 font-mono text-xs text-ink" data-testid="changes-list">
            {[...filtered].reverse().map((event) => (
              <li
                key={event.sequence}
                data-testid={`change-row-${event.sequence}`}
                data-kind={event.kind}
                className="grid gap-1 rounded bg-surface-raised px-2 py-1 sm:grid-cols-[6rem_7rem_1fr]"
              >
                <span className="text-ink-faint">seq {formatInteger(event.sequence)}</span>
                <span
                  className={event.kind === "conflict" ? "text-status-critical" : "text-accent"}
                >
                  {KIND_LABEL[event.kind]}
                </span>
                {event.kind === "conflict" && event.conflict !== null ? (
                  <span className="text-ink-muted" data-testid="conflict-metadata">
                    tenant {event.conflict.tenant} · ops {event.conflict.operations.join(", ")} ·
                    members {event.conflict.members.join(", ")} · clocks{" "}
                    {event.conflict.clocks.join(", ")} · outcome {event.conflict.outcome}
                  </span>
                ) : (
                  <span className="truncate text-ink-muted">
                    {event.collection ?? "—"} / {event.document ?? "—"} · op {event.operationId}
                  </span>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}

function Counter({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex gap-2" data-counter={label}>
      <dt className="text-ink-muted">{label}</dt>
      <dd className="font-mono tabular-nums text-ink">{value}</dd>
    </div>
  );
}
