---
name: simple-ui
description: TerraScale UI standard — simple purpose, architecture, design and implementation, like Linear or Apple Notes. No taglines, helper text or secondary labels. Use whenever you create, change, review or write copy for any TerraScale page, screen, dialog, email or component (dashboard, ts-auth hosted pages, landing-site, docs-site chrome).
---

# Simple UI

> I want this to be as perfect as Linear's website or Apple Notes: simple purpose, simple architecture, simple design, simple implementation. Cut taglines, helper text and secondary labels. If a screen needs them to make sense, fix the layout. Every decision should elevate the user experience (UX), developer experience (DX) and agent experience (AX) at once, without breaking anything.

## Rules

1. One purpose per screen. A title, the content, one primary action.
2. Cards have a title only. No subtitle or description; rename the title if it is unclear.
3. Fields have a label only. No hint text. Encode format in the placeholder and input constraints (`type`, `min`, `max`, `pattern`, `maxlength`, sensible defaults). Explain only on validation failure.
4. Stats show label and value. No captions; exact metric definitions belong in `docs/`.
5. Empty states: one sentence, one action.
6. Dialogs: title and controls. Irreversible actions keep one short consequence sentence.
7. No eyebrows, taglines, subheadlines, "Recommended", "(optional)", "Visual preview" or similar secondary labels.
8. If removing text makes a screen confusing, change the layout, defaults, grouping or naming — never put the text back.

## Never cut

- Error and validation messages, status announcements (`role="status"`, `aria-live`).
- Security and consent safeguards: secret-shown-once notices, irreversible-action consequences, CLI device-code match checks, recovery-code warnings, legal text.
- Accessibility: `aria-label`, `ts-sr-only` text, `<label>` and `<legend>`. Drop `aria-describedby` that pointed at removed hints.
- Data itself (values, names, timestamps, code snippets the user copies).

## Page layout

Every dashboard page follows the same skeleton: header, stats strip, columns. The classes live in the shared CSS (`layout.css`, `surfaces.css`, `data.css`, `controls.css`), never in a feature stylesheet.

1. **Page header**: title left, one primary action right. On database pages the header sits under the database tabs.

   ```html
   <header class="ts-page-header">
     <h1 class="ts-page-title">Backups</h1>
     <div class="ts-page-actions"><button class="ts-btn ts-btn-primary">Back up now</button></div>
   </header>
   ```

2. **Stats strip** when the page has health or totals: 2 to 4 stats, label + value; `.ts-stat-meta` only for data (a time, a total), never captions.

   ```html
   <dl class="ts-stats" aria-label="Backup health">
     <div class="ts-stat"><dt class="ts-stat-label">Last backup</dt><dd class="ts-stat-value">2 h ago</dd><dd class="ts-stat-meta">Succeeded · 03:00 UTC</dd></div>
   </dl>
   ```

3. **Body**: `.ts-columns` with `.ts-columns-main` (the content the page is about) and `.ts-columns-side` (settings cards, storage, small forms). One-column pages skip it; the side column appears at 960px and up.

   ```html
   <div class="ts-columns">
     <div class="ts-columns-main"><!-- restore points, main cards --></div>
     <div class="ts-columns-side"><!-- schedule, storage --></div>
   </div>
   ```

4. **Card**: title left in the header, actions right; optional footer with Save/Cancel right, primary last.

   ```html
   <section class="ts-card ts-card-boxed ts-card-flush">
     <header class="ts-card-head"><div class="ts-card-heading"><h2 class="ts-card-title">Schedule</h2></div><div class="ts-card-actions"></div></header>
     <div class="ts-card-body"></div>
     <footer class="ts-card-foot"><button class="ts-btn ts-btn-secondary">Reset</button><button class="ts-btn ts-btn-primary">Save schedule</button></footer>
   </section>
   ```

5. **Create**: "New X" is `ts-btn ts-btn-primary ts-btn-sm` in the card header actions (or the page header when the page is about one list), and the empty state repeats it.

6. **Filters**: right side of the card header actions or a `.ts-toolbar`, inputs and button bottom-aligned; filters apply on change, so no Apply button; never `.ts-row` for a form.

   ```html
   <form class="ts-filters" method="get">
     <select class="ts-select" data-on:change="el.form.requestSubmit()"></select>
   </form>
   ```

7. **Lists**: `.ts-rows` for records with a status badge and one action on the right; `.ts-table` in `.ts-table-wrap` with the last column `.ts-table-actions` for wide data.

   ```html
   <ul class="ts-rows">
     <li><span class="ts-dot" data-tone="success" aria-hidden="true"></span><div class="ts-stack-sm"><span class="ts-rows-title">Today, 03:00 UTC</span><span class="ts-rows-meta">Scheduled · full</span></div><span class="ts-badge ts-badge-success"><span class="ts-dot" aria-hidden="true"></span>Succeeded</span><span class="ts-rows-size">412 MB</span><div class="ts-rows-actions"><button class="ts-btn ts-btn-secondary ts-btn-sm">Restore…</button></div></li>
   </ul>
   <div class="ts-rows-more"><button class="ts-btn ts-btn-ghost ts-btn-sm">Show older restore points</button></div>
   ```

8. **Presets over codes**: `.ts-chips` for a choice of 2 to 6 options (schedule presets, retention), the raw value (e.g. the cron) read-only in `.ts-stat-meta`-style mono text, a "Custom" chip revealing the raw input.

   ```html
   <fieldset class="ts-chips">
     <legend class="ts-label">Full backup</legend>
     <label class="ts-chip"><input type="radio" name="schedule" value="daily" checked><span>Daily</span></label>
     <label class="ts-chip"><input type="radio" name="schedule" value="weekly"><span>Weekly</span></label>
   </fieldset>
   ```

9. **Empty state**: one sentence plus the create button.

   ```html
   <div class="ts-empty" role="status"><p class="ts-empty-title">No restore points yet.</p><button class="ts-btn ts-btn-primary ts-btn-sm">Back up now</button></div>
   ```

10. **Destructive**: `ts-btn-danger` trigger in the danger card or the row actions, always opening a dialog; the dialog's danger submit sits right in `.ts-dialog-footer`.

11. **Saves do not reload the page**: forms post with Datastar and the endpoint answers with patched elements (pattern: `Features/Notifications/NotificationReadForm.cshtml`, `Features/Analytics/FilterMenuView.cshtml`). A full redirect only when the action leaves the page (delete, open another database).

    ```html
    <form method="post" action="/backups/schedule" data-on:submit="@post('/backups/schedule', {contentType:'form'})"></form>
    ```


## DX and AX

- Delete CSS classes, slice parameters and C# properties that only fed removed text; no dead code.
- Keep stable IDs, names and `data-*` hooks that tests, Datastar or agents use. Machine-readable content (OpenAPI, MCP, SVG map data) stays complete.
- Prefer native elements and existing components over new abstractions.
- Dashboard lint rejects the removed helper classes (`ts-hint`, `ts-card-description`, `ts-empty-detail`, `ts-stat-detail`, `ts-eyebrow`); do not reintroduce them under new names.

## Tokens

Dashboard styles use the tokens in `src/TerraScale.Dashboard/web/assets/css/tokens.css`; no raw hex, and no raw px where a token fits.

- Spacing, 4px grid: `--ts-s1` 4, `s2` 8, `s3` 12, `s4` 16, `s5` 20, `s6` 24, `s8` 32, `s10` 40, `s12` 48, `s16` 64. Icon to label `s2`; between controls `s2` or `s3`; card padding `s5`; between sections `s6` to `s10`. Space siblings with `gap`, never margins.
- Radius: `--ts-radius-sm` 4 (badges, tiles, inner items), `--ts-radius` 6 (controls, rows, menu items), `--ts-radius-lg` 10 (cards, menus), `--ts-radius-xl` 14 (dialogs), `--ts-radius-pill`.
- Type: `--ts-fs-xs` 12 (meta, badges), `sm` 13 (secondary, menus), `md` 14 (body), `lg` 16, `xl` 20 (card and dialog titles), `2xl` 24, `3xl` 32 (page titles, figures). Uppercase mono labels use the five `--ts-label-*` tokens together. Code and figures use `--ts-font-mono` with `tabular-nums`.
- Sizes: controls are `--ts-control-h-sm` 32, `--ts-control-h` 36 or `--ts-control-h-lg` 40 tall. Icons are 16px in a row and 14px for a trailing chevron or arrow. Table rows are 48px, their header 36px.
- Surfaces, darkest first: `--ts-canvas` (page), `--ts-surface` (cards, inputs), `--ts-surface-2` (menus, dialogs), `--ts-surface-3` (tiles, tracks). Row fills: `--ts-hover` on hover, `--ts-selected` for the current item.
- Lines: `--ts-line` (dividers, card borders), `--ts-line-strong` (overlays), `--ts-control-line` (inputs and outlined buttons; it keeps 3:1). No shadows.
- Text: `--ts-ink` (primary), `--ts-ink-2` (secondary), `--ts-ink-3` (labels, placeholders). Links `--ts-link`.
- Accent: `--ts-accent` fills with `--ts-on-accent` text; `--ts-accent-soft` tints. Status: `--ts-success`, `--ts-warning`, `--ts-severe`, `--ts-danger`, `--ts-info`; solid danger buttons use `--ts-danger-solid`. Charts take `--ts-chart-1` to `--ts-chart-5` in order.
- Focus: `outline: var(--ts-ring)` with a 2px offset, or a negative offset inside menus and tabs.
- Motion: `--ts-fast` for hover and focus, `--ts-base` for panels, `--ts-loop` for loops, all with `--ts-ease`.
- Loading (`loading.css`): buttons get their spinner from `button-loading.js`; elsewhere use `.ts-spinner` inline, `.ts-skeleton` (`.ts-skeleton-table`, `.ts-skeleton-chart`) for content that loads later, and `.ts-progress` for steps. Loading visuals appear after `--ts-fast`, so a fast load shows nothing, and they never change the layout. Keep `aria-busy` and `role="status"`.

## Proof

- Before/after screenshots of every changed page, shown in your T3 thread (repo rule in `AGENTS.md`). How:
  1. **Run both versions.** Before = `origin/main`, after = your branch; same seed data, same signed-in role, same viewport (1280x800, plus 390x844 if the page has a narrow layout), every supported theme. Start the stack with `make dev`, or the journey's environment (`bash e2e/env/local.sh`, see `e2e/AGENTS.md`) for signed-in states.
  2. **Capture** with Playwright 1.63 (its Chromium is installed on every dev server): `npx -y playwright@1.63.0 screenshot --viewport-size=1280,800 --full-page <url> <file>` for a page you can reach by URL.
  3. **Save** to `~/.local/state/shots/<branch>/<page>-<theme>-before.png` and `-after.png`. Never commit screenshots (no `docs/qa/` folders).
  4. **Show them in the thread.** With T3 tools: `html_preview`, then `html_render` one grid per page (columns before | after, a caption row with page, theme and viewport, `<img src="<absolute path>">`; local images are inlined). Without T3 tools (GLM in OpenCode): put a `SCREENSHOTS` section in your final reply with one line per page, `![before: <page> <theme>](<absolute path>) ![after: <page> <theme>](<absolute path>)`.
  5. Compare them against every rule in this file before you open the PR.
- CI runs the journey on the pull request; it audits every page (console errors, failed requests, overflow, axe WCAG 2.2 AA).
- When a page's heading or data changes, update its state in `e2e/pages/inventory.ts`; never weaken a check.
- [ ] Before shipping UI, compare actual before/after screenshots against every applicable rule below in dark and supported light themes; fix default-styled or unfinished controls first.

## Tabs and segmented controls

- SDK language switchers always use the shared `.sdk-tabs` component (stylesheet and keyboard handler) documented in SDK language tabs.
- Use a flat row with a self-hosted official language logo and name. Never nest boxed buttons inside another boxed control or rely on browser default button styling.
- Show selection with one blue accent underline. Keep hover, selected and visible keyboard focus distinct in light and dark themes.
- Select the first tab initially so the active language is visible on narrow screens.
- Use tablist/tab/tabpanel semantics, associated IDs, selected state and a single tab stop. Support Left/Right, Home/End and wraparound; selection must switch the snippet and copy target together.
- Verify desktop and narrow layouts, pointer and keyboard switching, loaded icons, and screenshots in every supported theme across dashboard, docs and landing.
- When shared tab sources change, update the importing entry points in docs and landing in the same change so all three sites run their CI builds and deployments. Workflow dependency changes belong to the CI owner.
