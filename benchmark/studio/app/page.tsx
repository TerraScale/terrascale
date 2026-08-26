import { redirect } from "next/navigation";

import { EngineStatusPanel } from "@/components/home/EngineStatusPanel";
import { EngineStatusError } from "@/components/home/EngineStatusError";
import { TerrabaseApiError } from "@/api/generated/client";
import { bootstrapOperatorSession } from "@/lib/server/session";
import { createStudioClient } from "@/lib/server/studio-client";

// Studio home: the operator session bootstrap (one typed read) and the engine
// status rendered through that same typed read — never raw fetch, never engine
// internals. Loading and error states are real routes (loading.tsx streams
// during in-flight reads; typed failures render the EngineStatusError route,
// with error.tsx as the last-resort boundary); an unauthenticated session
// redirects to the host's login flow exactly as the host directs it.
//
// The route is dynamic: the status read and the session bootstrap must run per
// request against the API host (the dev fixture or SE-08), never prerendered
// into a stale build artifact.
export const dynamic = "force-dynamic";

export default async function HomePage() {
  const client = createStudioClient();
  try {
    const session = await bootstrapOperatorSession(client);
    if (session.kind === "redirect") {
      redirect(session.loginUrl);
    }
    return <EngineStatusPanel status={session.status} />;
  } catch (error) {
    if (error instanceof TerrabaseApiError) {
      // The typed failure is a designed route; only unexpected errors reach
      // the segment's error boundary.
      return (
        <EngineStatusError code={error.code} message={error.message} retryable={error.retryable} />
      );
    }
    throw error;
  }
}
