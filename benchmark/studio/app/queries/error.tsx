"use client";

// Route-level error boundary for the queries segment: the last-resort boundary
// for unexpected failures. Typed operation failures are designed routes
// rendered from the typed outcome (OperationErrorPanel); only what is not a
// typed API error reaches this boundary.

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
        aria-labelledby="queries-error-title"
        className="grid w-full max-w-md gap-4 rounded-panel border border-line bg-surface p-6 shadow-panel"
        data-testid="queries-error"
      >
        <div className="grid gap-2">
          <h1 id="queries-error-title" className="text-xl font-semibold text-ink">
            Queries unavailable
          </h1>
          <p className="text-sm text-ink-muted">
            The queries route failed unexpectedly. Typed operation failures render inline with the
            host&apos;s error identity; reaching this boundary means the failure was not a typed API
            error.
          </p>
          <dl className="grid gap-1 font-mono text-xs text-ink-muted">
            <div className="flex gap-2">
              <dt>code</dt>
              <dd className="text-ink" data-testid="queries-error-code">
                {code}
              </dd>
            </div>
            <div className="flex gap-2">
              <dt>retryable</dt>
              <dd className="text-ink" data-testid="queries-error-retryable">
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
