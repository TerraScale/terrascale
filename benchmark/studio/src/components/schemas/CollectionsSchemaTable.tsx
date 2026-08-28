// The collections × schema-state table (SM-04): one row per collection of
// the catalog the page read, carrying its enforced version, validation
// status, and migration state. A row whose typed read failed or whose answer
// was not the contract's shape renders that fact honestly — never a guess,
// never a silent omission.

import {
  MIGRATION_STATE_LABELS,
  SCHEMA_COLLECTIONS_BOUND,
  schemasHref,
  type SchemaStateView,
} from "@/components/schemas/vocabulary";
import { formatInteger } from "@/lib/format";

/** One catalog collection with its parsed schema state, or its typed failure. */
export interface CollectionSchemaRow {
  readonly name: string;
  readonly documents: number;
  readonly sizeBytes: number;
  readonly state: SchemaStateView | null;
  readonly failureCode: string | null;
  readonly malformed: number;
}

const VALIDATION_CELL: Record<SchemaStateView["validation"], string> = {
  enforced: "Enforced",
  none: "Schema-free",
};

export interface CollectionsSchemaTableProps {
  readonly rows: readonly CollectionSchemaRow[];
  readonly selected: string | null;
  readonly truncated: boolean;
}

export function CollectionsSchemaTable({ rows, selected, truncated }: CollectionsSchemaTableProps) {
  return (
    <section
      aria-labelledby="schema-collections-heading"
      className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
      data-testid="schema-collections-table"
    >
      <h2 id="schema-collections-heading" className="text-base font-semibold text-ink">
        Collections
      </h2>
      <p className="mt-1 text-xs text-ink-faint">
        One typed <span className="font-mono">schema.status</span> read per collection, bounded to{" "}
        {formatInteger(SCHEMA_COLLECTIONS_BOUND)} collections per page load.
        {truncated ? " The catalog exceeded that bound; only the first rows were read." : ""}
      </p>
      <div className="mt-3 overflow-x-auto">
        <table className="w-full border-collapse text-sm">
          <thead>
            <tr className="text-left text-xs tracking-wide text-ink-muted uppercase">
              <th scope="col" className="border-b border-line px-2 py-1.5">
                Collection
              </th>
              <th scope="col" className="border-b border-line px-2 py-1.5">
                Schema version
              </th>
              <th scope="col" className="border-b border-line px-2 py-1.5">
                Rules
              </th>
              <th scope="col" className="border-b border-line px-2 py-1.5">
                Validation
              </th>
              <th scope="col" className="border-b border-line px-2 py-1.5">
                Migration
              </th>
              <th scope="col" className="border-b border-line px-2 py-1.5">
                Documents
              </th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr
                key={row.name}
                className="data-row"
                data-testid="schema-collection-row"
                data-collection={row.name}
                data-selected={row.name === selected ? "true" : "false"}
                data-migration-kind={row.state?.migrationKind ?? "unknown"}
              >
                <th scope="row" className="border-b border-line px-2 py-1.5 text-left font-mono">
                  <a
                    href={schemasHref(row.name)}
                    className={
                      row.name === selected
                        ? "text-accent underline decoration-accent"
                        : "text-ink underline decoration-line hover:text-accent"
                    }
                  >
                    {row.name}
                  </a>
                </th>
                {row.state === null ? (
                  <td
                    className="border-b border-line px-2 py-1.5 text-status-warn"
                    colSpan={4}
                    data-testid="schema-collection-unavailable"
                  >
                    {row.failureCode === null
                      ? "State not recognized — the host's answer was not the contract's shape."
                      : `State unavailable (${row.failureCode}).`}
                  </td>
                ) : (
                  <>
                    <td className="border-b border-line px-2 py-1.5 font-mono tabular-nums">
                      {row.state.enforcedVersion === null
                        ? "—"
                        : formatInteger(row.state.enforcedVersion)}
                    </td>
                    <td className="border-b border-line px-2 py-1.5 font-mono tabular-nums">
                      {formatInteger(row.state.ruleCount)}
                    </td>
                    <td className="border-b border-line px-2 py-1.5">
                      {VALIDATION_CELL[row.state.validation]}
                    </td>
                    <td
                      className="border-b border-line px-2 py-1.5 font-mono"
                      title={MIGRATION_STATE_LABELS[row.state.migrationKind]}
                    >
                      {row.state.migrationKind}
                    </td>
                  </>
                )}
                <td className="border-b border-line px-2 py-1.5 font-mono tabular-nums">
                  {formatInteger(row.documents)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
