# Porting guide: ts-dashboard (Next.js) → TerraScale.Dashboard (C# Razor Slices)

Every page in the original `ts-dashboard` app has a 1:1 counterpart here.
This document is the contract for porting one faithfully.

## Where things live

| Concern | File |
| --- | --- |
| Page model + page endpoint | `src/TerraScale.Dashboard/Features/<Feature>/Pages/<Page>/<Page>PageModel.cs` + `<Page>PageEndpoint.cs` (one type per file) |
| Page template | `src/TerraScale.Dashboard/Features/<Feature>/Pages/<Page>/<Page>Slice.cshtml` |
| Mutations / API | `src/TerraScale.Dashboard/Features/<Feature>/Endpoints/<Action>Endpoint.cs` — **one endpoint class per file** |
| Feature store data | `src/TerraScale.Dashboard/Features/<Feature>/Stores/<Feature>Store.cs` — `DashboardStore` is `partial`; extend it there |
| Core entities + seed | `Shared/State/DashboardStore.cs` — read-mostly; prefer feature partials for additions |
| Shell chrome (sidebar, head, FABs) | `Shared/Layout/ShellHtml.cs` — pages never re-render it |
| CSS framework | `wwwroot/css/terrascale.css` — the ONLY stylesheet; append a `/* ===== Feature: <X> ===== */` section at the end if a pattern is missing |
| Icons (lucide, exact SVG) | `Shared/Components/Icons.cs` — `@Icons.KeyRound(18)` renders raw SVG |
| SVG charts | `Shared/Components/Charts.cs` — `Charts.Sparkline(values)`, `Charts.LineChart(points, label)` |

## Page skeleton

```cshtml
@inherits RazorSlice<DatabasesPageModel>
@{ var shell = Model.Shell; }
@ShellHtml.Top(shell)
<div class="ts-page">
    ...page content...
</div>
@ShellHtml.Bottom(shell)
```

```csharp
public sealed record DatabasesPageModel(...) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class DatabasesPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<DatabasesSlice, DatabasesPageModel>(store, "/databases")
{
    protected override DatabasesPageModel Load() => new() { Shell = ShellFactory.Create(store, PageRoute), ... };
}
```

Routes are discovered automatically — **never touch `src/TerraScale.Dashboard/Program.cs`**.

## Rules

1. **Razor Slices has no `Html.Raw` and no tag helpers.** Raw HTML comes from
   `HtmlString`-returning helpers: `@Icons.X(n)`, `@Charts.X(...)`,
   `@ShellHtml.Top/Bottom`. A page helper of your own returns
   `Microsoft.AspNetCore.Html.HtmlString` the same way.
2. **Every endpoint is one class in one file**, deriving from
   `EndpointWithoutRequest<IResult>` / `Endpoint<TRequest>` from TerraScale.Endpoints.
   Mutations read forms/signals via `HttpContext.Request` or `ReadSignalsAsync<T>`,
   mutate the store, and `Results.Redirect(..., false, false)` back to the page.
   Auth is handled by `DashboardAuthMiddleware` (cookie `ts_session`).
3. **Copy is copied verbatim** from the original page/feature (`*-copy.ts`,
   inline JSX text) — including tone, casing, and empty states. English only.
4. **Design tokens only** — use `terrascale.css` classes; never inline styles
   except `--ts-nav-accent` style overrides the shell already handles.
   Badges/alerts always pair color with text (design system rule).
5. **Mono for data**: IDs, ULIDs, connection strings, queries, numbers in
   tables render with `ts-mono` / `ts-table-mono` (JetBrains Mono, tabular-nums).
6. **Datastar is the interaction layer**: `data-signals`, `data-on-click`,
   `data-show`, `data-class` for tabs/dialogs/filters; `data-on-load` +
   endpoint `IDatastarService` only when a fragment must update live.
   Plain `details.ts-menu` for popovers, native `dialog.ts-dialog` for modals.
7. **Interactive JS is banned** beyond `wwwroot/js/terrascale.js` (copy button,
   menu click-away). Everything else is Datastar attributes or CSS.
8. **Dark-only**: never add a light theme, never hardcode a color a token
   expresses. Accent blue is the only non-neutral interactive color.
9. Demo data flows from `DashboardStore` (in-memory, seeded). Seed realistic
   ULID-shaped ids (`db_01J...`), ISO dates, plausible numbers matching the
   original fixtures where they exist.
10. **Verify before committing**: `dotnet build` must succeed with zero
    warnings (warnings are errors), and the page must render: log in
    (`mario@terrascale.tech` / `terrascale`) and fetch the page with the
    session cookie.

## Original sources per feature

`/home/mariogk/Projects/TerraScale/ts-dashboard/`:
`app/<route>/page.tsx` is the markup; `src/features/<feature>/` holds its data
loaders, copy, and client components; `src/components/ui/` holds primitives
(Button → `ts-btn-*`, Badge → `ts-badge-*`, Panel → `ts-panel`, DataTable →
`ts-table-wrap`/`ts-table`, Dialog → `dialog.ts-dialog`, Tabs → `ts-tabs`).
