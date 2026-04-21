# Handoff: TerraScale Landing Page Redesign

## Overview

This package contains a high-fidelity design exploration for the **TerraScale marketing site** — a dark-mode, developer-focused landing experience built around a primary home page, a dedicated pricing page, and a contact page. The design is locked to a single committed configuration (detailed below), with the other explored variations preserved in the source for reference.

The goal of this handoff is to translate the HTML design reference into the TerraScale production codebase.

---

## About the Design Files

The files in `design-reference/` are a **design reference created in HTML/React-via-Babel** — a prototype demonstrating intended look, layout, copy, and behavior. **They are not production code.**

Your job is to **recreate these designs inside the existing TerraScale codebase**, using whatever framework, component primitives, styling approach, routing, and conventions are already established there (Next.js / Astro / plain React / etc.). Do not copy the prototype verbatim — it uses inline `<script type="text/babel">` + CDN React and plain `<style>` blocks, which is fine for prototyping but is not how the production site should be built.

If the target codebase has:
- A **design token system** (CSS custom properties, Tailwind config, theme file) → port the tokens in the "Design Tokens" section into it.
- A **component library** (Button, Card, Badge) → re-skin and reuse those, don't introduce parallel components.
- A **routing solution** → use it; the prototype's `localStorage`-backed page switch is a stand-in.
- A **font-loading strategy** (next/font, fontsource, self-hosted `@font-face`) → use that; the prototype's raw `@font-face` is illustrative only.

If no production codebase exists yet, pick whichever framework is appropriate for a marketing site (Next.js App Router is a reasonable default) and implement accordingly.

---

## Fidelity

**High fidelity.** Colors, typography, spacing, layout, copy, and interaction states should be matched pixel-perfectly. Every value in this README is intentional.

The locked configuration is:
- `data-theme="focused"` (blue primary, green success)
- `data-glass="subtle"` (0.025 alpha glass fills)
- `data-atmosphere="dramatic"` (1.9× glow multiplier, strongest hero atmosphere)

Two alternative themes (`indigo`, `arctic`) and two alternative atmosphere levels (`minimal`, `standard`) are preserved in the CSS. Keep them, but ship `focused` + `subtle` + `dramatic` as the default.

---

## Pages / Routes

The site has three routes:

### 1. Home (`/`)
Single long-scroll page. In order:
1. **Header** (sticky top nav)
2. **HeroSection** — headline, subhead, two CTAs, three stat cards, floating decorative geometry, grid + scanline overlays driven by `--ts-atmo-*` vars.
3. **FeaturesSection** — six feature cards in a 3-column responsive grid.
4. **CodePanelSection** — language-tab switcher (TypeScript / Python / Go / Rust / C#) with syntax-highlighted code sample, bordered panel, copy-to-clipboard affordance.
5. **PricingSection** — three-tier pricing cards (Starter / Pro / Enterprise), middle tier marked "Most Popular" with elevated border + badge.
6. **CtaSection** — full-bleed gradient call-to-action band.
7. **Footer** — four columns of nav links + brand column + copyright row.

### 2. Pricing (`/pricing`)
Dedicated pricing page. In order:
1. **Header**
2. **PricingSection** (same as home, but as page intro)
3. **PricingFeatureTriad** — three feature callouts unique to pricing page
4. **DetailedPricingTable** — feature-matrix comparison table, rows grouped by category
5. **PricingFAQ** — accordion-style FAQ
6. **PricingCTA** — closing CTA
7. **Footer**

### 3. Contact (`/contact`)
Rendered by `ContactPage` in `ts-pages.jsx`. Contains contact form with `FormField` component. `AboutPage` also exists in the same file but is not wired into the router — treat it as unused scaffolding unless the product team asks for it.

---

## Design Tokens

All tokens live as CSS custom properties on `[data-theme="focused"]`. Port these into your token system of choice.

### Colors — Focused (Default) Theme

| Token | Value | Usage |
|---|---|---|
| `--ts-bg-base` | `#050508` | Page background |
| `--ts-bg-surface` | `#09090F` | Section backgrounds |
| `--ts-bg-card` | `#0B0B14` | Card / panel fill |
| `--ts-code-bg` | `#07070D` | Code block background |
| `--ts-divider` | `#15151F` | Hairlines, borders between sections |
| `--ts-primary` | `#2563EB` | Primary brand blue (buttons, links, accents) |
| `--ts-primary-light` | `#60A5FA` | Primary hover / lighter accents |
| `--ts-secondary` | `#10B981` | Success / emerald (stat positives, "included" checks) |
| `--ts-tertiary` | `#8B5CF6` | Violet accent (rare — enterprise tier highlight) |
| `--ts-text` | `#F1F5F9` | Body text |
| `--ts-text-muted` | `#9BACCB` | Secondary text, captions |
| `--ts-glass-bg` | `rgba(37, 99, 235, 0.025)` | Glass surface fill (subtle modifier active) |
| `--ts-glass-border` | `rgba(148, 163, 184, 0.16)` | Glass surface border |
| `--ts-grid-color` | `rgba(37, 99, 235, 0.055)` | Hero grid overlay lines |
| `--ts-hero-glow-top` | `rgba(37, 99, 235, 0.22)` | Hero top radial glow |
| `--ts-hero-glow-mid` | `rgba(37, 99, 235, 0.10)` | Hero mid radial glow |
| `--ts-hero-glow-accent` | `rgba(16, 185, 129, 0.05)` | Hero accent glow (emerald) |
| `--ts-gradient-primary` | `linear-gradient(135deg, #3B82F6 0%, #2563EB 100%)` | Primary CTA gradient |

### Atmosphere Multipliers (dramatic)

| Token | Value |
|---|---|
| `--ts-atmo-mult` | `1.9` |
| `--ts-atmo-grid-opacity` | `1` |
| `--ts-atmo-float-opacity` | `1.25` |

### Alternative Themes (preserve in code)

Full definitions for `indigo` and `arctic` themes are in `design-reference/TerraScale Redesign.html` lines ~50–150. Port them as additional token sets so the theme can be swapped via a `data-theme` attribute on `<html>` or `<body>`.

### Typography

| Token | Value |
|---|---|
| `--ts-font-body` | `'Quantico', system-ui, sans-serif` |
| `--ts-font-accent` | `'Roboto', system-ui, sans-serif` |
| `--ts-font-mono` | `'Roboto Mono', 'JetBrains Mono', ui-monospace, monospace` |

**Font usage rules:**
- **Quantico** — default for everything (headlines, body, buttons). Weights 400 and 700.
- **Roboto** — chip labels, eyebrow/kicker text above section headers, micro-meta (timestamps, tag labels, form field labels). Weights 300/400/500/700.
- **Roboto Mono** — all code, inline `<code>`, numeric stat values, version badges. Weights 400/500/700.
- **JetBrains Mono** — fallback for `Roboto Mono`, also self-hosted.

**Loading strategy in the prototype:** Roboto + Roboto Mono via Google Fonts CDN; Quantico + JetBrains Mono as self-hosted `woff2` (`fonts/` folder, provided in this handoff). Use your codebase's preferred font-loading mechanism; make sure `font-display: swap` is used.

### Type Scale (observed in design)

| Use | Size | Weight | Line-height | Letter-spacing |
|---|---|---|---|---|
| Hero H1 | `clamp(3rem, 6vw, 5.5rem)` | 700 | 1.05 | `-0.02em` |
| Section H2 | `clamp(2rem, 4vw, 3rem)` | 700 | 1.1 | `-0.01em` |
| Card heading | `1.25rem` (20px) | 700 | 1.3 | 0 |
| Body | `1rem` (16px) | 400 | 1.6 | 0 |
| Muted / caption | `0.875rem` (14px) | 400 | 1.5 | 0 |
| Eyebrow / kicker | `0.75rem` (12px), Roboto 500 | 500 | 1 | `0.12em`, uppercase |
| Stat number | `2.5rem`–`3rem`, Roboto Mono 700 | 700 | 1 | `-0.01em` |
| Code | `0.875rem`, Roboto Mono 400 | 400 | 1.6 | 0 |

### Spacing

The design uses an 8-point grid. Common section paddings: `clamp(4rem, 8vw, 8rem)` vertical on desktop, `1.5rem` horizontal mobile / `max(1.5rem, 4vw)` desktop.

| Token | Value |
|---|---|
| `xs` | 4px |
| `sm` | 8px |
| `md` | 16px |
| `lg` | 24px |
| `xl` | 32px |
| `2xl` | 48px |
| `3xl` | 64px |
| `4xl` | 96px |
| `5xl` | 128px |

### Border radius

| Use | Value |
|---|---|
| Small chips, badges | `6px` |
| Buttons, inputs | `10px` |
| Cards, panels | `16px` |
| Hero code windows | `18px` |

### Shadows

Cards are mostly flat on dark; elevation comes from borders + glass + hover lift.

| Use | Value |
|---|---|
| Card hover | `0 12px 40px -12px rgba(37, 99, 235, 0.35)` |
| Floating CTA | `0 20px 60px -20px rgba(37, 99, 235, 0.5)` |
| Popular-tier pricing card outline | `0 0 0 1px var(--ts-primary), 0 24px 60px -24px rgba(37, 99, 235, 0.45)` |

---

## Components (authoritative list)

Every component referenced in the router. Source file noted.

### `ts-sections-1.jsx`
- **`TerraScaleLogo`** — SVG mark + wordmark. Use `assets/logo-principal.svg` / `assets/logo-white.svg` / `assets/icon-blue.svg`.
- **`Icon`** — inline SVG icon wrapper; a small set of stroked icons used across features/nav.
- **`Header`** — sticky top nav. Logo left, 4 nav links center-right, secondary "Sign in" + primary "Start free" CTA right. Underline-wipe hover on links (`.link-wipe` class in HTML).
- **`HeroSection`** — two-column on desktop (copy left, visual right); stacks on tablet/mobile. Includes `.hero-atmo-overlay`, `.hero-grid-overlay`, `.hero-scan-overlay` absolute-positioned children for atmosphere.
- **`StatCard`** — used inside hero, three-across. Left-border accent (1px solid `--ts-primary`), mono numeric label, Roboto caption.

### `ts-sections-2.jsx`
- **`FeaturesSection`** — 3×2 grid on desktop, 2×3 on tablet, 1×6 on mobile. `gap: 24px`.
- **`SectionHeader`** — eyebrow (Roboto uppercase, primary color), H2, lead paragraph. Centered by default; variant prop for left-aligned.
- **`FeatureCard`** — icon top-left in a 44×44 rounded square with 10% primary fill, heading, body copy, optional "Learn more →" link. Hover: `.ts-tile-hover` lift.
- **`CodePanelSection`** — language tab row (5 tabs with SVG icons from `assets/lang-*.svg`), code panel with window chrome (3 traffic-light dots, filename label), copy button top-right. Only the active language's snippet is rendered.
- **`HighlightedCode`** — minimal custom syntax highlighter; classes: `.tok-keyword`, `.tok-string`, `.tok-comment`, `.tok-fn`, `.tok-num`, `.tok-op`. Port the highlighter logic from the prototype or swap for Shiki / Prism in production.

### `ts-sections-3.jsx`
- **`PricingSection`** — three-card row. Each card uses `PricingCard`.
- **`PricingCard`** — props: `tier`, `price`, `period`, `description`, `features[]`, `cta`, `popular?`. Popular variant elevates with primary border + "Most popular" badge.
- **`CtaSection`** — full-width gradient band, center-aligned headline + two buttons. Gradient: `--ts-gradient-primary` at 135°, with an overlay of `--ts-hero-glow-accent` at 30% to add warmth.
- **`Footer`** — 5 columns (Brand + blurb, Product, Developers, Company, Resources). Bottom row: copyright + social icon row. Links use `.link-wipe`.

### `ts-pages.jsx`
- **`AboutPage`** — not routed. Ignore unless requested.
- **`ContactPage`** — route `/contact`. Two-column on desktop: left = intro copy + contact details (email, address, hours), right = form (Name, Email, Company, Message, Submit).
- **`FormField`** — Roboto label above glass-surface input. Focus state: 2px outline using `--ts-primary` at 35% alpha.

### `ts-pricing-page.jsx`
- **`PricingPage`** — route `/pricing`.
- **`PricingFeatureTriad`** — three feature callouts (icon + heading + body).
- **`DetailedPricingTable`** — feature-matrix table. Column headers = tier names, rows grouped by category header row. Checkmark = `--ts-secondary`, em-dash = muted.
- **`PricingFAQ`** — collapsible `<details>`/`<summary>` pattern. Chevron rotates 180° on open.
- **`PricingCTA`** — mirrors `CtaSection` but pricing-specific copy.

---

## Interactions & Behavior

### Scroll reveal
Implemented globally in the `App` component via `useScrollReveal(deps)`:
- Every `<section>` auto-gets `.reveal`.
- Grid containers with 2–6 children become `.reveal-stagger`, children get `.reveal`.
- An `IntersectionObserver` (`threshold: 0.12`, `rootMargin: '0px 0px -40px 0px'`) adds `.is-in` when each enters viewport.
- Transition: 720ms cubic-bezier(.22, 1, .36, 1) on opacity (0→1), transform (translateY(22px)→0), filter (blur(4px)→none).
- Stagger delays: 0, 80, 160, 240, 320, 400ms for nth-child 1–6.
- Observer disconnects and re-tags on route change.

In production, use your framework's equivalent (e.g. a React hook + `IntersectionObserver`, Framer Motion's `whileInView`, or `motion-one`). Respect the same timings.

### Hover states
- **`.ts-tile-hover`** — `translateY(-3px)`, 260ms `cubic-bezier(.2,.8,.2,1)`. Apply to feature cards, pricing cards, stat cards.
- **`.link-wipe`** — animated 1px underline wiping left→right via `::after` + `scaleX`, 340ms `cubic-bezier(.22,1,.36,1)`. Apply to all nav and footer links.
- **`.cta-shine`** — 50%-width gradient sheen sweeps across primary buttons on hover, 900ms `cubic-bezier(.4,0,.2,1)`. Apply to all gradient CTAs.
- **`.breathe`** — 5.5s breathing scale (1 ↔ 1.04) with opacity pulse. Applied to ambient orb decorations in hero.
- **`.pulse-dot`** — 2.2s opacity pulse (1 ↔ 0.45). For status dots ("All systems operational" indicator in header right edge, if present in your version).
- **`.float-up` / `.float-down`** — 14s/16s infinite Y-axis float with subtle rotation. For decorative geometric shapes in hero background.

### Reduced motion
`@media (prefers-reduced-motion: reduce)` disables all of the above. **Preserve this rule** in the port — it's non-negotiable for accessibility.

### Interactive behavior
- **Language tabs** (code panel) — click to switch rendered snippet. Active tab: primary-color bottom border + text. Inactive: muted. 180ms fade between snippets.
- **Copy button** (code panel) — copies the visible snippet via `navigator.clipboard.writeText`, briefly swaps icon to a checkmark for 1500ms.
- **FAQ accordion** — native `<details>`/`<summary>` OK; rotate chevron via CSS `details[open] summary .chevron { transform: rotate(180deg); }`.
- **Contact form** — client-side validation on submit. Required: name, email (valid format), message. Error state: red border + helper text. Success state: swap form with a confirmation card. Wire the actual submit to whatever endpoint the backend team provides.
- **Router** — prototype uses local state + `localStorage`. Replace with your framework router (`next/link`, `react-router`, etc.) and preserve page state via URL, not localStorage.

### Header behavior
Sticky at top, `backdrop-filter: blur(14px)`, `background: rgba(var(--ts-bg-base-raw), 0.72)`, bottom border `--ts-divider`. No scroll-based resize animation — it stays a constant height.

---

## Responsive Breakpoints

The prototype uses `clamp()` heavily but the major breakpoints are:

| Breakpoint | Width | Behavior |
|---|---|---|
| Mobile | `< 640px` | Single column; hero stacks (copy then visual); nav collapses to hamburger; stat cards stack; feature grid 1 column; footer columns stack. |
| Tablet | `640–1024px` | Feature grid 2 columns; hero still stacks but visual centers; pricing cards wrap to 2+1. |
| Desktop | `≥ 1024px` | Full two-column hero, 3-column feature grid, 3-column pricing, 5-column footer. |
| Wide | `≥ 1440px` | Max-width container `1200px` centered; side gutters grow. |

Hamburger nav for `< 768px` is **not** implemented in the prototype — it's a gap. Spec it using your existing mobile-nav pattern.

---

## Assets

All in `design-reference/assets/`.

| File | Usage |
|---|---|
| `logo-principal.svg` | Primary logo (header, hero, footer) |
| `logo-white.svg` | Monochrome white variant (on photo backgrounds if added later) |
| `icon-blue.svg` | Standalone mark (favicon base, social meta) |
| `lang-typescript.svg` | Language tab icon |
| `lang-python.svg` | Language tab icon |
| `lang-go.svg` | Language tab icon |
| `lang-rust.svg` | Language tab icon |
| `lang-csharp.svg` | Language tab icon |

Fonts in `design-reference/fonts/`:

| File | Family |
|---|---|
| `quantico.woff2` | Quantico 400 |
| `quantico-bold.woff2` | Quantico 700 |
| `jetbrains-mono.woff2` | JetBrains Mono 400 |
| `jetbrains-mono-bold.woff2` | JetBrains Mono 700 |

Roboto and Roboto Mono are loaded from Google Fonts in the prototype. Decide per your team's policy (self-host for privacy / perf, or keep the CDN link).

---

## Accessibility Checklist (please uphold)

- Color contrast: the palette was picked so body text (`#F1F5F9` on `#050508`) exceeds WCAG AAA, and muted text (`#9BACCB`) exceeds AA normal. Preserve these values.
- All interactive elements need visible focus styles. Prototype uses `outline: 2px solid var(--ts-primary); outline-offset: 2px;` on `:focus-visible`.
- Animations respect `prefers-reduced-motion` (see above).
- All icons purely decorative get `aria-hidden="true"`; icons that carry meaning get an accessible label.
- Form fields use `<label>` bound via `htmlFor` — don't replace with placeholder-only fields.
- Language tabs should be a `role="tablist"` with `role="tab"` children and proper `aria-selected` / `aria-controls`.

---

## Files in This Handoff

```
design_handoff_terrascale_landing/
├── README.md                       ← this file
└── design-reference/
    ├── TerraScale Redesign.html    ← the prototype entry point
    ├── ts-sections-1.jsx           ← Header, Hero, StatCard, Logo, Icon
    ├── ts-sections-2.jsx           ← Features, SectionHeader, CodePanel
    ├── ts-sections-3.jsx           ← Pricing, CTA, Footer
    ├── ts-pages.jsx                ← Contact + (unused) About
    ├── ts-pricing-page.jsx         ← Pricing page + sub-components
    ├── assets/                     ← SVG logos + language icons
    └── fonts/                      ← Quantico + JetBrains Mono woff2
```

**To preview the prototype locally:** the HTML uses CDN React + Babel, so any static server will do —

```bash
cd design-reference && python3 -m http.server 8000
# open http://localhost:8000/TerraScale%20Redesign.html
```

---

## Implementation Suggestions (non-binding)

1. **Start with tokens.** Port the color, type, spacing, radius, and shadow tables above into your token system *first*. Everything else depends on these.
2. **Build primitives next.** `Button`, `Card`, `Badge`, `Input`, `StatDisplay`, `Eyebrow` — shared across sections. Reuse existing ones in the codebase where possible.
3. **Then section components**, in home-page order (Header → Hero → Features → CodePanel → Pricing → CTA → Footer). Ship the home route before starting on `/pricing` and `/contact`.
4. **Code panel last.** The syntax highlighter in `HighlightedCode` is bespoke; production should use **Shiki** (compile-time) or **Prism** (runtime). Match the token colors in the prototype's CSS (`.tok-*` classes).
5. **Atmosphere layers (`.hero-atmo-overlay`, `.hero-grid-overlay`, `.hero-scan-overlay`)** are pure CSS — port them literally. They're what makes the hero feel alive.
6. **Do a motion pass at the end.** Implementing scroll-reveal before content is in place makes layout work harder. Add `.reveal` wiring, link-wipe, cta-shine, and breathe last.
7. **QA with reduced-motion on.** The page must still feel designed, not broken, when all animations are suppressed.

---

Questions the implementer will likely hit — flag back to design before guessing:

- Which of the three themes (`focused` / `indigo` / `arctic`) should actually be selectable by users vs. locked? (Currently locked to `focused`.)
- Does the header need a mobile hamburger, and what's its content model?
- What is the real source for pricing data — hardcoded, CMS, or Stripe product catalog?
- Does the contact form POST to an endpoint, or to a form service (Formspree, HubSpot, etc.)?
- Is dark-mode-only intentional, or should a light theme follow?
