// Route-level loading state for the schemas segment: shown while the page's
// typed catalog and schema.status reads are in flight.

export default function Loading() {
  return (
    <main
      aria-busy="true"
      aria-live="polite"
      className="flex min-h-64 items-center justify-center px-5 py-8"
      role="status"
    >
      <p className="text-sm text-ink-muted" data-testid="schemas-loading">
        Loading schema states…
      </p>
    </main>
  );
}
