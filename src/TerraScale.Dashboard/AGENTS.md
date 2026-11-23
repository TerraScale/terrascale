# ts-dashboard

Read the root [AGENTS.md](../../AGENTS.md). Load the `csharp` skill before writing C#.

.NET 11, RazorSlices and Datastar, using the official `Stripe.net` library. Owns dashboard authentication integration, MCP, organizations, projects, users, billing, plans and Stripe management. Identity belongs to `ts-auth`; usage metrics and regional availability/latency come from TerraBase.

## Layout and checks

- `src/TerraScale.Dashboard/` contains `Features/`, `Platform/`, `UI/` and `web/`. Keep each product area's components and abstractions together. The SDK source lives in `sdk/dotnet/` (`ProjectReference`).
- Build: `make dashboard-build`. Lint: `make dashboard-lint`. Full validation: `make dashboard-check`.
- Asset commands: `make dashboard-css`, `make dashboard-js`, `make dashboard-check-css`. Bundle CSS with Lightning CSS into one shipped stylesheet; committed assets must match their sources.

## Storage and concurrency

- Store dashboard state in relational tables of `ts-dashboard` through `DashboardDbContext` and the TerraBase EF Core provider; lost concurrency races surface as `DbUpdateConcurrencyException`. Reach TerraBase's product APIs over MagicOnion through the shared contracts library, never a TerraBase project or binary.
- Accounts and organizations go through `AccountStore` and `OrganizationStore` only: read the aggregate, change the record, save it with its root `version`; an erased row carries `erasure_id`. One table per migration, generated through `src/TerraScale.Dashboard.EntityFrameworkDesign`; request paths save with one `SaveChangesAsync` and no explicit transaction; ULID keys, foreign keys, unique indexes and check constraints carry the invariants.
- Read-modify-write on stored state must be idempotent and conflict-retrying: reread the row, apply the change to what is stored now, skip the save when nothing differs, and on `DbUpdateConcurrencyException` reread and apply again. Creates are insert-or-get (a unique-key loss rereads the winner's row). Concurrent mutations (sign-in audit events, first-sign-in provisioning) must commute or be a no-op when state already matches — never a counted retry loop that two tabs, a double-click or a callback retry can exhaust. Unresolved conflicts return a retryable `409`, never `500`; never swallow them.
- Treat TerraBase, `ts-auth` and stored data as untrusted input: never `ToDictionary`/`Add` keys or pass values to `ArgumentException`-throwing BCL calls without checking first (`TryAdd`, `TryParse`, range tests); reject bad responses as the area's `JsonException` or validation failure.
- Optional page parts (sync, metrics, analytics) load through `OptionalPanel`, which renders the page with the part's notice and logs EventId 44 with code frames. Required reads never go through it; add no other broad `catch (Exception)`.
- Official `Stripe.net` may transitively bring `Newtonsoft.Json`; direct Newtonsoft package references and API usage remain banned.

## UI and regions

- The signed-in dashboard is dark-only; only the signed-out organization picker (`/organizations/choose`) ships a light theme.
- Serve logos, wordmarks and favicons from committed SVG files (including auth pages, app shell and metadata). Never inline their path/polygon data in templates, styles or components.
- Use `dotted-map` only for maps, loaded only where needed. Accessible controls are the canonical input; include machine-readable map data in the SVG.
- Every map is the one shared region map component (`Common/UI/Slices/RegionMap.cshtml`, `Common/UI/RegionMapModel.cs`, `Common/UI/region-map.css`, `web/assets/js/region-map.js`, dot data from `web/build/generate-region-map.mjs`). It supports `select` and `read-only` modes and renders every map across the app. Change its look or motion in those files only; never build a second map or per-screen map styles/scripts.
- Regions are equal: the dashboard never shows or names a home, primary or replica region. A database has one set of regions with at least one selected, and `Features/Databases/DatabasePlacementEndpoints.cs` derives what TerraBase's placement operations need from that set.
- Discover authentication, database and orchestrator endpoints automatically.

## Dropdowns

- Every dropdown uses browser-managed HTML controls styled with shared dashboard design tokens (colors, font, border, radius, dimensions, focus/disabled/invalid states). Preserve labels, keyboard access, visible focus, OS accessibility and forced-colors support.
- Value choices use real `<select>`/`<option>` elements and shared `ts-select` styles. Never hide a `<select>` behind a proxy control or simulate selects/listboxes in JavaScript. Customizable select styling (`base-select` and `::picker(select)`) is progressive CSS enhancement inside `@supports`; unsupported browsers keep ordinary native selects without a JS polyfill.
- Action and navigation dropdowns use semantic links/buttons in `popover="auto"` containers opened by native `popovertarget` invokers, positioned with CSS anchors and browser collision handling. Never turn actions into select options or hand-manage dropdown positioning, opening, dismissal or keyboard handling in JS/Datastar. Use ordinary Tab navigation; do not claim ARIA menu semantics requiring a custom keyboard model.
- Keep non-dropdown `<details>`/`<summary>` disclosures as disclosures.
