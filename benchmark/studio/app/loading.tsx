// Route-level loading state for the home segment: shown while the typed
// engine.status read is in flight (the fixture's status-slow behavior makes
// this observable in tests).

export default function Loading() {
  return (
    <main
      aria-busy="true"
      aria-live="polite"
      className="flex min-h-64 items-center justify-center px-5 py-8"
      role="status"
    >
      <p className="text-sm text-ink-muted" data-testid="home-loading">
        Loading engine status…
      </p>
    </main>
  );
}
