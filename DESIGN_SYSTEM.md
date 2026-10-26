# TerraScale Dashboard Design System

Pages are server-rendered [RazorSlices](https://github.com/DamianEdwards/RazorSlices)
`.cshtml` slices enhanced with Datastar. Paths below are relative to
`src/TerraScale.Dashboard` unless they start with `scripts/`. Readable CSS
lives in `web/assets/css/` (shared) and `Features/<Feature>/`
(page-specific); `make css` (`npm run build:css` in `web/`, the asset build's
npm project) bundles it with Lightning CSS into the single stylesheet
`web/static/css/terrascale.css`.

## Principles

1. **Compose the kit.** Build pages from the shared slices in `UI/Slices/`
   and the shared `ts-*` classes below before writing new markup or CSS.
   There are no page-header, tab, card or stat helper components in C#;
   feature slices write that markup directly, as in the example below.
2. **Flat and blue.** Content sits directly on the canvas; sections are split
   by hairlines, never boxed, and nothing glows (no gradients, blur or soft
   shadows; focus is a solid outline). Blue (`--ts-accent`) is the one accent
   for actions, tabs, charts and highlights. Only the navigation icons carry
   their own tones (the `Item` list in `UI/Slices/ShellSidebar.cshtml`) so
   sections are easy to find.
3. **Spacing by gap.** Containers space their children with `gap`, never with
   margins. Use the 4px scale `--ts-s1`…`--ts-s12`.
4. **Tokens only.** Colors, type, radius and motion come from
   `web/assets/css/tokens.css`. No inline `style` attributes (CSP
   `style-src 'self'`); use `data-*` attributes for finite variants. The
   tokens meet WCAG 2.2 AA: every ink keeps 4.5:1 on the canvas and all
   surfaces, white text sits on `--ts-accent-fill` (never `--ts-accent`), and
   form controls use the 3:1 `--ts-control-line` border.
5. **No custom JavaScript.** Tabs, filters and pagination are GET links and
   forms. Interactivity comes from native HTML (`<details>` menus, `<dialog>`
   with `command`/`commandfor`) and Datastar attributes; write JavaScript only
   as a last resort.

## Page anatomy

Abridged from `Features/Databases/DatabasesView.cshtml`:

```cshtml
@inherits RazorSlice<TerraScale.Dashboard.Features.Databases.DatabasesPage>
@implements IUsesLayout<TerraScale.Dashboard.UI.Slices.ShellDocument, ShellModel>
<main class="ts-page">
<h1 class="ts-sr-only">Databases</h1><div class="ts-page-actions"></div>
<dl class="ts-stats" aria-label="Database totals">
  <div class="ts-stat" data-tone="blue"><dt class="ts-stat-label"><span class="ts-dot" aria-hidden="true"></span>Databases</dt><dd class="ts-stat-value">@Model.All.Count</dd><dd class="ts-stat-detail">In this project</dd></div>
</dl>
<section class="ts-card ts-card-flush" id="project-databases" aria-labelledby="project-databases-title">
  <header class="ts-card-head"><div class="ts-card-heading"><h2 class="ts-card-title" id="project-databases-title">Project databases</h2><p class="ts-card-description">@Model.Description</p></div></header>
  <div class="ts-card-body"><div class="ts-table-wrap"><table class="ts-table">…</table></div></div>
</section>
</main>
@functions { public ShellModel LayoutModel => Model.Shell; }
```

| Pattern | C# location and use |
|---|---|
| Page heading | Markup only: signed-in pages render `<h1 class="ts-sr-only">` and `.ts-page-actions`; `UI/Slices/ShellDocument.cshtml` sets the `<title>` |
| Tabs | Markup only: `nav.ts-tabs` with `a.ts-tab` and `aria-current` (e.g. `Features/Databases/DatabaseFrame.cshtml`) |
| Card | Markup only: `section.ts-card` with `ts-card-head` and `ts-card-body`; `ts-card-flush` for tables |
| Stats | Markup only: `dl.ts-stats` with `div.ts-stat` and `data-tone` (`ts-stats-compact` for word values) |
| Disclosure | No C# equivalent; create and destructive forms use `dialog.ts-dialog` |
| Empty state | Markup only: `div.ts-empty` with `ts-empty-title` and `ts-empty-detail`; a call to action may follow |
| Alert | Markup only: `ts-alert ts-alert-info/-success/-warning/-danger` with `ts-alert-title` and `ts-alert-body` |
| Badge | Markup only: `ts-badge-neutral/-info/-success/-warning/-danger/-violet`, or `ts-badge-tone` with `data-tone` |
| Field | Markup only: `div.ts-field` with `ts-label`, the control and a hint or error |
| Region map | `UI/Slices/RegionMap.cshtml` (decorative) and `UI/Slices/RegionPicker.cshtml` with `UI/RegionMapField.cs` (highlights hubs chosen in a select or checkbox group) |
| Icons | `UI/Icons.cs` (`Icons.Render`) and `UI/Slices/ShellIcons.cs` |

## Classes

| Area | Classes |
|---|---|
| Layout | `ts-page` `ts-split` `ts-grid` `ts-grid-2` `ts-grid-3` `ts-stack(-sm/-lg)` `ts-row` `ts-row-between` `ts-toolbar` |
| Text | `ts-meta` `ts-muted` `ts-strong` `ts-mono` `ts-num` `ts-link` `ts-truncate` `ts-break` `ts-sr-only` |
| Controls | `ts-btn` + `-primary/-secondary/-ghost/-danger`, `-sm/-lg/-block`; `ts-icon-btn`; `ts-input` `ts-select` `ts-label` `ts-hint` `ts-check` `ts-input-group` |
| Forms | `ts-form` `ts-form-grid` `ts-form-actions` `ts-filters` `ts-filter-field` |
| Data | `ts-table-wrap` `ts-table` `ts-list` `ts-list-item` `ts-kv` `ts-pager` `ts-progress` `ts-code` `ts-snippet` `ts-dot` `ts-avatar` |
| Overlays | `ts-menu` (`<details class="ts-menu" data-on:click__outside="el.open = false">` + `ts-menu-panel`, `ts-menu-item`), `dialog.ts-dialog`, `ts-tip` |

### Content-column breakpoints

The signed-in `.ts-main` is the named `page` inline-size query container.
Page layouts in `layout.css` respond to its width, not the viewport:
`.ts-grid-2` becomes one column below **48rem (768px)**, while `.ts-grid-3`
and `.ts-split` stack below **60rem (960px)** (the split keeps a hairline between
its stacked sections). The named queries keep nested containers from changing
which column the layout uses; they do not apply to public/auth layouts outside
`.ts-main`. Give those layouts their own container when reusing these helpers.

The queries use px because container-query rems resolve against the 14px root
font size, whereas viewport-query rems resolve against 16px. On desktop the
264px sidebar means these transitions happen at roughly 1030px and 1225px
window widths. At phone widths the sidebar becomes a drawer and the page
padding tightens at the existing 48rem viewport breakpoint. Give grid and list
children a shrinkable track when adding copyable or unbroken text.

Inline-size containment also creates a stacking context and containing block:
page-local fixed/absolute overlays may position relative to `.ts-main` and
cannot stack above outside chrome. In particular, an in-page dropdown scrolled
under the mobile sticky topbar is covered by it. Keep such menus below the
header; render viewport-level overlays outside `.ts-main`, or use the dialog
top layer.

## Charts

`Features/Home/OperationChart.cs`, `Features/Home/HomeRequestChart.cs` and
`Features/Analytics/AnalyticsLinePlot.cs` compute the chart geometry; slices
such as `Features/Home/ActivityChart.cshtml`,
`Features/Home/HomeRequestChartView.cshtml`,
`Features/Analytics/AnalyticsLineView.cshtml` and
`Features/Usage/UsageShareBar.cshtml` render SVG on the server: monotone curves, a gradient
area for single series, a dashed grid, and a focusable column for every plotted
bucket, with a guide line, dots and an SVG tooltip listing the point label and the
exact value of every series. Each additional group of three series reuses the
categorical tokens with a distinct dash length, matching curves, legend strokes,
tooltip keys and markers. The tooltip is sized from its monospaced text and
flips left near the plot end. Datastar initializes `ChartInteraction` with a
cached chronological column list and one displayed tooltip overlay. A line
chart is one Tab stop, starting on the latest point; arrow keys, Home and End
move between points. Pointer hover and keyboard focus show the tooltip; Escape
dismisses it. Named chart regions do not add a separate Tab stop. Rings and
share bars sit in a focusable `ts-chart-meter` whose CSS tooltip shows name
and percent on `:hover` and `:focus-visible`. No chart library is loaded.

## Performance

The C# host has no response-compression middleware. Datastar loads from the
dashboard with a pinned SRI hash (`AssetUrls.DatastarIntegrity` in
`Platform/Assets/AssetUrls.cs`). Every static file is linked with its content
hash (`Assets.Url(...)` in slices adds `?v=` plus the first six bytes of its
SHA-256; the CSS build hashes the stylesheet's `url()`s) and is cached as
immutable when that version matches. `web/build/build-css.mjs --release` and
`web/build/build-js.mjs --release` write maximum-quality brotli siblings, but the
project build does not pass `--release` and the host does not negotiate
precompressed files. The Go application's hover speculation rules and
`Clear-Site-Data` prefetch clearing are not ported.

## Motion

Page navigations show a full-width gradient progress bar
(`body.ts-nav-pending`, set by Datastar in `UI/Slices/ShellDocument.cshtml`
and `UI/Slices/Document.cshtml`). A form
marked `data-submit-once` is not sent again while that navigation loads, and
its submit buttons read as busy; use it where a second submission would fail
or repeat an action. A long create shows its own progress inside the form:
the Create database dialog covers its fields with a progress panel from the
moment the form is submitted, announces it through a `role="status"` line,
and the new database opens on a ready notice (`?created=1`). Motion uses
`--ts-fast` and `--ts-base` for transitions and `--ts-loop` for one cycle of
an ongoing wait. Fonts are preloaded (the monospace face only once someone
is signed in, since signed-out pages never show it) and use
`font-display: block`, so text never flashes a fallback face.
`prefers-reduced-motion` disables animation.
