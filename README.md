# ts-dashboard-2

C# port of the TerraScale dashboard (ts-dashboard), 1:1 in design, pages,
and features. Dark-only design system v1 rendered from a single CSS file.

See `AGENTS.md` for the full conventions (feature slices, one type per file,
one endpoint per file, CI/deploy).

## Stack

- **C# / .NET 10** with **Razor Slices** templates (`src/ts-dashboard-2/Features/*/**.cshtml`)
- **TerraScale.Endpoints** — every route is an endpoint class, one per file,
  discovered at startup (local `ts-libs` checkout preferred, vendored
  `src/ts-dashboard-2/nuget/` package fallback)
- **terrascale.css** — the whole design system in one stylesheet:
  tokens, base, components, and the dashboard shell (`src/ts-dashboard-2/wwwroot/css/terrascale.css`)
- **Datastar** (`wwwroot/js/datastar.js`) — the front/back communication layer;
  slices declare `data-*` attributes, endpoints stream `Datastar.Stream(...)` events
- Server-rendered SVG charts (`Framework/Charts.cs`) and lucide icon set (`Framework/Icons.cs`)

## Layout

- `src/ts-dashboard-2/Features/<Feature>/` — the feature slice: one endpoint
  per file (`<Page>PageEndpoint.cs`, `<Action>Endpoint.cs`), one DTO per file
  (`<Page>PageModel.cs`, row records), the `<Page>Slice.cshtml` view, and the
  feature's `DashboardStore` partial
- `src/ts-dashboard-2/Framework/` — `SlicePageEndpoint`, `ShellHtml`, shell,
  store core, icons, charts
- `src/ts-dashboard-2/wwwroot/css/terrascale.css` — the single-file CSS framework
- `ts-dashboard-2.slnx` — solution at the repo root

## Run

```
dotnet run --project src/ts-dashboard-2
# open http://localhost:5000 → redirected to /login
# demo credentials: mario@terrascale.tech / terrascale
```

## Page pattern

```csharp
public sealed class DatabasesPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<DatabasesSlice, DatabasesPageModel>(store, "/databases")
{
    protected override DatabasesPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
```

The slice opens with `@ShellHtml.Top(Model.Shell)` and closes with
`@ShellHtml.Bottom(Model.Shell)`; everything between is the page.
