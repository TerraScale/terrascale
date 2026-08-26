// Engine status view model rendering — a server component fed exclusively by
// the typed `engine.status` read (never raw fetch, never engine internals).

import type { EngineStatus } from "@/api/generated/client";
import { StatTile } from "@/components/ui/StatTile";
import { formatBytes, formatDurationMs, formatInteger, formatUtcTimestamp } from "@/lib/format";

export interface EngineStatusPanelProps {
  readonly status: EngineStatus;
}

export function EngineStatusPanel({ status }: EngineStatusPanelProps) {
  return (
    <section aria-labelledby="engine-status-heading" className="grid gap-4">
      <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <h2 id="engine-status-heading" className="text-base font-semibold text-ink">
          Engine status
        </h2>
        <dl className="mt-2 grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
          <div className="flex gap-2">
            <dt className="text-ink-muted">Engine</dt>
            <dd className="font-mono text-ink" data-testid="engine-id">
              {status.engineId}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">Version</dt>
            <dd className="font-mono text-ink" data-testid="engine-version">
              {status.engineVersion}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">Role</dt>
            <dd>
              <span
                className="rounded-full border border-line bg-surface-raised px-2 py-0.5 font-mono text-xs text-ink"
                data-testid="engine-role"
              >
                {status.role}
              </span>
            </dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">Started</dt>
            <dd className="font-mono text-ink" data-testid="engine-started-at">
              {formatUtcTimestamp(status.startedAt)}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">Uptime</dt>
            <dd className="font-mono text-ink" data-testid="engine-uptime">
              {formatDurationMs(status.uptimeMs)}
            </dd>
          </div>
        </dl>
      </div>

      <div className="grid gap-3 sm:grid-cols-3" data-testid="storage-tiles">
        <StatTile label="Collections" value={formatInteger(status.storage.collections)} />
        <StatTile label="Documents" value={formatInteger(status.storage.documents)} />
        <StatTile
          label="WAL"
          value={formatBytes(status.storage.walBytes)}
          hint="write-ahead log size"
        />
      </div>

      <div className="grid gap-3 sm:grid-cols-4" data-testid="counter-tiles">
        <StatTile label="Reads" value={formatInteger(status.counters.reads)} />
        <StatTile label="Writes" value={formatInteger(status.counters.writes)} />
        <StatTile label="Queries" value={formatInteger(status.counters.queries)} />
        <StatTile label="Family ops" value={formatInteger(status.counters.familyOperations)} />
      </div>
    </section>
  );
}
