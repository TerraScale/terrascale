// Route-level loading state for the topology segment: shown while the typed
// topology reads are in flight (the fixture's status-slow behavior makes this
// observable in tests).

export default function Loading() {
  return (
    <main
      aria-busy="true"
      aria-live="polite"
      className="flex min-h-64 items-center justify-center px-5 py-8"
      role="status"
    >
      <p className="text-sm text-ink-muted" data-testid="topology-loading">
        Loading topology…
      </p>
    </main>
  );
}
