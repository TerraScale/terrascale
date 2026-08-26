// Typed error state for the home route: rendered server-side from the typed
// API error (code, retryability), so the operator sees the host's error
// identity — production error boundaries collapse server errors to a digest,
// which would lose it. app/error.tsx remains the last-resort boundary for
// unexpected failures; this is the designed, tested error route.

import { EffectButton } from "@/components/ui/EffectButton";

export interface EngineStatusErrorProps {
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
}

export function EngineStatusError({ code, message, retryable }: EngineStatusErrorProps) {
  return (
    <main className="flex min-h-64 items-center justify-center px-5 py-8">
      <section
        aria-labelledby="engine-status-error-title"
        className="grid w-full max-w-md gap-4 rounded-panel border border-line bg-surface p-6 shadow-panel"
        data-testid="home-error"
      >
        <div className="grid gap-2">
          <h1 id="engine-status-error-title" className="text-xl font-semibold text-ink">
            Engine status unavailable
          </h1>
          <p className="text-sm text-ink-muted">{message}</p>
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
          <EffectButton asLink href="/">
            Try again
          </EffectButton>
        </div>
      </section>
    </main>
  );
}
