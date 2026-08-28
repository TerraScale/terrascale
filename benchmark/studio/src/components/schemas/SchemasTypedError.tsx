// Typed error state for the schemas routes (SM-04): rendered server-side
// from the typed API error (code, retryability) so the operator sees the
// host's error identity — collapsing to a digest at the boundary would lose
// it. app/schemas/error.tsx remains the last-resort boundary for unexpected
// failures; this is the designed, tested error route, mirroring the DB-02/03/06
// convention.

import { EffectButton } from "@/components/ui/EffectButton";

export interface SchemasTypedErrorProps {
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
  /** Route to re-navigate to for the Try again action (re-runs the typed reads). */
  readonly retryHref: string;
}

export function SchemasTypedError({ code, message, retryable, retryHref }: SchemasTypedErrorProps) {
  return (
    <main className="flex min-h-64 items-center justify-center px-5 py-8">
      <section
        aria-labelledby="schemas-error-title"
        className="grid w-full max-w-md gap-4 rounded-panel border border-line bg-surface p-6 shadow-panel"
        data-testid="schemas-error"
      >
        <div className="grid gap-2">
          <h1 id="schemas-error-title" className="text-xl font-semibold text-ink">
            Schema states unavailable
          </h1>
          <p className="text-sm text-ink-muted">{message}</p>
          <dl className="grid gap-1 font-mono text-xs text-ink-muted">
            <div className="flex gap-2">
              <dt>code</dt>
              <dd className="text-ink" data-testid="schemas-error-code">
                {code}
              </dd>
            </div>
            <div className="flex gap-2">
              <dt>retryable</dt>
              <dd className="text-ink" data-testid="schemas-error-retryable">
                {String(retryable)}
              </dd>
            </div>
          </dl>
        </div>
        <div className="flex flex-wrap gap-2">
          <EffectButton asLink href={retryHref}>
            Try again
          </EffectButton>
        </div>
      </section>
    </main>
  );
}
