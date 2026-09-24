# TerraScale Dashboard Design System

The Usage page (`internal/features/usage/usage.templ`) is the design reference. Feature pages use server-rendered [templ](https://templ.guide/) components; their generated Go files are checked in, regenerated with `make generate`, and verified with `make check-generated`. Readable CSS sources live under `web/assets/css/` and `web/assets/Features/`; `npm run build:css` bundles them with Lightning CSS into the single stylesheet at `web/static/css/terrascale.css`.

## Principles

1. **Flat, not boxed.** No cards or panels around sections. Separate sections with 1px `--ts-line` hairlines and whitespace.
2. **One screen first.** The main view of a page fits 1440×900 without scrolling. Put secondary content behind tabs, dialogs or pagination.
3. **Server-rendered state.** Pages and controls are rendered by templ. Tabs, filters and pages use linkable GET query params (`?tab=`, `?status=`, `?page=`) where applicable. Native links and forms remain the canonical navigation and submission behavior; JavaScript enhances them without replacing the server-rendered content.
4. **Reuse before adding.** Use an existing class or helper. A new pattern goes into the CSS once and is shared.
5. **Dark only.** Use tokens and never raw hex. Validate chart colors against `--ts-canvas`.

## Tokens (use these, nothing else)

| Group | Tokens |
|---|---|
| Surfaces | `--ts-canvas` #0E0F14 (page) · `--ts-surface` #16181F · `--ts-surface-muted` #1F222B · `--ts-line` #333846 |
| Ink | `--ts-ink` (primary text) · `--ts-ink-muted` (meta, labels, icons) |
| Accent | `--ts-accent` #3B82F6 (primary action, selected tab, bar fill) · tints via `rgba(var(--ts-accent-rgb), a)` |
| Status | `--ts-success` · `--ts-warning` · `--ts-danger` · `--ts-info`, reserved for state and always paired with an icon or label |
| Series | `--ts-cat-1` blue · `--ts-cat-2` orange · `--ts-cat-3` green, in fixed order and never cycled |
| Space | `--ts-xs` 4 · `--ts-sm` 8 · `--ts-md` 12 · `--ts-lg` 16 · `--ts-xl` 24 (px) |
| Type | `--ts-fs-meta` 12 · `--ts-fs-ui` 14 · `--ts-fs-body` 16 · `--ts-fs-panel-title` 18 · weights `--ts-fw-medium/semibold/bold` |
| Shape | `--ts-radius-badge` 6 · `--ts-radius-control` 8 · controls `--ts-control-h` 40 / `-lg` 44 |
| Fonts | `--ts-font-sans` (Oxanium) for UI · `--ts-font-mono` for numbers, IDs and code |

Breakpoints are only 40 / 48 / 64 / 80 / 96rem. Place the rule next to the one it modifies.

## Page anatomy

```
ts-page
├─ alerts (return/status messages, optional)
├─ toolbar         distinct-view tabs or page title left · filters right · hairline below
└─ sections        ts-section-head (title + meta | one primary action)
                   content: KPI row · table · chart · two-column split
                   hairline between sections
```

- **Top spacing:** signed-in pages start 16px below the desktop inset edge. Mobile pages keep enough clearance for the fixed navigation button. Content must start at the same y on every tab, so check it with a bounding-box measurement.
- **Navigation:** use a 14rem (224px) sidebar with 40px rows and 24px icon frames. The same width applies to the mobile drawer and Admin menu. Keep project and account popovers within the drawer, with names and actions readable at 390px.
- **Shared shell:** use `ui.Auth`, `ui.Shell` or `ui.Admin` from `internal/ui/layouts.templ` around feature components. The shell renders primary and secondary navigation, project and organization switching, account actions, and the mobile drawer. Menus use native `<details>/<summary>` and remain usable without JavaScript; the pinned Datastar Rocket runtime adds outside-click and Escape handling and manages the mobile drawer interaction.
- **Tabs:** use them to switch between a small number of distinct tasks or views. A page with one view uses a title in the toolbar; large feature areas group related routes and keep each route linkable.
- **Two columns:** main content on the left and a narrow aside on the right (limits, breakdowns). The grid starts at 64rem, and the aside has a left hairline.
- **Home overview exception:** the root overview has no page tabs. Use `.ts-home-overview` with workspace KPIs at the top of the left column, activity below them, and Getting started as a full-height right rail. Activity range is a compact menu inside the Activity section; `?range=` remains linkable.

## Components

Class names below are the Usage-page ones (`ts-usage-*`). **When a second page adopts a pattern, promote it:** rename it to the generic name (drop `usage-`), keep it in the `features` layer, update the Usage template, and delete the old selector.

| Pattern | Class (today → generic) | Rules |
|---|---|---|
| Toolbar | `ts-usage-toolbar` → `ts-toolbar` | One control tall on every tab. Filter labels are `ts-sr-only`. |
| Tabs | `ts-tabs` + `ts-tab` inside the toolbar | Underline style with a 16px icon, `--ts-fs-body` text, semibold when selected, 2px accent underline on the hairline. Links use `aria-current="page"` and `aria-selected`. |
| Filters | `ts-usage-filters` → `ts-filters` | Use a GET form with labeled controls so filters work without JavaScript. Always offer an "All …" option and a **Clear** link when a filter is active. |
| Section head | `ts-usage-head` → `ts-section-head` | Holds `h2.ts-panel-title`, a `p.ts-meta` one-line description, and at most one primary button on the right. |
| KPI row | `ts-usage-kpis` + `ts-usage-kpi-value` → `ts-kpis` | A `<dl>` with 3–4 items, each with a 2px left rule. `dt` = 14px icon + label, `dd` = 1.5rem semibold value (+ badge), `small.ts-meta` = one line of context. Add an optional `ts-bar-track` progress bar. |
| Stats list | `ts-usage-stats` → `ts-stats` | Compact icon + label, value and detail, for secondary figures. |
| Settings list | `ts-usage-settings` → `ts-settings-list` | A `<dl>` of label/value rows with hairlines, for read-only facts such as limits and rates. |
| Table | `ts-table-wrap` + `ts-table` | Uppercase muted headers. The first column is 14px icon + `ts-table-strong` name. Numbers use `ts-table-num` and are right-aligned. Status uses a badge. Scrolls horizontally on mobile and never squeezes. |
| Grouped table | `tbody` per group, `ts-usage-cap-project` / `-database` | The parent row leads. Child rows indent by `--ts-xl`, and a hairline separates groups. |
| Inline edit table | `ts-usage-caps`, `ts-usage-cap` | One `<form>` wraps the table, and the submit button sits in the section head (`form="id"`). Inputs carry the unit prefix (`$`), `pattern`, and `aria-describedby` pointing to the computed column. Placeholders say what blank means. |
| Pagination | `ts-usage-pager`, `ts-usage-page[aria-current]` → `ts-pager` | "Showing a–b of N" on the left and Previous · numbers · Next on the right. The page size is 10, and hrefs keep the active filters. |
| Empty state | `ts-empty` | Used when a list or filter returns nothing. Says what to do next. |
| Badge | `ts-badge ts-badge-{success,warning,danger,info,neutral}` | Lowercase state words (`paid`, `open`, `active`). Tone maps from state and never from rank. |
| Alert | `ts-alert ts-alert-{success,info,warning,danger}` | Contains `ts-alert-title` (icon + text) and an optional `ts-alert-body`. Placed under the section head. |
| Button | `ts-btn` + `ts-btn-primary` / `-secondary` / `-outline` / `-ghost`, `ts-btn-sm` | One primary per section. Icon first at 16px. External or billing links are `<a>` with an ExternalLink icon. |
| Dialog | `dialog.ts-dialog`, `[data-open-dialog]`, `[data-close-dialog]`, `data-autoopen` | For edits that need consent or long text. Reopens itself on a validation error. The footer has Cancel (ghost) and Save (primary). |
| Chart | Time-series and ring charts | Lines are 2px with `ts-cat-N` per series and a zero-based y axis. Show a swatch legend with totals in the head. The hover tooltip shows the full date and time (UTC) and every series value. The overlay is `aria-hidden`, and the legend or totals carry the text. |
| Progress | `ts-bar-track` > `ts-bar-fill` | Needs `role="img"` and an `aria-label` with the percent. Warning or danger tone when ≥80% / ≥100%. |

## Content rules

- **Titles:** sentence case, short nouns ("Payment history", "Caps by project and database").
- **Meta line:** one sentence that says the scope ("Organization totals for the current billing period").
- **Numbers:** `N0` with separators, money as `$41.40`, dates as `MMM d, yyyy`, times as `HH:mm 'UTC'`. Say "No cap", "No limit" or "Not reported" and never leave a blank cell.
- **Icons:** use the page's existing icon set, and verify each icon name against its source. Use 14px in labels and 16px in buttons and tabs.
- **Accessibility:** every input has a `<label>` (visible or `ts-sr-only`), sections use `aria-labelledby`, and a state is never shown by color alone.

## Refactoring a page: checklist

1. Remove cards and panels, and replace them with flat sections and hairlines.
2. Put filters in the toolbar with a page title or a few distinct-view tabs; keep secondary views linkable with `?tab=` or their existing route.
3. Give each section a head (title, meta line, and at most one primary action).
4. Turn headline numbers into a KPI row. Turn long lists into paginated, filterable tables with an empty state.
5. Move rarely used or consent-heavy edits into a dialog, and keep frequent edits inline.
6. Promote any `ts-usage-*` class you reuse to its generic name. Don't copy it.
7. Verify with screenshots at 1440×900 and 390×844, and check that the tabs and content sit at the same y on every tab.
8. Run `make lint`, `make test-fast`, `make test-slow`, and `make build`. The slow target builds the pinned TypeScript SDK snapshot before running Playwright. Add screenshots to the PR and don't commit them.

## Templates, interactions, and CSS

- Keep page markup in feature-owned `*.templ` components under `internal/features/`; put shared document and signed-in shell markup in `internal/ui/layouts.templ`. Regenerate checked-in `*_templ.go` files with `make generate`; do not hand-edit generated output.
- The shared page wrapper loads `/js/rocket-components.js` once. That module imports the pinned Datastar Rocket v1.0.4 bundle and `dashboard.js`; feature pages should not add another Datastar runtime. Rocket components use light-DOM hosts so server-rendered children remain visible and native controls provide the no-script behavior.
- Keep interactions accessible in their HTML first: use links for navigation, forms for writes, `<details>/<summary>` for menus, and labeled native inputs for selection. Add Rocket or Datastar behavior as an enhancement, including explicit keyboard and ARIA behavior for custom interactions.
- Author CSS in `web/assets/css/` and `web/assets/Features/`. Run `make css` (or `npm run build:css`) to generate the only public stylesheet, `web/static/css/terrascale.css`; do not add a second CSS bundle or edit the minified output by hand.
