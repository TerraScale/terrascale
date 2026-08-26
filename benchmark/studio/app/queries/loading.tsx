// Route-level loading state for the queries segment: shown while the segment's
// server work is in flight. Query execution itself happens in a server action,
// whose in-flight state is the console's streaming execution indicator.

export default function Loading() {
  return (
    <main
      aria-busy="true"
      aria-live="polite"
      className="flex min-h-64 items-center justify-center px-5 py-8"
      role="status"
    >
      <p className="text-sm text-ink-muted" data-testid="queries-loading">
        Opening the query console…
      </p>
    </main>
  );
}
