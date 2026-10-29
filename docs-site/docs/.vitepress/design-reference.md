# Shared design and contract reference

## Dashboard snapshot

Repository: TerraScale/ts-dashboard. Reference commit:
`f0ef8be4edcef111f3d9f58c13151f90b9ccafd9`, matching the local `origin/main`
reference on October 12, 2026 (UTC). The checkout was clean. No uncommitted
work or migration implementation was imported, and the dashboard was not modified.

Imported verbatim:

- `theme/tokens.css` is the shared TerraScale tokens file (`light-dark()` colours; VitePress's
  `html.dark` sets `color-scheme`, see `style.css`).
- `logo-terrascale-principal.svg` (dark) and `logo-terrascale-blue.svg` (light) → public wordmarks.
- `web/static/brand/logo-icon-blue.svg` → public favicon.
- Geist, Geist Mono and Oxanium WOFF2 files and their OFL license texts.

`base.css`, `shell.css`, `layout.css` and `surfaces.css` informed the 264px
sidebar, 56px header, 4px spacing grid, 6px controls, blue active navigation,
toned entry icons, an 86vw/300px mobile drawer and flat sections separated by hairlines. Native VitePress
landmarks, skip link, search, outline and drawer behavior remain in use.
VitePress opens its documentation sidebar at 960px; the dashboard uses a
768px app-shell breakpoint. The landing content stacks at 768px. Font display
uses swap to keep long-form docs readable while fonts load.

After the dashboard migration merges, choose its reviewed merged revision,
refresh only design tokens and public brand/font assets, update this commit
reference and rerun desktop/mobile navigation and accessibility checks. Do not
import runtime, account data, private routes or migration plans.

## SDK evidence

Public npm metadata on October 12, 2026 (UTC) lists `@terrascale/sdk@0.0.3`.
The example lockfile records its published artifact integrity. Its installed
root exports, route manifest and runtime validation were inspected directly.

Committed source references inspected read-only:

- JavaScript: `8c8ded2c4d6bfc7fd1277329d60c92818cf3d524`.
- C#: `df852e3b4d3a4df324452d46792b20566eea5136`.
- Go: `305dceb49a22d83c1fa865cdef6ea3d0c5680a16`.

Public NuGet's TerraScale.DotNet flat-container endpoint returned 404. No public
immutable Go module version was qualified. Do not turn source versions into
release claims. The retained C# receipt applies to its own earlier exact
package/producer revisions, not these current source revisions.

## Privacy boundary

Every locale source, public downloadable example, rendered page, search chunk
and bundled text asset passes the public-output audit. Former internal inventory
URLs retain only a short public contract link. Legal routes retain only safe
availability/security guidance. AGENTS.md is human-owned and excluded from
site input; no protected instruction file or Git history is rewritten.

The audit is a regression guard plus manual source review, not proof that a
pattern scanner can detect every possible secret. A found credential must be
reported by location/type and rotated without reproducing its value.
