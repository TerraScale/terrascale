# TerraScale Dashboard Design System

The Usage page (`Features/Usage/Pages/Usage/UsageSlice.cshtml`) is the reference. Every other page is refactored to match it. Source styles live in readable partials under `src/TerraScale.Dashboard/Styles/` and `src/TerraScale.Dashboard/Features/`; the build serves exactly one generated `wwwroot/css/terrascale.css` file and no page links a separate stylesheet.

## Principles

1. **Flat, not boxed.** No cards or panels around sections. Separate sections with 1px `--ts-line` hairlines and whitespace.
2. **One screen first.** The main view of a page fits 1440×900 without scrolling. Put secondary content behind tabs, dialogs or pagination.
3. **Server-rendered state.** Tabs, filters and pages are GET query params (`?tab=`, `?status=`, `?page=`) and every state is linkable. JS only enhances.
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
├─ toolbar         tabs left · filters right · hairline below · 60px tall on every tab
└─ sections        ts-section-head (title + meta | one primary action)
                   content: KPI row · table · chart · two-column split
                   hairline between sections
```

- **Top spacing:** `.ts-main` gets reduced top padding on toolbar pages. Content must start at the same y on every tab, so check it with a bounding-box measurement.
- **Two columns:** main content on the left and a narrow aside on the right (limits, breakdowns). The grid starts at 64rem, and the aside has a left hairline.

## Components

Class names below are the Usage-page ones (`ts-usage-*`). **When a second page adopts a pattern, promote it:** rename it to the generic name (drop `usage-`), keep it in the `features` layer, update the Usage slice, and delete the old selector.

| Pattern | Class (today → generic) | Rules |
|---|---|---|
| Toolbar | `ts-usage-toolbar` → `ts-toolbar` | One control tall on every tab. Filter labels are `ts-sr-only`. |
| Tabs | `ts-tabs` + `ts-tab` inside the toolbar | Underline style with a 16px icon, `--ts-fs-body` text, semibold when selected, 2px accent underline on the hairline. Links use `aria-current="page"` and `aria-selected`. |
| Filters | `FilterMenu(...)` helper, `ts-usage-filters` → `ts-filters` | Each filter is a hidden input plus `details.ts-menu` with `[data-filter-value]` options, and the JS submits the GET form. Always offer an "All …" option and a **Clear** link when a filter is active. |
| Section head | `ts-usage-head` → `ts-section-head` | Holds `h2.ts-panel-title`, a `p.ts-meta` one-line description, and at most one primary button on the right. |
| KPI row | `ts-usage-kpis` + `ts-usage-kpi-value` → `ts-kpis` | A `<dl>` with 3–4 items, each with a 2px left rule. `dt` = 14px icon + label, `dd` = 1.5rem semibold value (+ badge), `small.ts-meta` = one line of context. Add an optional `ts-bar-track` progress bar. |
| Stats list | `StatList(...)` / `ts-usage-stats` → `ts-stats` | Compact icon + label, value and detail, for secondary figures. |
| Settings list | `ts-usage-settings` → `ts-settings-list` | A `<dl>` of label/value rows with hairlines, for read-only facts such as limits and rates. |
| Table | `ts-table-wrap` + `ts-table` | Uppercase muted headers. The first column is 14px icon + `ts-table-strong` name. Numbers use `ts-table-num` and are right-aligned. Status uses a badge. Scrolls horizontally on mobile and never squeezes. |
| Grouped table | `tbody` per group, `ts-usage-cap-project` / `-database` | The parent row leads. Child rows indent by `--ts-xl`, and a hairline separates groups. |
| Inline edit table | `ts-usage-caps`, `ts-usage-cap` | One `<form>` wraps the table, and the submit button sits in the section head (`form="id"`). Inputs carry the unit prefix (`$`), `pattern`, and `aria-describedby` pointing to the computed column. Placeholders say what blank means. |
| Pagination | `ts-usage-pager`, `ts-usage-page[aria-current]` → `ts-pager` | "Showing a–b of N" on the left and Previous · numbers · Next on the right. The page size is 10, and hrefs keep the active filters. |
| Empty state | `EmptyState(title, description)` | Used when a list or filter returns nothing. Says what to do next. |
| Badge | `ts-badge ts-badge-{success,warning,danger,info,neutral}` | Lowercase state words (`paid`, `open`, `active`). Tone maps from state and never from rank. |
| Alert | `ts-alert ts-alert-{success,info,warning,danger}` | Contains `ts-alert-title` (icon + text) and an optional `ts-alert-body`. Placed under the section head. |
| Button | `ts-btn` + `ts-btn-primary` / `-secondary` / `-outline` / `-ghost`, `ts-btn-sm` | One primary per section. Icon first at 16px. External or billing links are `<a>` with an ExternalLink icon. |
| Dialog | `dialog.ts-dialog`, `[data-open-dialog]`, `[data-close-dialog]`, `data-autoopen` | For edits that need consent or long text. Reopens itself on a validation error. The footer has Cancel (ghost) and Save (primary). |
| Chart | `Charts.TimeSeriesChart(times, series, label, tickFormat)`, `Charts.Ring` | Lines are 2px with `ts-cat-N` per series and a zero-based y axis. Show a swatch legend with totals in the head. The hover tooltip shows the full date and time (UTC) and every series value. The overlay is `aria-hidden`, and the legend or totals carry the text. |
| Progress | `ts-bar-track` > `ts-bar-fill` | Needs `role="img"` and an `aria-label` with the percent. Warning or danger tone when ≥80% / ≥100%. |

## Content rules

- **Titles:** sentence case, short nouns ("Payment history", "Caps by project and database").
- **Meta line:** one sentence that says the scope ("Organization totals for the current billing period").
- **Numbers:** `N0` with separators, money as `$41.40`, dates as `MMM d, yyyy`, times as `HH:mm 'UTC'`. Say "No cap", "No limit" or "Not reported" and never leave a blank cell.
- **Icons:** only `Icons.*` names that exist (check `Shared/Components/Icons.cs`, since unknown names fall back to a circle). Use 14px in labels and 16px in buttons and tabs.
- **Accessibility:** every input has a `<label>` (visible or `ts-sr-only`), sections use `aria-labelledby`, and a state is never shown by color alone.

## Refactoring a page: checklist

1. Remove cards and panels, and replace them with flat sections and hairlines.
2. Put tabs and filters in one toolbar, and move secondary views to `?tab=`.
3. Give each section a head (title, meta line, and at most one primary action).
4. Turn headline numbers into a KPI row. Turn long lists into paginated, filterable tables with an empty state.
5. Move rarely used or consent-heavy edits into a dialog, and keep frequent edits inline.
6. Promote any `ts-usage-*` class you reuse to its generic name. Don't copy it.
7. Verify with screenshots at 1440×900 and 390×844, and check that the tabs and content sit at the same y on every tab.
8. Run `npm run build:css`, the brace lint, `dotnet build -warnaserror`, fast tests, slow tests and `npx playwright test`. Add screenshots to the PR and don't commit them.
