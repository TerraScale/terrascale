// Studio navigation: implemented routes link, later-wave routes are marked
// planned (muted, non-interactive) so the shell establishes the page
// vocabulary every DB ticket extends without shipping dead links.

const IMPLEMENTED = [
  { href: "/", label: "Home" },
  { href: "/live", label: "Live view" },
  { href: "/documents", label: "Documents" },
  { href: "/queries", label: "Queries" },
  { href: "/queries/indexes", label: "Indexes" },
  { href: "/families", label: "Families" },
  { href: "/schemas", label: "Schemas" },
  { href: "/topology", label: "Topology" },
] as const;

const PLANNED = [] as const;

export function StudioNav() {
  return (
    <nav aria-label="Studio" aria-describedby="studio-nav-planned-note">
      <ul className="flex flex-wrap items-center gap-1">
        {IMPLEMENTED.map((item) => (
          <li key={item.href}>
            <a
              href={item.href}
              className="rounded-panel px-3 py-1.5 text-sm text-ink-muted transition-colors hover:bg-surface-raised hover:text-ink"
            >
              {item.label}
            </a>
          </li>
        ))}
        {PLANNED.map((label) => (
          <li key={label}>
            <span
              aria-disabled="true"
              title={`${label} lands with a later studio ticket`}
              className="rounded-panel cursor-default px-3 py-1.5 text-sm text-ink-faint"
            >
              {label}
              <span className="sr-only"> (planned)</span>
            </span>
          </li>
        ))}
      </ul>
      <p id="studio-nav-planned-note" className="sr-only">
        Planned sections open with later studio tickets.
      </p>
    </nav>
  );
}
