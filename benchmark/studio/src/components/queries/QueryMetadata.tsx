// Query-level metadata for the console (DB-05): matched and returned counts,
// the execution window, the host's staleness observations verbatim, and an
// explicit statement of what the wire contract does not carry yet.
//
// The studio API contract's QueryResult carries rows, matched, and staleness
// only. The engine's plan and ordering (QueryPlan, QueryResult::ordering,
// index_generation in src/query/) are not on the wire, so this panel says so
// instead of inventing a plan.

import type { ResultsOutcome } from "@/components/queries/query-console-model";
import { formatInteger, formatUtcTimestamp } from "@/lib/format";

export interface QueryMetadataProps {
  readonly outcome: ResultsOutcome;
}

export function QueryMetadata({ outcome }: QueryMetadataProps) {
  return (
    <section
      aria-labelledby="query-metadata-heading"
      className="grid gap-3 rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
      data-testid="query-metadata"
    >
      <h2 id="query-metadata-heading" className="text-base font-semibold text-ink">
        Query metadata
      </h2>
      <dl
        className="grid grid-cols-2 gap-x-6 gap-y-1 text-sm sm:grid-cols-3"
        data-testid="query-metadata-facts"
      >
        <Fact
          label="Matched"
          value={formatInteger(outcome.matched)}
          testId="query-metadata-matched"
        />
        <Fact
          label="Returned"
          value={formatInteger(outcome.returnedRows)}
          testId="query-metadata-returned"
        />
        <Fact
          label="Executed at"
          value={formatUtcTimestamp(outcome.executedAt)}
          testId="query-metadata-executed-at"
        />
      </dl>
      <p className="text-xs text-ink-faint" data-testid="query-metadata-elapsed-note">
        Elapsed is this one execution&apos;s wall-clock window ({formatInteger(outcome.elapsedMs)}{" "}
        ms), shown for the operator only. It is not a benchmark and not a performance claim.
      </p>

      <div className="grid gap-2">
        <h3 className="text-sm font-semibold text-ink">Staleness observations</h3>
        {outcome.staleness.length === 0 ? (
          <p className="text-sm text-ink-muted" data-testid="query-staleness-empty">
            The host reported no staleness observations for this query.
          </p>
        ) : (
          <ul className="grid gap-1" data-testid="query-staleness">
            {outcome.staleness.map((observation) => (
              <li
                key={observation.indexId}
                className="grid gap-0.5 rounded bg-surface-raised px-2 py-1 font-mono text-xs text-ink"
                data-index-id={observation.indexId}
              >
                <span className="text-ink">{observation.indexId}</span>
                <span className="text-ink-muted" data-testid="query-staleness-reason">
                  {observation.reason}
                </span>
              </li>
            ))}
          </ul>
        )}
      </div>

      <p className="text-xs text-ink-faint" data-testid="query-plan-note">
        Plan and ordering metadata: the API contract&apos;s QueryResult carries rows, matched, and
        staleness only. The engine&apos;s plan, applied ordering, and index generation are not on
        the wire yet, so this console cannot show them — recorded as a contract gap for the contract
        owner, never filled with an invented plan.
      </p>
    </section>
  );
}

function Fact({ label, value, testId }: { label: string; value: string; testId: string }) {
  return (
    <div className="flex gap-2" data-fact={label}>
      <dt className="text-ink-muted">{label}</dt>
      <dd className="font-mono tabular-nums text-ink" data-testid={testId}>
        {value}
      </dd>
    </div>
  );
}
