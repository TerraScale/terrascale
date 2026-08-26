// Acknowledgement Level panel (DB-07): the engine's AcknowledgementReport
// rendered verbatim per recent write — the requested and observed levels, the
// Regions with the Members that contributed, the observation lane tick, and
// the degenerate normalization when one applies. The level is part of the
// public contract and is distinct from local receipt; it is never rendered as
// a delivery state.

import { formatInteger } from "@/lib/format";

import { LimitsList } from "./LimitsList";
import { ACK_LEVEL_LABEL } from "../lib/topology-labels";
import type { AcknowledgementReportWire, RecentWritesReport } from "../lib/topology-types";

export interface AcknowledgementPanelProps {
  readonly report: RecentWritesReport;
}

export function AcknowledgementPanel({ report }: AcknowledgementPanelProps) {
  return (
    <section
      aria-labelledby="topology-acknowledgement-heading"
      className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
    >
      <h2 id="topology-acknowledgement-heading" className="text-base font-semibold text-ink">
        Acknowledgement Level
      </h2>
      <p className="mt-1 text-xs text-ink-faint">
        The strongest state a Region or Member has confirmed for each recent replicated write —
        distinct from local receipt.
      </p>
      <div className="mt-3 overflow-x-auto">
        <table className="w-full text-left text-sm" data-testid="topology-acknowledgement">
          <thead className="text-xs tracking-wide text-ink-muted uppercase">
            <tr>
              <th scope="col" className="py-1 pr-4 font-medium">
                Operation
              </th>
              <th scope="col" className="py-1 pr-4 font-medium">
                Requested
              </th>
              <th scope="col" className="py-1 pr-4 font-medium">
                Observed
              </th>
              <th scope="col" className="py-1 pr-4 font-medium">
                Contributed
              </th>
              <th scope="col" className="py-1 font-medium">
                Observed at (lane tick)
              </th>
            </tr>
          </thead>
          <tbody className="font-mono text-xs text-ink">
            {report.writes.length === 0 ? (
              <tr>
                <td className="py-1.5 text-ink-muted" colSpan={5} data-testid="topology-ack-empty">
                  No replicated writes in this scenario yet.
                </td>
              </tr>
            ) : (
              report.writes.map((write) => <WriteRow key={write.operation} write={write} />)
            )}
          </tbody>
        </table>
      </div>
      <LimitsList limits={report.limits} />
    </section>
  );
}

function WriteRow({ write }: { write: AcknowledgementReportWire }) {
  const contributed = write.contributed
    .map((part) => `${part.region}: ${part.members.join(", ")}`)
    .join(" · ");
  return (
    <tr className="border-t border-line" data-operation={write.operation}>
      <td className="py-1.5 pr-4">{write.operation}</td>
      <td className="py-1.5 pr-4">{level(write.requested)}</td>
      <td className="py-1.5 pr-4">
        {level(write.observed)}
        {write.normalization === null ? null : (
          <span className="text-ink-faint"> (normalized from {level(write.normalization[0])})</span>
        )}
      </td>
      <td className="py-1.5 pr-4 text-ink-muted">{contributed}</td>
      <td className="py-1.5 tabular-nums">{formatInteger(write.observedAt)}</td>
    </tr>
  );
}

function level(wire: AcknowledgementReportWire["requested"]): string {
  return ACK_LEVEL_LABEL.get(wire) ?? wire;
}
