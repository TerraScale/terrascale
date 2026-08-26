"use client";

// Member status with the polled fallback (DB-07): the status enum is the
// engine's ReplicationStatus, unchanged. Until the Live View delivery lands
// with DB-04, status transitions render through interval polling of the same
// typed topology read; when DB-04 lands, this island swaps its transport for
// change-event-backbone delivery and the enum stays exactly as it is. Health
// is a health observation only — it never carries or implies a Convergence
// fact.

import { useEffect, useState } from "react";

import { TerrabaseApiError } from "@/api/generated/client";

import { STATUS_LABEL, STATUS_TONE } from "../lib/topology-labels";
import type { TopologyMemberSeat } from "../lib/topology-types";
import { TopologyClient } from "../lib/topology-client";

const POLL_INTERVAL_MS = 2000;

const TONE_CLASS: Record<"live" | "warn" | "critical", string> = {
  live: "text-status-live",
  warn: "text-status-warn",
  critical: "text-status-critical",
};

export interface MemberStatusPanelProps {
  readonly apiBaseUrl: string;
  readonly initialMembers: readonly TopologyMemberSeat[];
  readonly initialLaneTick: number;
}

export function MemberStatusPanel({
  apiBaseUrl,
  initialMembers,
  initialLaneTick,
}: MemberStatusPanelProps) {
  const [members, setMembers] = useState(initialMembers);
  const [laneTick, setLaneTick] = useState(initialLaneTick);
  const [pollCount, setPollCount] = useState(0);
  const [lastError, setLastError] = useState<string | null>(null);

  useEffect(() => {
    const client = new TopologyClient({ baseUrl: apiBaseUrl });
    let cancelled = false;
    const poll = async () => {
      try {
        const snapshot = await client.readTopology();
        if (cancelled) {
          return;
        }
        setMembers(snapshot.members);
        setLaneTick(snapshot.laneTick);
        setPollCount((count) => count + 1);
        setLastError(null);
      } catch (error) {
        if (cancelled) {
          return;
        }
        // A failed poll keeps the last observed facts on screen and states
        // the failure; it never blanks the view.
        setLastError(
          error instanceof TerrabaseApiError ? `${error.code}: ${error.message}` : String(error),
        );
      }
    };
    const timer = setInterval(poll, POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [apiBaseUrl]);

  return (
    <section
      aria-labelledby="topology-members-heading"
      className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
    >
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 id="topology-members-heading" className="text-base font-semibold text-ink">
          Members
        </h2>
        <p className="font-mono text-xs text-ink-faint tabular-nums" data-testid="topology-lane">
          lane synthetic · lane tick <span data-testid="topology-lane-tick">{laneTick}</span>
        </p>
      </div>
      <p className="mt-1 text-xs text-ink-faint" data-testid="topology-poll-note">
        Polled fallback (pre-DB-04): member status refreshes on an interval through the typed
        topology read; Live View delivery lands with DB-04. Status is a health observation; it never
        carries or implies a Convergence fact.
      </p>
      <div className="mt-3 overflow-x-auto">
        <table className="w-full text-left text-sm" data-testid="topology-members">
          <thead className="text-xs tracking-wide text-ink-muted uppercase">
            <tr>
              <th scope="col" className="py-1 pr-4 font-medium">
                Member
              </th>
              <th scope="col" className="py-1 pr-4 font-medium">
                Region
              </th>
              <th scope="col" className="py-1 pr-4 font-medium">
                Failure Domain
              </th>
              <th scope="col" className="py-1 font-medium">
                Status
              </th>
            </tr>
          </thead>
          <tbody className="font-mono text-xs text-ink">
            {members.map((seat) => {
              const tone = STATUS_TONE.get(seat.status) ?? "warn";
              return (
                <tr key={seat.member} className="border-t border-line" data-member={seat.member}>
                  <td className="py-1.5 pr-4">{seat.member}</td>
                  <td className="py-1.5 pr-4">{seat.region}</td>
                  <td className="py-1.5 pr-4">{seat.failureDomain}</td>
                  <td className="py-1.5">
                    <span
                      className={`font-medium ${TONE_CLASS[tone]}`}
                      data-status={seat.status}
                      data-testid={`topology-status-${seat.member}`}
                    >
                      {STATUS_LABEL.get(seat.status) ?? seat.status}
                    </span>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <p className="mt-2 font-mono text-xs text-ink-faint tabular-nums">
        polls <span data-testid="topology-poll-count">{pollCount}</span>
      </p>
      {lastError === null ? null : (
        <p
          className="mt-1 font-mono text-xs text-status-warn"
          data-testid="topology-poll-error"
          role="status"
        >
          last poll failed, showing last observed facts: {lastError}
        </p>
      )}
    </section>
  );
}
