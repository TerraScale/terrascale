# TerraScale Dashboard Design System

Pages are server-rendered [templ](https://templ.guide/) components enhanced
with Datastar. Readable CSS lives in `web/assets/css/` (shared) and
`web/assets/Features/<Feature>/` (page-specific); `npm run build:css` bundles
it with Lightning CSS into the single stylesheet `web/static/css/terrascale.css`.

## Principles

1. **Compose the kit.** Build pages from the components in
   `internal/ui/components.templ` before writing markup or CSS.
2. **Flat and blue.** Content sits directly on the canvas; sections are split
   by hairlines, never boxed, and nothing glows (no gradients, blur or soft
   shadows; focus is a solid outline). Blue (`--ts-accent`) is the one accent
   for actions, tabs, charts and highlights. Only the navigation icons carry
   their own tones (`internal/ui/layout_helpers.go`) so sections are easy to
   find.
3. **Spacing by gap.** Containers space their children with `gap`, never with
   margins. Use the 4px scale `--ts-s1`…`--ts-s12`.
4. **Tokens only.** Colors, type, radius and motion come from
   `web/assets/css/tokens.css`. No inline `style` attributes (CSP
   `style-src 'self'`); use `data-*` attributes for finite variants.
5. **No custom JavaScript.** Tabs, filters and pagination are GET links and
   forms. Interactivity comes from native HTML (`<details>` menus, `<dialog>`
   with `command`/`commandfor`) and Datastar attributes; write JavaScript only
   as a last resort.

## Page anatomy

```templ
<main class="ts-page">
	@ui.PageHeader(ui.Header{Eyebrow: project, Title: "Databases", Description: "…"}) {
		<a class="ts-btn ts-btn-primary" href="…">New database</a>
	}
	@ui.Tabs("Database sections", tabs)
	@ui.Stats("Database metrics") {
		@ui.Stat("Databases", "3", "in this project")
	}
	@ui.Card(ui.CardProps{ID: "databases", Title: "Project databases", Flush: true}) {
		<div class="ts-table-wrap"><table class="ts-table">…</table></div>
	}
</main>
```

| Component | Use |
|---|---|
| `ui.PageHeader` | The page's single `h1`, eyebrow, description; children are actions |
| `ui.Tabs` | Underline tabs linking page sections (`?tab=`) |
| `ui.Card` | A flat section: heading row plus content, hairline-separated from its siblings |
| `ui.Stats` / `ui.Stat` | Headline numbers in a divided row (`ts-stats-compact` for word values) |
| `ui.Disclosure` | A button that expands a create or destructive form inline |
| `ui.Empty` | Empty lists and charts; children hold the call to action |
| `ui.Alert` | `info`, `success`, `warning`, `danger` messages |
| `ui.Badge` | Status pills: `neutral`, `info`, `success`, `warning`, `danger`, `violet` |
| `ui.Field` | Label, control (children), hint and error; the control spreads `ControlAttrs` |
| `ui.PasswordInput` | Password field with a Datastar reveal toggle |
| `ui.RegionMap` | Server-rendered region map; highlights the hubs chosen in a select |

## Classes

| Area | Classes |
|---|---|
| Layout | `ts-page` `ts-split` `ts-grid` `ts-grid-2` `ts-grid-3` `ts-stack(-sm/-lg)` `ts-row` `ts-row-between` `ts-toolbar` |
| Text | `ts-meta` `ts-muted` `ts-strong` `ts-mono` `ts-num` `ts-link` `ts-truncate` `ts-break` `ts-sr-only` |
| Controls | `ts-btn` + `-primary/-secondary/-ghost/-danger`, `-sm/-lg/-block`; `ts-icon-btn`; `ts-input` `ts-select` `ts-label` `ts-hint` `ts-check` `ts-input-group` |
| Forms | `ts-form` `ts-form-grid` `ts-form-actions` `ts-filters` `ts-filter-field` |
| Data | `ts-table-wrap` `ts-table` `ts-list` `ts-list-item` `ts-kv` `ts-pager` `ts-progress` `ts-code` `ts-snippet` `ts-dot` `ts-avatar` |
| Overlays | `ts-menu` (`<details { ui.MenuBehavior... }>` + `ts-menu-panel`, `ts-menu-item`), `dialog.ts-dialog`, `ts-tip` |

## Charts

`internal/ui/charts` renders SVG on the server: monotone curves, a gradient
area for single series, a dashed grid, and focusable hover columns (capped at
60) with a guide line, dots and an SVG tooltip listing the point label and the
exact value of every series. The tooltip is sized from its monospaced text and
flips left near the plot end, so it never leaves the viewBox. With Datastar
running a line chart is one tab stop, starting on the latest point; the arrow
keys, Home and End move between points. Rings and share bars sit in a
focusable `ts-chart-meter` whose `ts-chart-tooltip` shows name and percent
above it. Tooltips appear on `:hover` and `:focus-visible`; no chart
library or script.

## Performance

Responses are compressed (brotli, zstd, gzip) by `httpcompression`. Datastar
comes from the jsDelivr CDN (immutable, pinned by SRI). Every static file is
linked with its content hash (`static.URL` in templates; the CSS build hashes
the stylesheet's `url()`s) and cached as immutable. Release builds precompress
the stylesheet and scripts with brotli at maximum quality. Speculation rules
prefetch pages on hover, and every mutating request clears the prefetch cache
with `Clear-Site-Data`.

## Motion

Page navigations show a full-width gradient progress bar
(`body.ts-nav-pending`, set by Datastar in `internal/ui/layouts.templ`). A form
marked `data-submit-once` is not sent again while that navigation loads, and
its submit buttons read as busy; use it where a second submission would fail
or repeat an action. Fonts are preloaded (the monospace face only once someone
is signed in, since signed-out pages never show it) and use
`font-display: block`, so text never flashes a fallback face.
`prefers-reduced-motion` disables animation.
