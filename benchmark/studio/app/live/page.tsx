import type { Metadata } from "next";

import { ChangesFeed } from "@/components/live/ChangesFeed";
import { TickFeed } from "@/components/live/TickFeed";
import { apiBaseUrl, studioBackendContext } from "@/lib/server/studio-client";

// The Live View (DB-02's plumbing proof, DB-04's changes feed): the recent-
// changes feed renders the backbone's selector kinds with V1-parity filters
// and conflict content restrictions, and the tick island keeps proving the
// session/resume plumbing end-to-end. The page itself stays a server
// component; only the islands are client-side, and they talk exclusively
// through the typed client.
//
// Dynamic: the API base URL and backend context resolve per request from the
// environment, so one build serves any local host or fixture.
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Live view — TerraBase Studio",
};

export default function LivePage() {
  return (
    <div className="grid gap-4">
      <div>
        <h1 className="text-xl font-semibold text-ink">Live view</h1>
        <p className="mt-1 max-w-2xl text-sm text-ink-muted">
          The backbone changes stream through the API host&apos;s subscription endpoint: every
          selector kind renders with filters, reconnects resume from checkpoints, and drops are
          counted and surfaced — never hidden.
        </p>
      </div>
      <ChangesFeed apiBaseUrl={apiBaseUrl()} backendContext={studioBackendContext()} />
      <TickFeed apiBaseUrl={apiBaseUrl()} />
    </div>
  );
}
