// Typed error state for the queries and indexes views (DB-05).
//
// The host's error identity — code, message, retryability — renders verbatim.
// The studio never rewords, maps, or collapses the engine's error vocabulary;
// a code it does not recognize is shown as-is (fail closed, never hidden).

import type { ErrorOutcome } from "@/components/queries/query-console-model";

export interface OperationErrorPanelProps {
  readonly outcome: ErrorOutcome;
  /** testid prefix so each route's error region is addressable in tests. */
  readonly testId: string;
  readonly title?: string;
}

export function OperationErrorPanel({ outcome, testId, title }: OperationErrorPanelProps) {
  return (
    <section
      aria-labelledby={`${testId}-title`}
      className="grid gap-2 rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
      data-testid={testId}
    >
      <h2 id={`${testId}-title`} className="text-sm font-semibold text-ink">
        {title ?? "Typed operation failed"}
      </h2>
      <p className="text-sm text-ink-muted" data-testid={`${testId}-message`}>
        {outcome.message}
      </p>
      <dl className="grid gap-1 font-mono text-xs text-ink-muted">
        <div className="flex gap-2">
          <dt>code</dt>
          <dd className="text-ink" data-testid={`${testId}-code`}>
            {outcome.code}
          </dd>
        </div>
        <div className="flex gap-2">
          <dt>retryable</dt>
          <dd className="text-ink" data-testid={`${testId}-retryable`}>
            {String(outcome.retryable)}
          </dd>
        </div>
      </dl>
      <p className="text-xs text-ink-faint">
        The code and message above come from the API host envelope unmodified. No credentials or
        document bodies are carried in error payloads.
      </p>
    </section>
  );
}
