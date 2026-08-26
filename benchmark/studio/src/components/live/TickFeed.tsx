"use client";

// The trivial SSE Live View proving the studio's live plumbing end-to-end
// (DB-02): one tick subscription through the typed client, rendered honestly —
// replayed, duplicate, rejected, and resynced frames are counted and surfaced,
// never hidden. The tick list is bounded (last 10 identities only).

import { useEffect, useRef, useState } from "react";

import {
  TerrabaseStudioClient,
  type TickDeltaPayload,
  type TicksSubscriptionSnapshot,
} from "@/api/generated/client";
import { formatInteger, formatUtcTimestamp } from "@/lib/format";

const BOUNDED_TICK_LIST = 10;

const STATE_LABEL: Record<TicksSubscriptionSnapshot["state"], string> = {
  connecting: "Connecting",
  live: "Live",
  reconnecting: "Reconnecting",
  failed: "Failed",
};

const STATE_ICON: Record<TicksSubscriptionSnapshot["state"], string> = {
  connecting: "◌",
  live: "●",
  reconnecting: "⟳",
  failed: "✕",
};

export interface TickFeedProps {
  readonly apiBaseUrl: string;
}

export function TickFeed({ apiBaseUrl }: TickFeedProps) {
  const subscriptionRef = useRef<ReturnType<TerrabaseStudioClient["subscribeTicks"]> | null>(null);
  const [snapshot, setSnapshot] = useState<TicksSubscriptionSnapshot | null>(null);
  const [ticks, setTicks] = useState<TickDeltaPayload[]>([]);

  useEffect(() => {
    const client = new TerrabaseStudioClient({ baseUrl: apiBaseUrl });
    const subscription = client.subscribeTicks({
      onTick: (tick) => {
        setTicks((previous) => [...previous.slice(BOUNDED_TICK_LIST - 1), tick]);
        setSnapshot(subscription.snapshot());
      },
      onState: (_state, next) => setSnapshot({ ...next }),
      onRejected: () => setSnapshot(subscription.snapshot()),
      onResync: () => setSnapshot(subscription.snapshot()),
      onReplayed: () => setSnapshot(subscription.snapshot()),
      onFatal: () => setSnapshot(subscription.snapshot()),
    });
    subscriptionRef.current = subscription;
    setSnapshot(subscription.snapshot());
    return () => {
      subscription.close();
      subscriptionRef.current = null;
    };
  }, [apiBaseUrl]);

  const state = snapshot?.state ?? "connecting";
  const stateLabel = STATE_LABEL[state];
  const lastTick = ticks.at(-1);

  return (
    <section aria-labelledby="tick-feed-heading" className="grid gap-4">
      <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="tick-feed-heading" className="text-base font-semibold text-ink">
            Tick feed
          </h2>
          <p
            className="flex items-center gap-1.5 text-sm"
            data-testid="tick-connection-state"
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
            <span className="text-ink">{stateLabel}</span>
            <span className="sr-only">connection</span>
          </p>
        </div>
        <p className="mt-1 text-xs text-ink-faint">
          One SSE subscription through the typed client. The fixture drops the connection
          periodically; ticks resume from the last checkpoint with replayed identities counted.
        </p>
        <dl
          className="mt-3 grid grid-cols-2 gap-x-6 gap-y-1 text-sm sm:grid-cols-4"
          data-testid="tick-counters"
        >
          <Counter
            label="Ticks"
            value={snapshot === null ? "0" : formatInteger(snapshot.tickCount)}
          />
          <Counter
            label="Replayed"
            value={snapshot === null ? "0" : formatInteger(snapshot.replayedCount)}
          />
          <Counter
            label="Duplicates"
            value={snapshot === null ? "0" : formatInteger(snapshot.duplicateCount)}
          />
          <Counter
            label="Rejected"
            value={snapshot === null ? "0" : formatInteger(snapshot.rejectedCount)}
          />
          <Counter
            label="Reconnects"
            value={snapshot === null ? "0" : formatInteger(snapshot.reconnectCount)}
          />
          <Counter
            label="Resyncs"
            value={snapshot === null ? "0" : formatInteger(snapshot.resyncCount)}
          />
          <Counter
            label="Last seq"
            value={
              snapshot?.lastSeq === null || snapshot === null
                ? "—"
                : formatInteger(snapshot.lastSeq)
            }
          />
          <Counter
            label="Latest counter"
            value={lastTick === undefined ? "—" : formatInteger(lastTick.counter)}
          />
        </dl>
        {snapshot === null || snapshot.lastError === null ? null : (
          <p
            className="mt-2 font-mono text-xs text-status-warn"
            data-testid="tick-last-error"
            role="status"
          >
            {snapshot.lastError}
          </p>
        )}
      </div>

      <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <h3 className="text-sm font-semibold text-ink">
          Latest ticks (bounded to {BOUNDED_TICK_LIST})
        </h3>
        {ticks.length === 0 ? (
          <p className="mt-2 text-sm text-ink-muted" data-testid="tick-list-empty">
            Waiting for the first tick…
          </p>
        ) : (
          <ul className="mt-2 grid gap-1 font-mono text-xs text-ink" data-testid="tick-list">
            {ticks.map((tick) => (
              <li key={tick.seq} className="flex gap-3 rounded bg-surface-raised px-2 py-1">
                <span className="text-ink-faint">seq {tick.seq}</span>
                <span>counter {tick.counter}</span>
                <span className="text-ink-faint">{formatUtcTimestamp(tick.emittedAt)}</span>
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
