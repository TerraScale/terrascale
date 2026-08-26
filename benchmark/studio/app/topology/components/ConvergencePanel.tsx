// Convergence and replication lag (DB-07): the engine's Convergence
// observation rendered verbatim — which Members hold the operation identity,
// each as its own local publication fact, observed at a lane tick — plus the
// per-Member replication lag as a storage-sequence distance. Convergence is
// its own observation, never inferred from an acknowledgement, and the
// measurement definitions below are the engine's, carried on the read.

import { formatInteger } from "@/lib/format";

import { LimitsList } from "./LimitsList";
import type { ConvergenceReport } from "../lib/topology-types";

export interface ConvergencePanelProps {
  readonly report: ConvergenceReport;
}

export function ConvergencePanel({ report }: ConvergencePanelProps) {
  return (
    <section
      aria-labelledby="topology-convergence-heading"
      className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
    >
      <h2 id="topology-convergence-heading" className="text-base font-semibold text-ink">
        Convergence
      </h2>
      <p className="mt-1 text-xs text-ink-faint">
        Members have reached the contract-defined state for a replicated operation; the
        acknowledgement level is observable separately.
      </p>

      {report.convergence === null ? (
        <p className="mt-3 text-sm text-ink-muted" data-testid="topology-convergence-pending">
          No Convergence observation in this scenario yet.
        </p>
      ) : (
        <dl
          className="mt-3 grid gap-x-6 gap-y-1 text-sm"
          data-testid="topology-convergence"
          data-convergence-operation={report.convergence.operation}
        >
          <div className="flex gap-2">
            <dt className="text-ink-muted">Operation</dt>
            <dd className="font-mono text-ink">{report.convergence.operation}</dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">Held by</dt>
            <dd className="font-mono text-ink">
              {report.convergence.holdings.map((holding) => holding.member).join(", ")}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">Holding level</dt>
            <dd className="font-mono text-ink">
              {report.convergence.holdings
                .map((holding) => holding.level)
                .filter((level, index, all) => all.indexOf(level) === index)
                .join(", ")}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">Observed at (lane tick)</dt>
            <dd className="font-mono text-ink tabular-nums">
              {formatInteger(report.convergence.observedAt)}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">Condition</dt>
            <dd className="text-ink">{report.condition}</dd>
          </div>
        </dl>
      )}

      <h3 className="mt-4 text-sm font-semibold text-ink">Replication lag</h3>
      <p className="mt-1 text-xs text-ink-faint">
        The storage-sequence distance between a Member&rsquo;s durable replication cursor and the
        source&rsquo;s published sequence.
      </p>
      <div className="mt-2 overflow-x-auto">
        <table className="w-full text-left text-sm" data-testid="topology-lag">
          <thead className="text-xs tracking-wide text-ink-muted uppercase">
            <tr>
              <th scope="col" className="py-1 pr-4 font-medium">
                Member
              </th>
              <th scope="col" className="py-1 pr-4 font-medium">
                Source high water
              </th>
              <th scope="col" className="py-1 pr-4 font-medium">
                Member high water
              </th>
              <th scope="col" className="py-1 font-medium">
                storage-sequence distance
              </th>
            </tr>
          </thead>
          <tbody className="font-mono text-xs text-ink">
            {report.lag.map((entry) => (
              <tr key={entry.member} className="border-t border-line" data-member={entry.member}>
                <td className="py-1.5 pr-4">{entry.member}</td>
                <td className="py-1.5 pr-4 tabular-nums">{formatInteger(entry.sourceHighWater)}</td>
                <td className="py-1.5 pr-4 tabular-nums">{formatInteger(entry.memberHighWater)}</td>
                <td className="py-1.5 tabular-nums" data-distance={entry.distance}>
                  {formatInteger(entry.distance)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <h3 className="mt-4 text-sm font-semibold text-ink">Measurement definitions</h3>
      <blockquote
        className="mt-1 border-l-2 border-line pl-3 text-xs text-ink-muted"
        data-testid="topology-definitions"
      >
        <p>{report.definitions.convergence}</p>
        <p className="mt-1">{report.definitions.lag}</p>
      </blockquote>
      <LimitsList limits={report.limits} />
    </section>
  );
}
