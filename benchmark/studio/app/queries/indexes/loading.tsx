// Route-level loading state for the index view segment: served during
// navigation while the catalog read streams (the Suspense fallback inside the
// page carries the same pending state once the segment is open).

export default function Loading() {
  return (
    <main
      aria-busy="true"
      aria-live="polite"
      className="flex min-h-64 items-center justify-center px-5 py-8"
      role="status"
    >
      <p className="text-sm text-ink-muted" data-testid="indexes-loading">
        Opening the index catalog…
      </p>
    </main>
  );
}
