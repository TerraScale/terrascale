"use client";

// Route-level error boundary for the topology segment: the last-resort route
// for unexpected failures (typed failures render the page's own designed
// error route instead). Try again re-runs the segment; the typed identity, if
// any, is surfaced for the operator.

import { EffectButton } from "@/components/ui/EffectButton";
import { TerrabaseApiError } from "@/api/generated/client";

export default function Error({
  error,
  reset,
}: {
  readonly error: Error & { digest?: string };
  readonly reset: () => void;
}) {
  const code = error instanceof TerrabaseApiError ? error.code : "error.unknown";
  return (
    <main className="flex min-h-64 items-center justify-center px-5 py-8">
      <section
        aria-labelledby="topology-error-boundary-title"
        className="grid w-full max-w-md gap-4 rounded-panel border border-line bg-surface p-6 shadow-panel"
        data-testid="topology-error-boundary"
      >
        <div className="grid gap-2">
          <h1 id="topology-error-boundary-title" className="text-xl font-semibold text-ink">
            Topology view failed
          </h1>
          <p className="text-sm text-ink-muted">
            An unexpected failure interrupted the topology reads. The identity below is the
            envelope&rsquo;s error code when one exists; no credentials or document bodies are
            carried in error payloads.
          </p>
          <p
            className="font-mono text-xs text-ink-muted"
            data-testid="topology-error-boundary-code"
          >
            {code}
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <EffectButton onClick={() => reset()}>Try again</EffectButton>
          <EffectButton asLink href="/">
            Return to studio home
          </EffectButton>
        </div>
      </section>
    </main>
  );
}
