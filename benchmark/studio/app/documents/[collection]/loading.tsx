// Route-level loading state for the collection segment: shown while the typed
// page-window read is in flight (the documents fixture's query delay makes
// this observable in tests).

export default function Loading() {
  return (
    <main
      aria-busy="true"
      aria-live="polite"
      className="flex min-h-64 items-center justify-center px-5 py-8"
      role="status"
    >
      <p className="text-sm text-ink-muted" data-testid="document-list-loading">
        Loading documents…
      </p>
    </main>
  );
}
