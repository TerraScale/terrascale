# ts-dashboard-2

C# port of the TerraScale dashboard (ts-dashboard), 1:1 in design, pages,
and features. Dark-only design system v1 rendered from a single CSS file.

## Stack

- **C# / .NET 10** with **Razor Slices** templates (`Features/*/**.cshtml`)
- **TerraScale.Endpoints** — every route is an endpoint class, one per file,
  discovered at startup (local `ts-libs` checkout preferred, forgejo NuGet fallback)
- **terrascale.css** — the whole design system in one stylesheet:
  tokens, base, components, and the dashboard shell (`wwwroot/css/terrascale.css`)
- **Datastar** (`wwwroot/js/datastar.js`) — the front/back communication layer;
  slices declare `data-*` attributes, endpoints stream `Datastar.Stream(...)` events
- Server-rendered SVG charts (`Charts.cs`) and lucide icon set (`Icons.cs`)

## Layout

- `Features/<Feature>/<Page>Page.cs` — model record + page endpoint in one file
- `Features/<Feature>/<Page>Slice.cshtml` — the page template next to it
- `Features/<Feature>/<Action>Endpoint.cs` — one file per API/mutation endpoint
- `Framework/` — `SlicePageEndpoint`, `ShellHtml` (sidebar/shell chrome)
- `Models.cs` — `DashboardStore`: seeded in-memory demo data powering pages
- `wwwroot/css/terrascale.css` — the single-file CSS framework

## Run

```
dotnet run
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
