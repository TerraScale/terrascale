# ts-dashboard


- .NET 11, RazorSlices and Datastar. Use the official Stripe.net library. Store dashboard state in relational tables through the TerraBase EF Core provider (`docs/dashboard/state-storage.md`). Reach TerraBase's product APIs over MagicOnion through the shared contracts library, never a TerraBase project or binary.
- Dashboard data lives in relational tables of the `ts-dashboard` database through `DashboardDbContext` and the TerraBase EF Core provider (`docs/dashboard/state-storage.md`); tables not yet moved still use `IDashboardState`. Accounts and organizations go through `AccountStore` and `OrganizationStore` only: read the aggregate, change the record, save it with its root `version`; an erased row carries `erasure_id`. One table per migration, generated through `src/TerraScale.Dashboard.EntityFrameworkDesign`; request paths save with one `SaveChangesAsync` and no explicit transaction; ULID keys, foreign keys, unique indexes and check constraints carry the invariants.
- `src/TerraScale.Dashboard/` contains `Features/`, `Platform/`, `UI/` and `web/`. Keep each product area's components and abstractions together. TUnit journeys live in `tests/TerraScale.Dashboard.E2E/`; the SDK source lives in `sdk/dotnet/` and is consumed through ProjectReference.
- Own dashboard authentication integration, MCP, organizations, projects, users, billing, plans and Stripe management. Identity belongs to ts-auth; usage metrics and regional availability/latency come from TerraBase.
- Production uses persistent TerraBase state and ts-auth authorization. Runtime demo paths are implementation gaps, not deployment requirements. Keep account creation and customer-SDK database round trips covered end to end. The E2E suite runs the relational tables on a real TerraBase built from the same commit, one database per `DemoEnvironment`.
- The signed-in dashboard is dark-only; only the signed-out organization picker (`/organizations/choose`) ships a light theme.
- Build: `make dashboard-build`. Lint: `make dashboard-lint`. Fast: `make dashboard-test-fast` (HTTP, MCP, CLI and customer-SDK journeys). Browser and internal-billing suites: `make dashboard-test-slow`. Full validation: `make dashboard-check`.
- Asset commands: `make -f scripts/dashboard/Makefile css`, `make -f scripts/dashboard/Makefile js`, `make -f scripts/dashboard/Makefile check-css`. Bundle CSS with Lightning CSS into one shipped stylesheet; committed assets must match their sources.
- Serve logos, wordmarks and favicons from committed SVG files, including auth pages, app shell and metadata. Never inline their path/polygon data in templates, styles or components.
- Use `dotted-map` only for maps. Load map code/data only where needed. Accessible controls are the canonical input; include machine-readable map data in the SVG.
- Every feature is always on. Do not add feature flags, activation settings, deploy toggles, kill switches or tuning knobs. Keep one correct value in code. CI allowances for unavoidable third-party switch names need a one-line reason. Fakes, demo stores and skip-auth belong only in tests and test binaries.
- Discover authentication, database and orchestrator endpoints automatically. Every region serves automatically after NetBird admission, discovers all regions and infers its own region without an enable step.

- The existing official Stripe.net dependency may transitively bring Newtonsoft.Json; direct Newtonsoft package references and API usage remain banned.

- Treat TerraBase, ts-auth and stored data as untrusted input: never `ToDictionary`/`Add` keys or pass values to `ArgumentException`-throwing BCL calls without checking first (`TryAdd`, `TryParse`, range tests), and reject a bad response as the area's `JsonException` or validation failure. See `docs/dashboard/analytics.md#platform-data-and-optional-panels`.
- Optional page parts (sync, metrics, analytics) load through `OptionalPanel`, which renders the page with the part's notice and logs EventId 44 with code frames. Required reads never go through it; add no other broad `catch (Exception)`.
- Synthetic provider and subprocess fixtures live only in `tests/TerraScale.Dashboard.E2E/Demo/`; the production executable always builds the real application.
- Each area's Fast suite must finish in under 30 s wall time; each single test must finish in under 10 s. A Fast suite over 30 s or a test over 10 s is a bug. Keep default parallelism.
- Test only the happy path and critical paths in Fast; move heavy critical tests to Slow and delete everything else. New features add at most one happy-path and one critical-failure test.
- Changes limited to `AGENTS.md`/`CLAUDE.md` skip area Fast tests in CI. Mixed commits keep the gates for the changed code.
