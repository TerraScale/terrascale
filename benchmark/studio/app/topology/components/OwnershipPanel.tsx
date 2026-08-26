// Per-range ownership (DB-07): the bucket-range table entries mapped through
// the studio — each Bucket Range is a contiguous [start, end) interval of
// buckets mapped to one Shard (canonical `shard/<number>` identity), owned by
// exactly one Member at one ownership epoch (the fencing fact: every
// ownership transition of the range raises it).

import { formatInteger } from "@/lib/format";

import type { RangeOwnership } from "../lib/topology-types";

export interface OwnershipPanelProps {
  readonly ranges: readonly RangeOwnership[];
}

export function OwnershipPanel({ ranges }: OwnershipPanelProps) {
  return (
    <section
      aria-labelledby="topology-ownership-heading"
      className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
    >
      <h2 id="topology-ownership-heading" className="text-base font-semibold text-ink">
        Range ownership
      </h2>
      <p className="mt-1 text-xs text-ink-faint">
        Bucket Range entries partition the fixed [0, 2^20) bucket space; each maps to one Shard with
        one owning Member and its ownership epoch.
      </p>
      <div className="mt-3 overflow-x-auto">
        <table className="w-full text-left text-sm" data-testid="topology-ownership">
          <thead className="text-xs tracking-wide text-ink-muted uppercase">
            <tr>
              <th scope="col" className="py-1 pr-4 font-medium">
                Bucket Range
              </th>
              <th scope="col" className="py-1 pr-4 font-medium">
                Buckets
              </th>
              <th scope="col" className="py-1 pr-4 font-medium">
                Shard
              </th>
              <th scope="col" className="py-1 pr-4 font-medium">
                Owner
              </th>
              <th scope="col" className="py-1 font-medium">
                ownership epoch
              </th>
            </tr>
          </thead>
          <tbody className="font-mono text-xs text-ink">
            {ranges.map((entry) => (
              <tr key={entry.shard} className="border-t border-line" data-shard={entry.shard}>
                <td className="py-1.5 pr-4">
                  [{formatInteger(entry.start)}, {formatInteger(entry.end)})
                </td>
                <td className="py-1.5 pr-4 tabular-nums">
                  {formatInteger(entry.end - entry.start)}
                </td>
                <td className="py-1.5 pr-4">{entry.shard}</td>
                <td className="py-1.5 pr-4">{entry.owner}</td>
                <td className="py-1.5 tabular-nums" data-testid={`topology-epoch-${entry.shard}`}>
                  {formatInteger(entry.epoch)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
