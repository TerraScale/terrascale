// One collection's schema state panel (SM-04): the enforced Schema Version
// and rule count, the validation status in the schema model's vocabulary, and
// the Schema Migration state with the drive's recorded facts — every label
// the engine's own variant docs. Live updates are the DB-04 seam the page
// names below the table, never built here.

import { StatTile } from "@/components/ui/StatTile";
import { MIGRATION_STATE_LABELS, type SchemaStateView } from "@/components/schemas/vocabulary";
import { formatInteger } from "@/lib/format";

const VALIDATION_LABELS: Record<SchemaStateView["validation"], string> = {
  enforced: "Enforced",
  none: "Schema-free",
};

function fact(value: number | null): string {
  return value === null ? "—" : formatInteger(value);
}

function driveRows(state: SchemaStateView): readonly (readonly [string, string])[] {
  const drive = state.migration;
  if (drive === null) {
    return [];
  }
  const rows: (readonly [string, string])[] = [
    ["Manifest", drive.manifest],
    ["Versions", `${fact(drive.fromVersion)} → ${fact(drive.toVersion)}`],
  ];
  if (drive.documents !== null) {
    rows.push(["Documents scanned", formatInteger(drive.documents)]);
  }
  if (drive.transformed !== null) {
    rows.push(["Documents transformed", formatInteger(drive.transformed)]);
  }
  if (drive.pages !== null) {
    rows.push(["Pages committed", formatInteger(drive.pages)]);
  }
  return rows;
}

export interface SchemaStatePanelProps {
  readonly state: SchemaStateView;
  readonly malformed: number;
}

export function SchemaStatePanel({ state, malformed }: SchemaStatePanelProps) {
  const rows = driveRows(state);
  return (
    <section
      aria-labelledby="schema-state-heading"
      className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
      data-testid="schema-state-panel"
      data-collection={state.collection}
      data-migration-kind={state.migrationKind}
      data-validation={state.validation}
    >
      <h2 id="schema-state-heading" className="text-base font-semibold text-ink">
        <span className="font-mono">{state.collection}</span> — schema state
      </h2>
      <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <StatTile
          label="Schema version"
          value={state.enforcedVersion === null ? "none" : formatInteger(state.enforcedVersion)}
          hint={
            state.validation === "enforced"
              ? "The enforced Schema Version."
              : "No recorded Document Schema."
          }
        />
        <StatTile
          label="Rules"
          value={formatInteger(state.ruleCount)}
          hint="Recorded rule count."
        />
        <StatTile
          label="Validation"
          value={VALIDATION_LABELS[state.validation]}
          hint="The recorded policy's mode."
        />
        <StatTile
          label="Migration"
          value={state.migrationKind}
          hint={MIGRATION_STATE_LABELS[state.migrationKind]}
        />
      </div>

      {rows.length > 0 ? (
        <dl
          className="mt-3 grid gap-1 font-mono text-xs text-ink-muted"
          data-testid="schema-drive-facts"
        >
          {rows.map(([label, value]) => (
            <div className="flex gap-2" key={label}>
              <dt className="min-w-40 text-ink-faint">{label}</dt>
              <dd className="text-ink">{value}</dd>
            </div>
          ))}
        </dl>
      ) : null}

      {malformed > 0 ? (
        <p
          className="mt-3 text-xs text-status-warn"
          role="status"
          data-testid="schema-state-malformed"
        >
          {formatInteger(malformed)} drive fact{malformed === 1 ? "" : "s"} arrived malformed and
          render as absent — rejected visibly, never coerced.
        </p>
      ) : null}
    </section>
  );
}
