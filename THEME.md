# TerraScale Theme Guide

This file documents the theme that is actually implemented in this repository.

It replaces the older aspirational version of the design system. If the code and this file ever disagree, treat the codebase as the source of truth and update this file.

## Scope

- App stack: Svelte 5 + TypeScript + Vite + Tailwind CSS 4
- Main theme source: `src/index.css`
- Main UI composition: `src/pages/*.svelte` and `src/components/*.svelte`
- Icon source and registry: `scripts/icons-manifest.mjs`, `scripts/icons-sync.mjs`, `src/lib/icons.ts`

## Theme Summary

TerraScale uses a dark, high-contrast product-marketing theme built from four core motifs:

- Near-black backgrounds with blue-led accent lighting
- Frosted glass surfaces with subtle borders and backdrop blur
- Neo-brutalist offset shadows for buttons, cards, and interactive controls
- Technical atmosphere created with grids, scanlines, radial glows, and code-panel styling

The brand tone is "developer infrastructure": polished, sharp-edged, slightly futuristic, and intentionally dark.

## Source Of Truth

### Files that define the theme

- `src/index.css`
  Defines root tokens, global element styles, utilities, motion helpers, buttons, inputs, badges, and card treatments.
- `index.html`
  Locks dark color scheme, preloads Quantico fonts, and sets the browser theme color to `#050508`.
- `public/logo-*.svg`, `public/icon-*.svg`
  Brand marks used throughout the site.
- `public/icons/**`
  Checked-in SVG icon assets.

### Files that demonstrate the theme in use

- `src/components/Header.svelte`
- `src/components/Footer.svelte`
- `src/components/ContactModal.svelte`
- `src/pages/Home.svelte`
- `src/pages/Pricing.svelte`
- `src/pages/Contact.svelte`
- `src/pages/About.svelte`
- `src/pages/NotFound.svelte`

## Reality Check

This repo still contains some stale metadata from an older React-based version in files like `README.md` and `components.json`. The implemented app is Svelte, uses a local SVG icon pipeline, and does not use React, Radix, ShadCN components, or runtime icon packages in the current UI.

`THEME.md` should reflect the implementation, not the stale metadata.

## Typography

### Primary font

Only `Quantico` is wired into the app theme.

```css
--ts-font-display: 'Quantico', system-ui, -apple-system, sans-serif;
--font-quantico: "Quantico", system-ui, -apple-system, sans-serif;
```

Applied globally via:

- `:root`
- `body`
- buttons, inputs, badges, code panels, and utility classes

### Font assets

- `public/fonts/quantico.woff2`
- `public/fonts/quantico-bold.woff2`

Preloaded in `index.html`.

### Important note

`public/fonts/jetbrains-mono*.woff2` exists in the repo, but the active theme does not use it. Even code-like UI in this project is currently styled with Quantico for brand consistency.

## Color System

### Core palette

| Token | Value | Role |
| --- | --- | --- |
| `--ts-color-primary` | `#2563EB` | Brand blue, primary CTA, focus, links |
| `--ts-color-secondary` | `#10B981` | Success, uptime, positive state |
| `--ts-color-accent` | `#0891B2` | Cyan accents, secondary highlight |
| `--ts-color-tertiary` | `#8B5CF6` | Premium or enterprise emphasis |

Supporting lighter and darker variants are defined for all four families in `src/index.css`.

### Semantic palette

| Token | Value |
| --- | --- |
| `--ts-color-info` | `#38BDF8` |
| `--ts-color-success` | `#10B981` |
| `--ts-color-warning` | `#FBBF24` |
| `--ts-color-error` | `#F43F5E` |

### Background palette

| Token | Value | Typical use |
| --- | --- | --- |
| `--ts-bg-base` | `#050508` | Main page background |
| `--ts-bg-surface` | `#0A0A0E` | Section and card surfaces |
| `--ts-bg-elevated` | `#0C0C10` | Hover and elevated panels |
| `--ts-bg-card` | `#080810` | Dense card or modal surface |

### Text palette

| Token | Value | Typical use |
| --- | --- | --- |
| `--ts-color-text` | `#F8FAFC` | Main copy |
| `--ts-color-text-muted` | `#94A3B8` | Secondary copy |
| `--ts-color-text-disabled` | `#475569` | Muted or disabled state |
| `--ts-color-divider` | `#1A1A24` | Borders and separators |

### Brand exceptions

The app mostly stays inside the theme palette, but a few intentional exceptions appear:

- Discord brand: `#5865F2`
- Language tabs on Home: TypeScript, Python, Go, Rust, and C# use language-specific colors
- 404 gradient mixes blue and green directly for a simpler fallback moment

These are valid exceptions when a brand or language identity matters.

## Spacing And Layout

### Spacing tokens

| Token | Value |
| --- | --- |
| `--ts-space-1` | `0.25rem` |
| `--ts-space-2` | `0.5rem` |
| `--ts-space-3` | `0.75rem` |
| `--ts-space-4` | `1rem` |
| `--ts-space-5` | `1.25rem` |
| `--ts-space-6` | `1.5rem` |
| `--ts-space-7` | `1.75rem` |
| `--ts-space-8` | `2rem` |
| `--ts-space-10` | `2.5rem` |
| `--ts-space-12` | `3rem` |
| `--ts-space-16` | `4rem` |
| `--ts-space-20` | `5rem` |

### Layout conventions

- Max content width: `1440px`
- Standard section gutters: `px-4 sm:px-6 lg:px-8`
- Section rhythm: heavy use of `py-24`
- Cards and major panels: usually `p-6` or `p-8`

### Header note

The theme token sets `--ts-header-height: 4rem`, but the current header renders at `h-[4.8rem]` in `src/components/Header.svelte`. If header height changes, keep both the token and the component aligned.

## Border Radius

The visual language is sharp, not fully brutalist and not soft-SaaS rounded.

| Token | Value |
| --- | --- |
| `--ts-radius-none` | `0px` |
| `--ts-radius-sm` | `4px` |
| `--ts-radius-md` | `6px` |
| `--ts-radius-lg` | `8px` |
| `--ts-radius-xl` | `12px` |
| `--ts-radius-2xl` | `16px` |
| `--ts-radius-pill` | `9999px` |

Preferred utilities:

- `ts-rounded-sm`
- `ts-rounded-md`
- `ts-rounded-lg`
- `ts-rounded-xl`
- `ts-rounded-2xl`

Legacy helpers like `br-sharp-4` still exist, but new work should use the `ts-rounded-*` utilities.

## Shadows, Glow, And Depth

### Brutalist offset shadows

| Token | Value |
| --- | --- |
| `--ts-shadow-brutal-sm` | `2px 2px 0 rgba(0, 0, 0, 0.5)` |
| `--ts-shadow-brutal` | `4px 4px 0 rgba(0, 0, 0, 0.6)` |
| `--ts-shadow-brutal-lg` | `6px 6px 0 rgba(0, 0, 0, 0.7)` |
| `--ts-shadow-brutal-xl` | `8px 8px 0 rgba(0, 0, 0, 0.75)` |

Utilities:

- `shadow-offset-sm`
- `shadow-offset`
- `shadow-offset-lg`
- `shadow-offset-blue`
- `shadow-offset-green`
- `shadow-offset-cyan`
- `shadow-offset-purple`

Use these on buttons, floating cards, toggles, and graphical callouts.

### Soft shadows

Soft shadows are used under gradient CTAs and glass surfaces to keep the UI polished.

- `ts-shadow-soft-sm`
- `ts-shadow-soft`
- `ts-shadow-soft-lg`

### Glow system

Glow is never the base visual treatment. It is an accent layer used for emphasis, focus, or hover energy.

- `ts-glow-primary-sm`
- `ts-glow-primary`
- `ts-glow-primary-lg`
- `ts-glow-secondary`
- `ts-glow-accent`
- `ts-glow-tertiary`

Legacy aliases still exist:

- `glow-blue`
- `glow-green`
- `glow-cyan`

## Glassmorphism

Glass is a foundational surface treatment in this project.

### Blur tokens

| Token | Value |
| --- | --- |
| `--ts-blur-sm` | `4px` |
| `--ts-blur-md` | `8px` |
| `--ts-blur-lg` | `12px` |
| `--ts-blur-xl` | `16px` |
| `--ts-blur-2xl` | `24px` |

### Glass backgrounds

| Token | Value |
| --- | --- |
| `--ts-glass-bg-subtle` | `rgba(255, 255, 255, 0.02)` |
| `--ts-glass-bg-light` | `rgba(255, 255, 255, 0.04)` |
| `--ts-glass-bg` | `rgba(255, 255, 255, 0.06)` |
| `--ts-glass-bg-strong` | `rgba(255, 255, 255, 0.08)` |
| `--ts-glass-bg-heavy` | `rgba(10, 10, 14, 0.7)` |

### Glass borders

| Token | Value |
| --- | --- |
| `--ts-glass-border-subtle` | `1px solid rgba(255, 255, 255, 0.06)` |
| `--ts-glass-border` | `1px solid rgba(255, 255, 255, 0.10)` |
| `--ts-glass-border-strong` | `1px solid rgba(255, 255, 255, 0.15)` |
| `--ts-glass-border-bright` | `1px solid rgba(255, 255, 255, 0.20)` |

### Utilities

- `ts-glass`
  Standard card and panel surface.
- `ts-glass-frosted`
  Stronger blur and stronger border, used on modals and higher-emphasis panels.
- `ts-glass-heavy`
  Dense glass, suitable for overlays.
- `ts-glass-primary`
- `ts-glass-accent`
- `ts-glass-tertiary`
  Tinted glass variants for highlighted callouts.

## Gradients And Atmosphere

### Theme gradients

- `--ts-gradient-primary`
- `--ts-gradient-primary-hover`
- `--ts-gradient-secondary`
- `--ts-gradient-accent`
- `--ts-gradient-tertiary`
- `--ts-gradient-rainbow`
- `--ts-gradient-cosmic`

### Background atmosphere

The site relies on layered environmental effects rather than flat fills:

- `bg-grid-pattern` for technical background structure
- `scanlines` for subtle terminal-like texture
- radial gradients in hero sections for ambient light
- blurred geometric floats and circles for depth

Home uses the full atmospheric stack most aggressively. Other pages simplify it.

### Text gradients

- `ts-text-gradient`
- `ts-text-gradient-accent`
- `ts-text-gradient-rainbow`
- `ts-text-gradient-animated`

Use gradient text for hero metrics, premium pricing emphasis, and short highlighted phrases. Avoid using it for long paragraphs.

## Motion

Motion is present across the site, but intentionally shallow and readable.

### Transition tokens

| Token | Value |
| --- | --- |
| `--ts-transition-fast` | `100ms ease-out` |
| `--ts-transition-normal` | `150ms ease-out` |
| `--ts-transition-slow` | `250ms ease-out` |
| `--ts-transition-spring` | `300ms cubic-bezier(0.175, 0.885, 0.32, 1.275)` |

### Scroll reveal

Implemented via:

- `src/lib/motion/reveal.ts`
- `src/components/ScrollReveal.svelte`

Directions:

- `up`
- `down`
- `left`
- `right`
- `none`

CSS classes:

- `ts-reveal`
- `ts-reveal-up`
- `ts-reveal-down`
- `ts-reveal-left`
- `ts-reveal-right`
- `ts-reveal-none`

### Motion utilities

- `hover-lift`
- `hover-lift-active`
- `ts-hover-glow`
- `ts-pressable`
- `animate-float-slow`
- `animate-float-reverse`
- `animate-bob`
- `animate-pulse-soft`
- `animate-slide-sideways`
- `neon-border-pulse`

### Reduced motion

The stylesheet includes a proper `prefers-reduced-motion: reduce` block. Any new animation should degrade cleanly inside that existing rule.

## Component Patterns

### Buttons

#### `ts-btn-primary`

Use for the main CTA on a section or page.

Traits:

- blue gradient fill
- uppercase Quantico
- small brutalist or soft-shadow pairing
- hover lift, stronger shadow

Often paired with:

- `shadow-offset-blue`
- `ts-pressable`

#### `ts-btn-glass`

Use for secondary actions next to a primary CTA.

Traits:

- translucent glass background
- white text by default
- blue-tinted hover state

### Inputs

#### `ts-input`

Used in `Contact.svelte` and `ContactModal.svelte`.

Traits:

- subtle glass background
- muted border
- blue focus ring and glow
- medium radius

### Badges

#### `ts-badge`

Variants:

- `ts-badge-primary`
- `ts-badge-secondary`
- `ts-badge-accent`
- `ts-badge-tertiary`

Used for uptime, SOC 2, trial tags, and section callouts.

### Stats cards

#### `ts-stats-card`

Used on Home and About.

Traits:

- glass panel
- top accent border
- soft hover lift
- soft radial glow on hover
- gradient number + terminal-style label

### Code panels

The Home page establishes a code-panel motif with:

- `terminal-badge`
- `terminal-code`
- `ts-code`
- `ts-code-line`
- `ts-code-token-*`

Even code-like content stays on-brand by using Quantico instead of a dedicated mono face.

## Page-Level Visual Patterns

### Header

- sticky
- translucent dark background
- grid texture
- blurred backdrop
- active nav item uses blue tint and border
- primary external action uses `ts-btn-primary`

### Footer

- denser surface tone than the page body
- muted copy with bright icon accents
- boxed social links with hover lift
- highlight badges for uptime and compliance

### Home

This is the most complete expression of the theme:

- radial atmospheric lighting
- grid + scanlines
- floating geometric decorations
- glass CTA rail
- stats cards
- technical code showcase

### Pricing

- same dark base as Home, but flatter and more transactional
- strong use of accent and tertiary colors for plan distinction
- frosted toggle and pricing table container
- FAQ uses glass accordion cards

### Contact

- split layout with a glass form and a quieter information rail
- success and error feedback use semantic green/red surfaces
- Discord CTA is allowed to use brand purple

### About

- tinted mission callout using `ts-glass-primary`
- icon-led value cards
- stats section reuses the Home metrics pattern

### 404

- simplified fallback page
- uses the same button system and reveal motion
- keeps the dark background but swaps in a direct blue-to-green headline gradient

## Icons

This repo does not use runtime icon packages in the app.

### Required workflow

1. Add or update icon definitions in `scripts/icons-manifest.mjs`
2. Run `npm run icons:sync`
3. Use the generated names from `src/lib/icons.ts`
4. Render icons through `src/components/Icon.svelte`

### Rules

- Store SVGs in `public/icons/`
- Prefer `lucide` for generic UI icons
- Prefer `simple-icons` for brands and socials
- Keep icons monochrome and `currentColor`-driven
- Do not import icon npm packages directly into Svelte components

## Logos And Brand Assets

Available brand marks:

- `public/logo-principal.svg`
- `public/logo-white.svg`
- `public/logo-blue.svg`
- `public/logo-black.svg`
- `public/icon-white.svg`
- `public/icon-blue.svg`
- `public/icon-black.svg`

Current usage:

- Header: `logo-principal.svg`
- Footer: `logo-white.svg`
- Favicon: `icon-blue.svg`

## Contributor Rules

When adding or updating UI, stay inside these boundaries:

- Use Quantico, not a new display or mono font
- Start from existing tokens and utilities in `src/index.css`
- Prefer glass + offset-shadow combinations over flat cards
- Keep backgrounds dark and high-contrast
- Use blue as the default lead accent
- Use green for positive state, cyan for secondary highlight, violet for premium emphasis
- Reuse `ScrollReveal` for entrance motion instead of inventing ad hoc patterns
- Keep brand and language color exceptions rare and intentional
- Render icons through the local registry pipeline only

## Preferred Implementation Order

When building new UI in this repo, use this order:

1. Existing utility or component pattern
2. Existing token in `:root`
3. New utility in `src/index.css`
4. New token only if the value is clearly part of the shared system

Do not hardcode new one-off colors or effects if an existing token already fits.

## Maintenance Notes

- If `src/index.css` changes, update this file.
- If the visual language changes materially, update the page-pattern sections, not just the token tables.
- If the stack changes again, correct `README.md` and `components.json` too so the repo stops contradicting itself.
