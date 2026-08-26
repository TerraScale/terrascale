"use client";

// Route-level error state for the home segment: a real, recoverable route —
// Try again re-runs the segment, and the typed error identity (code,
// retryability) is surfaced for the operator. The fixture's status-fail
// behavior makes this observable in tests.

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
  const retryable = error instanceof TerrabaseApiError ? error.retryable : false;
  return (
    <main className="flex min-h-64 items-center justify-center px-5 py-8">
      <section
        aria-labelledby="studio-error-title"
        className="grid w-full max-w-md gap-4 rounded-panel border border-line bg-surface p-6 shadow-panel"
        data-testid="home-error"
      >
        <div className="grid gap-2">
          <h1 id="studio-error-title" className="text-xl font-semibold text-ink">
            Engine status unavailable
          </h1>
          <p className="text-sm text-ink-muted">
            The typed engine status read failed. The error identity below comes from the API host
            envelope; no credentials or document bodies are carried in error payloads.
          </p>
          <dl className="grid gap-1 font-mono text-xs text-ink-muted">
            <div className="flex gap-2">
              <dt>code</dt>
              <dd className="text-ink" data-testid="home-error-code">
                {code}
              </dd>
            </div>
            <div className="flex gap-2">
              <dt>retryable</dt>
              <dd className="text-ink" data-testid="home-error-retryable">
                {String(retryable)}
              </dd>
            </div>
          </dl>
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
