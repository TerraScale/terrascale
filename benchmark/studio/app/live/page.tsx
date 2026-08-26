import type { Metadata } from "next";

import { TickFeed } from "@/components/live/TickFeed";
import { apiBaseUrl } from "@/lib/server/studio-client";

// The trivial Live View (DB-02): one SSE subscription rendering live ticks
// before any real data flows. The page itself stays a server component; only
// the tick island is client-side, and it talks exclusively through the typed
// client.
//
// Dynamic: the API base URL is resolved per request from the environment, so
// one build serves any local host or fixture.
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
          Live plumbing proof: ticks arrive over the host subscription endpoint through the typed
          client, with reconnect, checkpoint replay, and fail-closed frame rejection counted and
          surfaced.
        </p>
      </div>
      <TickFeed apiBaseUrl={apiBaseUrl()} />
    </div>
  );
}
