// Lane and qualification limits rendered with every topology panel (DB-07):
// the labels that keep each panel honest — synthetic lane, no production
// claims, and the contract facts the vocabulary pins (health is not
// Convergence; acknowledgement is not local receipt).

export interface LimitsListProps {
  readonly limits: readonly string[];
  readonly testId?: string;
}

export function LimitsList({ limits, testId = "topology-limits" }: LimitsListProps) {
  if (limits.length === 0) {
    return null;
  }
  return (
    <ul className={`mt-2 grid gap-0.5 text-xs text-ink-faint`} data-testid={testId}>
      {limits.map((limit) => (
        <li key={limit}>{limit}</li>
      ))}
    </ul>
  );
}
