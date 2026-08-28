"use client";

// The DB-04 live status counters: the home page's Live View over the
// backbone changes stream. The server-rendered engine status stays the
// typed snapshot; this island adds the live layer — changes observed by
// selector kind, the last sequence, the connection state, and every
// host-shed drop — with no polling anywhere (the counters advance on
// events, never on a timer).

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
  feedKindCounts,
  normalizeStreamEvent,
  selectorOf,
  type FeedEvent,
} from "@/components/live/changes-model";

export interface LiveStatusCountersProps {
  readonly apiBaseUrl: string;
  readonly backendContext: BackendContext;
}

export function LiveStatusCounters({ apiBaseUrl, backendContext }: LiveStatusCountersProps) {
  const subscriptionRef = useRef<ChangesSubscription | null>(null);
  const [snapshot, setSnapshot] = useState<ChangesSubscriptionSnapshot | null>(null);
  const [feed, setFeed] = useState<readonly FeedEvent[]>([]);

  useEffect(() => {
    const client = new TerrabaseStudioClient({ baseUrl: apiBaseUrl, backendContext });
    const subscription = client.subscribeChanges({
      onState: (_state, next) => setSnapshot({ ...next }),
      onEvent: (event: StreamChangeEvent) => {
        const answer = normalizeStreamEvent(event);
        if (answer.ok) {
          setFeed((previous) => appendFeedEvent(previous, answer.event).feed);
        }
        setSnapshot({ ...subscription.snapshot() });
      },
      onDrop: () => setSnapshot({ ...subscription.snapshot() }),
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

  const counts = useMemo(() => feedKindCounts(feed), [feed]);
  const state = snapshot?.state ?? "connecting";
  // The last kind to arrive, shown as the stream's pulse (bounded state).
  const lastKind = feed.length === 0 ? null : selectorOf(feed[feed.length - 1].kind);

  return (
    <section
      aria-labelledby="live-status-heading"
      className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="live-status-heading" className="text-base font-semibold text-ink">
          Live status
        </h2>
        <p
          className="flex items-center gap-1.5 text-sm"
          data-testid="live-status-state"
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
            {state === "live" ? "●" : state === "failed" ? "✕" : "◌"}
          </span>
          <span className="text-ink">
            {state === "live"
              ? "Live"
              : state === "failed"
                ? "Failed"
                : state === "reconnecting"
                  ? "Reconnecting"
                  : "Connecting"}
          </span>
        </p>
      </div>
      <p className="mt-1 text-xs text-ink-faint">
        Backbone change counters observed live by this session — no polling; every number below
        advances on an event.
      </p>
      <dl
        className="mt-3 grid grid-cols-2 gap-x-6 gap-y-1 text-sm sm:grid-cols-3"
        data-testid="live-status-counters"
      >
        <Counter label="Changes" value={formatInteger(snapshot?.eventCount ?? 0)} />
        <Counter
          label="Last seq"
          value={
            snapshot === null || snapshot.lastSeq === null ? "—" : formatInteger(snapshot.lastSeq)
          }
        />
        <Counter label="Replayed" value={formatInteger(snapshot?.replayedCount ?? 0)} />
        <Counter label="Duplicates" value={formatInteger(snapshot?.duplicateCount ?? 0)} />
        <Counter
          label="Dropped"
          value={formatInteger(snapshot?.droppedTotal ?? snapshot?.dropCount ?? 0)}
        />
        <Counter label="Last kind" value={lastKind ?? "—"} />
      </dl>
      <div className="mt-3 flex flex-wrap gap-2" data-testid="live-status-kinds">
        {FILTER_KINDS.map((kind) => (
          <span
            key={kind.id}
            className="rounded-full border border-line bg-surface-raised px-2 py-0.5 font-mono text-xs text-ink-muted"
            data-testid={`live-status-kind-${kind.id}`}
          >
            {kind.label} {formatInteger(counts[kind.id] ?? 0)}
          </span>
        ))}
      </div>
      {snapshot !== null && snapshot.droppedTotal !== null && snapshot.droppedTotal > 0 ? (
        <p
          className="mt-2 font-mono text-xs text-status-warn"
          data-testid="live-status-drop-notice"
          role="status"
        >
          {formatInteger(snapshot.droppedTotal)} notifications dropped by the host&apos;s bounded
          queue (surfaced in-band; reconnect resumes from the boundary).
        </p>
      ) : null}
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
