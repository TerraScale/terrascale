// Stat tile: label + value, plain text tokens, no per-tile colors. Values and
// labels stay in ink tokens; a colored mark is never the identity of a tile.

export interface StatTileProps {
  readonly label: string;
  readonly value: string;
  readonly hint?: string;
}

export function StatTile({ label, value, hint }: StatTileProps) {
  return (
    <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
      <dt className="text-xs font-medium tracking-wide text-ink-muted uppercase">{label}</dt>
      <dd className="mt-1 font-mono text-2xl leading-tight text-ink tabular-nums">{value}</dd>
      {hint === undefined ? null : <p className="mt-1 text-xs text-ink-faint">{hint}</p>}
    </div>
  );
}
