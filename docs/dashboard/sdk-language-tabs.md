# SDK language tabs

Dashboard, documentation and landing use one flat language selector: a 16 px
self-hosted language mark, its name, and a blue underline for the selected tab.
Buttons have no individual background, border box or shadow. The surrounding
snippet may retain its surface and hairline border. Controls are at least 44 px
high and scroll within their row on narrow screens.

The canonical stylesheet is
`src/TerraScale.Dashboard/web/assets/css/sdk-tabs.css`; docs and landing import
that source directly. `.sdk-tabs` contains buttons with `role="tab"`. Sites use
their own theme tokens through `--sdk-ink`, `--sdk-muted` and `--sdk-accent` where
their token names differ. Dashboard and docs use the Graphite Blue tokens directly.

`web/assets/js/sdk-tabs.js` supplies delegated Left/Right and Home/End navigation,
wraparound, automatic activation and scrolling of the focused tab. Direction
follows the row's writing direction. Server-rendered groups add `data-sdk-tabs`
for selection and panel updates; Vue and Svelte retain their reactive selection.
Each group has a unique tab/panel ID association, `aria-selected`, and one
`tabindex="0"` tab. Hidden panels use the native `hidden` attribute.

The dashboard Database Overview and Home Connect views use the same row. Docs
register `SdkQueryTabs.vue` for the getting-started, SDK and SQL pages in every
locale. Landing's code panel preserves MCP and CLI beside the five SDK choices.
Docs and landing share their public example source in `landing-site/src/lib/highlight.ts`.
Existing copy controls continue to follow the selected snippet.

Python, JavaScript, Go, .NET and Rust marks come from Simple Icons 16.0.0 (CC0).
Each site's public `language-icons/` directory contains identical SVGs and their
source/license notice. They are bundled and served locally; no CDN dependency
or runtime asset fetch from an icon provider is required.

Before shipping any change to this control, compare before/after screenshots
against the UI skill in dashboard dark, landing dark and docs light/dark.
Check icons, selection, keyboard focus, snippet switching, copy, and narrow
screen overflow. Extend a retained browser journey instead of adding redundant
tests. Run affected Fast suites through `ts-ship` and dashboard browser journeys
through `ts-heavy-any`.
