"use client";

// Last-resort error boundary for the documents subtree: designed typed
// failures render the server-side DocumentsTypedError route instead — this
// boundary catches only unexpected errors, keeping the typed error identity
// (code, retryability) surfaced for the operator when it is available.

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
        aria-labelledby="documents-boundary-error-title"
        className="grid w-full max-w-md gap-4 rounded-panel border border-line bg-surface p-6 shadow-panel"
        data-testid="documents-boundary-error"
      >
        <div className="grid gap-2">
          <h1 id="documents-boundary-error-title" className="text-xl font-semibold text-ink">
            Documents view failed
          </h1>
          <p className="text-sm text-ink-muted">
            An unexpected failure interrupted the documents view. The typed error identity, when one
            is available, is below; no credentials or document bodies are carried in error payloads.
          </p>
          <dl className="grid gap-1 font-mono text-xs text-ink-muted">
            <div className="flex gap-2">
              <dt>code</dt>
              <dd className="text-ink" data-testid="documents-boundary-error-code">
                {code}
              </dd>
            </div>
            <div className="flex gap-2">
              <dt>retryable</dt>
              <dd className="text-ink" data-testid="documents-boundary-error-retryable">
                {String(retryable)}
              </dd>
            </div>
          </dl>
        </div>
        <div className="flex flex-wrap gap-2">
          <EffectButton onClick={() => reset()}>Try again</EffectButton>
          <EffectButton asLink href="/documents">
            All collections
          </EffectButton>
        </div>
      </section>
    </main>
  );
}
