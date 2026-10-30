# ts-dashboard


- .NET 11, RazorSlices and Datastar. Use the official Stripe.net library. Reach TerraBase only over MagicOnion through `src/TerraScale.TerraBase.Contracts` (Platform/Rpc), never a TerraBase project or binary; ts-auth through the dashboard-owned client in Platform/TsAuth.
- `src/TerraScale.Dashboard/` contains `Features/`, `Platform/`, `UI/` and `web/`. Keep each product area's components and abstractions together. TUnit journeys live in `tests/TerraScale.Dashboard.E2E/`; the SDK source lives in `sdk/dotnet/` and is consumed through ProjectReference.
- Own dashboard authentication integration, MCP, organizations, projects, users, billing, plans and Stripe management. Identity belongs to ts-auth; usage metrics and regional availability/latency come from TerraBase.
- Production uses persistent TerraBase state through ts-dotnet and ts-auth authorization. Runtime demo paths are implementation gaps, not deployment requirements. Keep account creation and customer-SDK database round trips covered end to end.
- Build: `make dashboard-build`. Lint: `make dashboard-lint`. Fast: `make dashboard-test-fast` (HTTP, MCP, CLI and customer-SDK journeys). Browser and internal-billing suites: `make dashboard-test-slow`. Full validation: `make dashboard-check`.
- Asset commands: `make -f scripts/dashboard/Makefile css`, `make -f scripts/dashboard/Makefile js`, `make -f scripts/dashboard/Makefile check-css`. Bundle CSS with Lightning CSS into one shipped stylesheet; committed assets must match their sources.
- Serve logos, wordmarks and favicons from committed SVG files, including auth pages, app shell and metadata. Never inline their path/polygon data in templates, styles or components.
- Use `dotted-map` only for maps. Load map code/data only where needed. Accessible controls are the canonical input; include machine-readable map data in the SVG.
- Every feature is always on. Do not add feature flags, activation settings, deploy toggles, kill switches or tuning knobs. Keep one correct value in code. CI allowances for unavoidable third-party switch names need a one-line reason. Fakes, demo stores and skip-auth belong only in tests and test binaries.
- Discover authentication, database and orchestrator endpoints automatically. Every region serves automatically after NetBird admission, discovers all regions and infers its own region without an enable step.

- The existing official Stripe.net dependency may transitively bring Newtonsoft.Json; direct Newtonsoft package references and API usage remain banned.

- Synthetic provider and subprocess fixtures live only in `tests/TerraScale.Dashboard.E2E/Demo/`; the production executable always builds the real application.
