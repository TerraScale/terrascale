import type { Metadata } from "next";
import { redirect } from "next/navigation";

import { TerrabaseApiError } from "@/api/generated/client";
import { EffectButton } from "@/components/ui/EffectButton";
import { apiBaseUrl } from "@/lib/server/studio-client";
import { resolveLoginUrl } from "@/lib/server/session";

import { AcknowledgementPanel } from "./components/AcknowledgementPanel";
import { ConvergencePanel } from "./components/ConvergencePanel";
import { MemberStatusPanel } from "./components/MemberStatusPanel";
import { OwnershipPanel } from "./components/OwnershipPanel";
import { RegionsPanel } from "./components/RegionsPanel";
import { TopologyClient } from "./lib/topology-client";

// Topology view (DB-07): the deployment's membership picture in the charter's
// exact vocabulary — Regions, Members, Failure Domains, per-range ownership
// with ownership epochs, the Acknowledgement Level per recent write, and
// Convergence with replication lag — rendered from typed topology reads
// through the API host (the dev fixture's synthetic lane until the
// replication data plane lands). No private control-plane data (Platform
// provisioning, billing, routing) appears on this route: the membership view
// is a data-plane fact. Loading and error states are real routes; an
// unauthenticated session redirects to the host's login flow exactly as the
// host directs.
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Topology — TerraBase Studio",
};

export default async function TopologyPage() {
  const baseUrl = apiBaseUrl();
  const client = new TopologyClient({ baseUrl });
  try {
    const [snapshot, writes, convergence] = await Promise.all([
      client.readTopology(),
      client.readRecentWrites(),
      client.readConvergence(),
    ]);

    return (
      <div className="grid gap-4">
        <div>
          <h1 className="text-xl font-semibold text-ink">Topology</h1>
          <p className="mt-1 max-w-2xl text-sm text-ink-muted">
            The deployment&rsquo;s membership view: Regions, Members, and Failure Domains; per-range
            ownership with ownership epochs; the Acknowledgement Level observed per recent write;
            and Convergence with replication lag. Read through typed topology operations — values on
            this route report from the synthetic lane (dev fixture) until the replication data plane
            lands, and every panel carries its lane and qualification limits. No production claims.
          </p>
        </div>
        <RegionsPanel regions={snapshot.regions} members={snapshot.members} />
        <MemberStatusPanel
          apiBaseUrl={baseUrl}
          initialMembers={snapshot.members}
          initialLaneTick={snapshot.laneTick}
        />
        <OwnershipPanel ranges={snapshot.ranges} />
        <AcknowledgementPanel report={writes} />
        <ConvergencePanel report={convergence} />
      </div>
    );
  } catch (error) {
    if (error instanceof TerrabaseApiError) {
      if (
        error.code === "session.unauthenticated" &&
        error.loginUrl !== null &&
        error.loginUrl.length > 0
      ) {
        redirect(resolveLoginUrl(baseUrl, error.loginUrl));
      }
      // The typed failure is a designed route; only unexpected errors reach
      // the segment's error boundary.
      return (
        <TopologyReadError code={error.code} message={error.message} retryable={error.retryable} />
      );
    }
    throw error;
  }
}

function TopologyReadError({
  code,
  message,
  retryable,
}: {
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
}) {
  return (
    <section
      aria-labelledby="topology-error-title"
      className="grid w-full max-w-md gap-4 rounded-panel border border-line bg-surface p-6 shadow-panel"
      data-testid="topology-error"
    >
      <div className="grid gap-2">
        <h1 id="topology-error-title" className="text-xl font-semibold text-ink">
          Topology unavailable
        </h1>
        <p className="text-sm text-ink-muted">
          The typed topology read failed. The error identity below comes from the API host envelope;
          no credentials or document bodies are carried in error payloads.
        </p>
        <dl className="grid gap-1 font-mono text-xs text-ink-muted">
          <div className="flex gap-2">
            <dt>code</dt>
            <dd className="text-ink" data-testid="topology-error-code">
              {code}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt>retryable</dt>
            <dd className="text-ink" data-testid="topology-error-retryable">
              {String(retryable)}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt>message</dt>
            <dd className="text-ink">{message}</dd>
          </div>
        </dl>
      </div>
      <div className="flex flex-wrap gap-2">
        <EffectButton asLink href="/topology">
          Try again
        </EffectButton>
        <EffectButton asLink href="/">
          Return to studio home
        </EffectButton>
      </div>
    </section>
  );
}
