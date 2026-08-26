// Route-level loading state for the per-document family panels: shown while
// the route's typed reads (document.read plus the family.read windows) are in
// flight (the families fixture's delay control makes this observable in
// tests).

export default function Loading() {
  return (
    <main
      aria-busy="true"
      aria-live="polite"
      className="flex min-h-64 items-center justify-center px-5 py-8"
      role="status"
    >
      <p className="text-sm text-ink-muted" data-testid="families-document-loading">
        Loading family panels…
      </p>
    </main>
  );
}
