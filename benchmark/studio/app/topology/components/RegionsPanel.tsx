// Regions and Failure Domains (DB-07): one Region is a named failure and
// latency domain; a Failure Domain is a set of resources whose correlated
// loss is treated as one failure (CONTEXT.md). Grouping here follows the
// membership view exactly — a Failure Domain never spans Regions.

import type { TopologyMemberSeat } from "../lib/topology-types";

export interface RegionsPanelProps {
  readonly regions: readonly string[];
  readonly members: readonly TopologyMemberSeat[];
}

export function RegionsPanel({ regions, members }: RegionsPanelProps) {
  return (
    <section
      aria-labelledby="topology-regions-heading"
      className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
    >
      <h2 id="topology-regions-heading" className="text-base font-semibold text-ink">
        Regions
      </h2>
      <p className="mt-1 text-xs text-ink-faint">
        Each Region is a named failure and latency domain; each Failure Domain groups Members whose
        correlated loss is one failure.
      </p>
      <ul className="mt-3 grid gap-3 sm:grid-cols-3" data-testid="topology-regions">
        {regions.map((region) => (
          <RegionCard key={region} region={region} members={members} />
        ))}
      </ul>
    </section>
  );
}

function RegionCard({
  region,
  members,
}: {
  region: string;
  members: readonly TopologyMemberSeat[];
}) {
  const seated = members.filter((seat) => seat.region === region);
  const domains = [...new Set(seated.map((seat) => seat.failureDomain))];
  return (
    <li
      className="rounded-panel border border-line bg-surface-raised px-3 py-2"
      data-region={region}
    >
      <p className="font-mono text-sm text-ink">{region}</p>
      <p className="mt-1 text-xs text-ink-muted">
        {seated.length} Member{seated.length === 1 ? "" : "s"}
      </p>
      <ul className="mt-1 grid gap-0.5 text-xs text-ink-faint">
        {domains.map((domain) => (
          <li key={domain} data-failure-domain={domain}>
            Failure Domain {domain}
          </li>
        ))}
      </ul>
    </li>
  );
}
