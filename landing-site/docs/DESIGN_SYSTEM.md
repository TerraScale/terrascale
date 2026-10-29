# TerraScale Design System

A complete reference for applying the TerraScale landing-page look to another
site. This is written so an AI agent or designer can read it once and produce
consistent output on a separate codebase. Copy the token block wholesale; copy
component patterns as needed.

---

## 0. Design principles

1. **Dark, technical, confident.** Single theme only - there is no light mode
   toggle. Assume `color-scheme: dark` at the document root.
2. **One atmosphere.** The body has a single fixed ambient radial glow drawn
   from `body::before`. Sections never paint their own background decoratives
   (grids, orbs, scanlines) - that was tried and reverted. Keep per-section
   chrome clean so the unified glow reads as one continuous space.
3. **Brand blue carries hierarchy.** Accents, active states, gradient text,
   and "most important" affordances all use `--ts-primary` (#2563EB) or its
   light companion `--ts-primary-light` (#60A5FA). Secondary colours exist
   (green, purple) but are used sparingly - status pulse, pricing accents,
   decorative dots.
4. **Glass over solid.** Elevated surfaces are `.ts-glass` - translucent
   background, low-alpha border, `backdrop-filter: blur(14px)`. They sit on
   the body glow rather than on opaque cards.
5. **Monospaced numbers, monospaced data.** Prices, stats, filenames, code -
   all `--ts-font-mono`. Adds a technical feel and keeps digits aligned
   (`font-variant-numeric: tabular-nums` on animated counters).
6. **Motion is subtle and shared.** Most transitions are 150–260 ms with
   cubic-bezier easings. Ambient loops (breathing, floating, pulsing) run at
   2.2–16 s so they never compete with the user. Everything collapses to
   instant under `prefers-reduced-motion: reduce`.
7. **Grounded copy.** No "SOC 2 Type II", "99.99% uptime", "sub-20 ms", "20+
   regions" unless the claim is actually verified. Describe what the product
   does, not superlatives.

---

## 1. Document shell

```html
<html lang="en" class="dark">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta name="color-scheme" content="dark" />
    <meta name="theme-color" content="#050508" />
    <meta name="darkreader-lock" /> <!-- opt out of Dark Reader re-theming -->

    <link rel="icon" type="image/svg+xml" href="/icon-blue.svg" />
    <link rel="preload" as="font" type="font/woff2" crossorigin
          href="/fonts/quantico.woff2" />
    <link rel="preload" as="font" type="font/woff2" crossorigin
          href="/fonts/quantico-bold.woff2" />
    <link rel="preconnect" href="https://fonts.googleapis.com" />
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
    <link rel="stylesheet"
          href="https://fonts.googleapis.com/css2?family=Roboto:wght@300;400;500;700&family=Roboto+Mono:wght@400;500;700&display=swap" />
  </head>
  <body>
    <!-- content -->
  </body>
</html>
```

- Only one theme ships. Every token is declared both on `:root` and
  `[data-theme="focused"]`, so no `data-theme` attribute is required.
- Always preload Quantico woff2 to avoid FOUT on the headline.

---

## 2. Brand assets

All brand marks live at the site root.

| File | Description | Usage |
|---|---|---|
| `/logo-principal.svg` | Wordmark, blue icon + white text | Hero, header |
| `/logo-white.svg` | Wordmark, all white | Footer, dark-on-dark contexts |
| `/logo-blue.svg` | Wordmark, all blue | High-contrast brand moments |
| `/logo-black.svg` | Wordmark, all black | Light-bg exports (print) |
| `/icon-blue.svg` | Square icon mark, blue | Favicon, small brand chips |
| `/icon-white.svg` | Square icon mark, white | App icons on dark bg |
| `/icon-black.svg` | Square icon mark, black | Light-bg exports |

Header usage: logo-principal at `height: 38px`.
Footer usage: logo-white at `height: 26px`.
Hero usage: logo-principal at `height: clamp(48px, 6vw, 78px)` with a
breathing radial glow behind it (see §11).

---

## 3. Tokens (CSS custom properties)

Drop this block unchanged at the top of your global stylesheet. It is the
single source of truth - every component reaches for these variables, never
hard-coded colours.

```css
:root,
[data-theme="focused"] {
  /* ── Backgrounds ──────────────────────────────────── */
  --ts-bg-base: #050508;
  --ts-bg-base-raw: 5, 5, 8;       /* rgb triplet for rgba() */
  --ts-bg-surface: #09090F;        /* slightly lighter, for footer etc */
  --ts-bg-card: #0B0B14;           /* opaque card background (rarely used) */
  --ts-code-bg: #07070D;           /* terminal / codeblock background */
  --ts-divider: #15151F;           /* hairlines between sections */

  /* ── Brand & semantic ─────────────────────────────── */
  --ts-primary: #2563EB;
  --ts-primary-light: #60A5FA;
  --ts-primary-rgb: 37, 99, 235;

  --ts-secondary: #10B981;         /* success / online green */
  --ts-secondary-rgb: 16, 185, 129;

  --ts-tertiary: #8B5CF6;          /* accent purple */
  --ts-tertiary-rgb: 139, 92, 246;

  /* ── Text ─────────────────────────────────────────── */
  --ts-text: #F1F5F9;
  --ts-text-muted: #9BACCB;

  /* ── Glass (subtle modifier) ──────────────────────── */
  --ts-glass-bg: rgba(37, 99, 235, 0.025);
  --ts-glass-border: rgba(148, 163, 184, 0.16);
  --ts-grid-color: rgba(37, 99, 235, 0.055);

  /* ── Hero / ambient glow ──────────────────────────── */
  --ts-hero-glow-top:    rgba(37, 99, 235, 0.22);
  --ts-hero-glow-mid:    rgba(37, 99, 235, 0.10);
  --ts-hero-glow-accent: rgba(16, 185, 129, 0.05);

  /* ── Gradients ────────────────────────────────────── */
  --ts-gradient-primary: linear-gradient(135deg, #3B82F6 0%, #2563EB 100%);

  /* ── 404 page ─────────────────────────────────────── */
  --ts-notfound-bg: #0d1729;
  --ts-notfound-blue: #0861ed;
  --ts-notfound-blue-bright: #3794ff;
  --ts-notfound-grid: rgba(43, 106, 211, 0.22);

  /* ── Atmosphere (locked to "dramatic") ────────────── */
  --ts-atmo-mult: 1.9;
  --ts-atmo-grid-opacity: 1;
  --ts-atmo-float-opacity: 1.25;

  /* ── Fonts ────────────────────────────────────────── */
  --ts-font-body:   'Quantico', system-ui, -apple-system, sans-serif;
  --ts-font-accent: 'Roboto', system-ui, sans-serif;
  --ts-font-mono:   'Roboto Mono', 'JetBrains Mono', ui-monospace, 'SF Mono', monospace;

  /* ── Spacing (8-pt grid) ──────────────────────────── */
  --ts-space-1:  0.25rem;
  --ts-space-2:  0.5rem;
  --ts-space-3:  0.75rem;
  --ts-space-4:  1rem;
  --ts-space-5:  1.25rem;
  --ts-space-6:  1.5rem;
  --ts-space-8:  2rem;
  --ts-space-10: 2.5rem;
  --ts-space-12: 3rem;
  --ts-space-16: 4rem;
  --ts-space-20: 5rem;
  --ts-space-24: 6rem;

  /* ── Radius ───────────────────────────────────────── */
  --ts-radius-sm:   6px;
  --ts-radius-md:   10px;
  --ts-radius-lg:   16px;
  --ts-radius-xl:   18px;
  --ts-radius-pill: 9999px;

  /* ── Shadows ──────────────────────────────────────── */
  --ts-shadow-card-hover:    0 12px 40px -12px rgba(37, 99, 235, 0.35);
  --ts-shadow-floating-cta:  0 20px 60px -20px rgba(37, 99, 235, 0.50);
  --ts-shadow-popular:       0 0 0 1px var(--ts-primary),
                             0 24px 60px -24px rgba(37, 99, 235, 0.45);

  /* ── Transitions ──────────────────────────────────── */
  --ts-transition-fast:   120ms ease-out;
  --ts-transition-normal: 180ms ease-out;
  --ts-transition-slow:   260ms cubic-bezier(.2, .8, .2, 1);

  color-scheme: dark;
  font-family: var(--ts-font-body);
  background-color: var(--ts-bg-base);
  color: var(--ts-text);
}
```

### Using RGB triplets

Every brand colour has a companion `--*-rgb` triplet so you can compose alpha:

```css
background: rgba(var(--ts-primary-rgb), 0.08);
border: 1px solid rgba(var(--ts-primary-rgb), 0.22);
```

Follow this pattern religiously - do not introduce new hardcoded RGBA values.

---

## 4. Colour usage cheat sheet

| Role | Token | Typical alpha |
|---|---|---|
| Page background | `--ts-bg-base` | 1 |
| Elevated surface (footer) | `--ts-bg-surface` | 1 |
| Card/panel | `.ts-glass` (≈0.025 blue) | - |
| Primary CTA bg | `var(--ts-gradient-primary)` | - |
| Primary CTA shadow | `rgba(var(--ts-primary-rgb), 0.35–0.50)` | 0.35–0.5 |
| Brand accent chip bg | `rgba(var(--ts-primary-rgb), 0.08–0.10)` | 0.08–0.1 |
| Brand accent chip border | `rgba(var(--ts-primary-rgb), 0.22–0.28)` | 0.22–0.28 |
| Active nav/tab | `rgba(var(--ts-primary-rgb), 0.10)` bg + `0.25` border | - |
| Hairline | `var(--ts-divider)` or `var(--ts-glass-border)` | - |
| Body text | `var(--ts-text)` | 1 |
| Muted text | `var(--ts-text-muted)` | 1 |
| Status online/ok | `var(--ts-secondary)` with `0 0 10px` glow | - |
| Success highlight | `rgba(var(--ts-secondary-rgb), 0.5)` | 0.5 |
| Accent / decorative | `var(--ts-tertiary)` at ≤0.4 alpha | - |

### Gradient text

```css
.ts-text-gradient {
  background: var(--ts-gradient-primary);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}
```

Use on 1–3 words inside a headline to create emphasis. Don't gradient entire
lines.

---

## 5. Typography

### Fonts

- **Body - Quantico** (self-hosted woff2, 400 + 700 weights). Geometric, angled,
  reads as "confident / engineered". Every paragraph and headline uses this
  unless noted.
- **Accent - Roboto** (Google Fonts, 300/400/500/700). Used for buttons,
  navigation links, eyebrows, footer text, badges, subtitles, pill labels,
  form labels - anywhere the
  tone needs to be "calm UI" rather than "engineered".
- **Mono - Roboto Mono** with JetBrains Mono as fallback. Used for prices,
  stats, code, filenames, and anything numeric that should stay tabular.

Token names map as: `--ts-font-body`, `--ts-font-accent`, `--ts-font-mono`.

### Type scale (observed in landing page, use these as targets)

| Role | Size | Weight | Font |
|---|---|---|---|
| Hero headline | `clamp(2.4rem, 4.2vw + 1rem, 5rem)` | 700 | body |
| Section title (H2) | `clamp(1.8rem, 2.5vw + 0.5rem, 3rem)` | 700 | body |
| CTA headline | `clamp(2rem, 3vw + 0.5rem, 3.2rem)` | 700 | body |
| Card title (H3) | 19px | 700 | body |
| Inline subtitle | `clamp(1rem, 0.7vw + 0.75rem, 1.15rem)` | 400 | accent |
| Body copy in card | 14.5px | 400 | body |
| Muted paragraph | 15–18px | 400 | accent |
| Eyebrow / kicker | 11px | 700 | accent, UPPERCASE, 0.14em ls |
| Pill / chip label | 11–13px | 600–700 | accent |
| Monospace digit/filename | 11–22px | 400–700 | mono, tabular-nums |
| Pricing $ value | `clamp(0.8125rem, 2.4vw + 0.25rem, 2.125rem)` | 700 | mono |
| Pricing animated counter | `clamp(0.8125rem, 2.4vw + 0.25rem, 2.125rem)` | 700 | mono, tabular-nums |

### Letter-spacing rhythm

- Headlines: `letter-spacing: -0.035em` down to `-0.02em`. Tighten as size
  grows.
- UPPERCASE labels (badges, eyebrows, status, button text): `0.08em` for
  button labels, `0.10em`–`0.14em` for eyebrows and chips.
- Lowercase body copy: `0` (default).

### Line-height rhythm

- Headlines: `1.02`–`1.14` (tight).
- Paragraphs: `1.55`–`1.7`.
- Buttons and pills: `1` implicit (single line).

---

## 6. Spacing

Use the `--ts-space-*` scale for component internals. For section rhythm use
these targets:

| Context | Desktop `padding` | Tablet/mobile |
|---|---|---|
| Content section (features, code, pricing) | `80–88px 0` | `56–64px 0` |
| Hero | `min-height: clamp(580px, calc(78vh - 72px), 760px)` | `clamp(520px, 92vh, 680px)` |
| Final CTA | `84px 0 112px` | `48px 0 72px` |
| Footer | `36px 0 20px` | same |
| Container inner padding | `0 40px` | `0 20px` |
| Max content width | `1240px` (or `1120px`, `960px`, `880px`, `720px` depending on density) | same |
| Section-header max-width | `720px` | same |

Grid gaps default to `16–20px`; card internal padding default `20–32px`
depending on density.

---

## 7. Radius

| Scale | Use |
|---|---|
| `4px` | Eyebrows, small chips, code-block bullets |
| `6px` (`--ts-radius-sm`) | Buttons, small cards, input fields |
| `7–8px` | Social icon squares, FAQ item |
| `10–12px` (`--ts-radius-md`) | Cards, terminal, CTA glass panel |
| `16–18px` (`--ts-radius-lg`/`xl`) | Large billboard CTAs |
| `9999px` (`--ts-radius-pill`) | Highlight pills, billing toggle, status chip |

Never mix two different "medium" radii inside one component - pick `10` or
`12` and stick with it.

---

## 8. Shadows & elevation

The page is mostly flat. Shadows are reserved for (a) primary CTAs, (b)
hover lift on cards, and (c) brand-coloured glow behind prominent elements.

```css
/* Primary CTA resting shadow */
box-shadow: 0 6px 24px rgba(var(--ts-primary-rgb), 0.40);

/* Primary CTA hover shadow */
box-shadow: 0 10px 32px rgba(var(--ts-primary-rgb), 0.50);

/* Card hover (feature / pricing) */
box-shadow: 0 10px 28px rgba(var(--ts-primary-rgb), 0.18);

/* Popular pricing card (blue ring halo) */
box-shadow: 0 8px 30px rgba(var(--ts-primary-rgb), 0.12);

/* Terminal / code panel */
box-shadow: 0 20px 60px rgba(0, 0, 0, 0.5),
            0 0 80px rgba(var(--ts-primary-rgb), 0.08);
```

A subtle inner light is sometimes added on elevated glass:
`inset 0 1px 0 rgba(255, 255, 255, 0.04)`.

---

## 9. Motion

### Durations & easings

| Purpose | Duration | Easing |
|---|---|---|
| Button / link hover colour & bg | 150–180 ms | `ease` |
| Button lift | 180 ms | `cubic-bezier(.2, .8, .2, 1)` |
| Card hover (lift + shadow) | 250–260 ms | `ease` |
| FAQ expand / chevron rotate | **220 ms** | `cubic-bezier(.22, 1, .36, 1)` (must match between arrow and panel) |
| Toggle thumb slide | 240 ms | `cubic-bezier(.4, 0, .2, 1)` |
| Reveal-on-scroll | 380 ms (`reveal` action default; CSS fallback 720 ms) | `cubic-bezier(.22, 1, .36, 1)` |
| Price tween (monthly↔annual) | 650 ms | `cubicOut` (Svelte `svelte/easing`) |
| CTA shine sweep | 900 ms | `cubic-bezier(.4, 0, .2, 1)` on hover |

### Shared keyframes (copy these into your global stylesheet)

```css
@keyframes ts-breathe   { 0%,100% { transform: scale(1);    opacity: .82 }
                          50%     { transform: scale(1.04); opacity: 1   } }

@keyframes ts-pulse-soft{ 0%,100% { opacity: 1 }
                          50%     { opacity: .45 } }

@keyframes ts-float-up  { 0%,100% { transform: translateY(0)     rotate(0deg) }
                          50%     { transform: translateY(-20px) rotate(1deg) } }

@keyframes ts-float-down{ 0%,100% { transform: translateY(0)    rotate(0deg) }
                          50%     { transform: translateY(18px) rotate(-1deg) } }

@keyframes ts-shine-sweep { 0%   { transform: translateX(-120%) skewX(-18deg) }
                            100% { transform: translateX(260%)  skewX(-18deg) } }
```

Ambient loops live between **2.2 s** (dot pulse) and **16 s** (float). The
`.breathe` class runs at 5.5 s - use it for subtle glows behind logos/hero
elements.

### `prefers-reduced-motion` policy

Every moving element must be disabled (not just slowed) under reduced motion.
Include this block globally:

```css
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 0.01ms !important;
    scroll-behavior: auto !important;
  }
  .ts-reveal, .ts-reveal.is-visible {
    opacity: 1; transform: none; filter: none; transition: none;
  }
  .breathe, .pulse-dot, .float-up, .float-down,
  .ts-tile-hover:hover, .ts-btn-primary:hover {
    animation: none !important;
    transform: none !important;
  }
  .cta-shine::before { display: none; }
}
```

### Scroll reveal

Elements fade + slide in (4 directions) as they enter the viewport. Apply via
the `reveal` Svelte action or by manually toggling classes:

- Element starts with class `ts-reveal` + direction (`ts-reveal-up/down/left/right/none`).
- When visible, add class `is-visible`.
- Direction offsets are 22px by default. The `reveal` action sets
  `--ts-reveal-duration` to 380ms; the CSS fallback is 720ms.

```css
.ts-reveal {
  opacity: 0;
  filter: blur(4px);
  transition:
    opacity var(--ts-reveal-duration, 720ms) cubic-bezier(.22, 1, .36, 1),
    transform var(--ts-reveal-duration, 720ms) cubic-bezier(.22, 1, .36, 1),
    filter var(--ts-reveal-duration, 720ms) cubic-bezier(.22, 1, .36, 1);
  transition-delay: var(--ts-reveal-delay, 0s);
  will-change: opacity, transform, filter;
}
.ts-reveal-up    { transform: translate3d(0,  22px, 0); }
.ts-reveal-down  { transform: translate3d(0, -22px, 0); }
.ts-reveal-left  { transform: translate3d( 22px, 0, 0); }
.ts-reveal-right { transform: translate3d(-22px, 0, 0); }
.ts-reveal-none  { transform: none; }
.ts-reveal.is-visible { opacity: 1; transform: translate3d(0,0,0); filter: none; }
```

Fire the `is-visible` class via `IntersectionObserver` with
`rootMargin: '0px 0px 120px 0px'` so elements reveal slightly before they
actually hit the fold.

---

## 10. Background & atmosphere

The entire site sits on one fixed radial-gradient backdrop. Do not add
per-section background colours, grids, orbs, or scanlines - they were tried
and removed because they fragmented the page.

```css
body {
  min-height: 100vh;
  background-color: var(--ts-bg-base);
  overflow-x: hidden;
}

body::before {
  content: '';
  position: fixed;
  inset: 0;
  z-index: -1;
  pointer-events: none;
  opacity: var(--ts-atmo-mult, 1);  /* 1.9 in the "dramatic" preset */
  background:
    radial-gradient(ellipse 80% 60% at 50% -10%, var(--ts-hero-glow-top)    0%, transparent 65%),
    radial-gradient(ellipse 55% 45% at 15% 25%, var(--ts-hero-glow-mid)    0%, transparent 55%),
    radial-gradient(ellipse 55% 45% at 85% 55%, var(--ts-hero-glow-mid)    0%, transparent 55%),
    radial-gradient(ellipse 45% 40% at 50% 90%, var(--ts-hero-glow-accent) 0%, transparent 55%);
}
```

If a section truly needs visual weight (e.g. a hero with a world map),
confine the effect to a child element with `overflow: hidden` and feather it
into the body via a `mask-image: radial-gradient(...)` so it dissolves at the
edges instead of creating a rectangle.

The one exception is the 404 page (`src/pages/NotFound.svelte`), which paints
its own backdrop from the `--ts-notfound-*` tokens plus a perspective grid
floor.

---

## 11. Core components

### 11.1 Header (sticky)

- `position: sticky; top: 0;` with `z-index: 100`.
- Background transitions from `rgba(var(--ts-bg-base-raw), 0.75)` to `0.94`
  once the user scrolls > 24 px (add/remove a `.scrolled` class on scroll).
- `backdrop-filter: blur(20px)`.
- Flex row: brand left, nav centred (`margin: 0 auto`), actions right. At
  `max-width: 1240px` the nav and actions hide and a hamburger button
  reveals a mobile menu.
- Height: `72px`.

```css
.ts-header {
  position: sticky; top: 0; z-index: 100;
  border-bottom: 1px solid var(--ts-divider);
  background: rgba(var(--ts-bg-base-raw), 0.75);
  backdrop-filter: blur(20px);
  transition: background 250ms ease;
}
.ts-header.scrolled { background: rgba(var(--ts-bg-base-raw), 0.94); }
```

Nav links: `font-size: 15px`, `font-weight: 600`, muted colour → full white
on hover, active state = brand pill (10% blue bg + 25% blue border).

### 11.2 Footer

- `background: var(--ts-bg-surface)` + top hairline (`border-top: 1px solid
  var(--ts-divider)`).
- Grid: `1.6fr repeat(4, 1fr)` - brand column wider than the 4 link columns.
- Brand column: logo-white (26px tall) + muted tagline + social row.
- Column headings: 11px, 700, uppercase, 0.14em letter-spacing.
- Each link has a chevron glyph that translates +2px on hover.
- Meta row at the bottom is divided by another hairline; left = copyright,
  right = inline language selector.

### 11.3 Buttons

Three variants. All use `--ts-font-accent`, `font-size: 13px`, `font-weight:
700`, `UPPERCASE`, `letter-spacing: 0.08em`.

```css
.ts-btn {
  display: inline-flex; align-items: center; justify-content: center;
  gap: 8px; padding: 15px 32px; border-radius: 6px;
  border: 1px solid transparent;
  cursor: pointer; text-decoration: none;
  transition: transform 180ms cubic-bezier(.2,.8,.2,1),
              box-shadow 180ms ease,
              background 180ms ease,
              border-color 180ms ease;
}

.ts-btn-primary {
  background: var(--ts-gradient-primary);
  color: #fff;
  box-shadow: 0 6px 24px rgba(var(--ts-primary-rgb), 0.4);
}
.ts-btn-primary:hover  { transform: translateY(-2px);
                          box-shadow: 0 10px 32px rgba(var(--ts-primary-rgb), 0.5); }
.ts-btn-primary:active { transform: translateY(0); }

.ts-btn-glass {
  background: rgba(255,255,255,0.09);
  border-color: var(--ts-glass-border);
  color: var(--ts-text);
  backdrop-filter: blur(8px);
}
.ts-btn-glass:hover {
  border-color: rgba(var(--ts-primary-rgb), 0.45);
  background: rgba(var(--ts-primary-rgb), 0.14);
}

.ts-btn-compact { padding: 11px 22px; font-size: 12px; }
```

**CTA shine** - primary CTAs often get a white-gradient sheen that sweeps
across on hover:

```css
.cta-shine { position: relative; overflow: hidden; isolation: isolate; }
.cta-shine::before {
  content: ''; position: absolute; inset: 0 auto 0 0;
  width: 50%; height: 100%;
  background: linear-gradient(90deg, transparent 0%,
              rgba(255,255,255,0.18) 50%, transparent 100%);
  transform: translateX(-120%) skewX(-18deg);
  pointer-events: none; z-index: 1;
}
.cta-shine:hover::before,
.cta-shine:focus-visible::before {
  animation: ts-shine-sweep 900ms cubic-bezier(.4, 0, .2, 1);
}
```

### 11.4 Inputs

```css
.ts-input {
  width: 100%; padding: 11px 14px; border-radius: 6px;
  background: rgba(0,0,0,0.25);
  border: 1px solid var(--ts-glass-border);
  color: var(--ts-text);
  font-family: var(--ts-font-body); font-size: 14px;
  outline: none;
  transition: border-color 150ms, box-shadow 150ms;
}
.ts-input::placeholder { color: rgba(155,172,203,0.5); }
.ts-input:hover  { border-color: rgba(148,163,184,0.28); }
.ts-input:focus-visible {
  border-color: rgba(var(--ts-primary-rgb), 0.5);
  box-shadow: 0 0 0 3px rgba(var(--ts-primary-rgb), 0.12);
}
.ts-input--textarea { min-height: 120px; resize: vertical; }
.ts-input-label {
  display: block;
  font-family: var(--ts-font-accent);
  font-size: 10px; font-weight: 700;
  color: var(--ts-text-muted);
  text-transform: uppercase; letter-spacing: 0.14em;
  margin-bottom: 6px;
}
```

### 11.5 Eyebrows (kickers)

Small pill above a section title. Two variants - with leading dot (default),
or plain.

```css
.ts-eyebrow {
  display: inline-flex; align-items: center; gap: 8px;
  padding: 5px 12px; border-radius: 4px;
  background: rgba(var(--ts-primary-rgb), 0.10);
  border: 1px solid rgba(var(--ts-primary-rgb), 0.25);
  color: var(--ts-primary-light);
  font-family: var(--ts-font-accent);
  font-size: 11px; font-weight: 700;
  text-transform: uppercase; letter-spacing: 0.14em;
  line-height: 1;
}
.ts-eyebrow::before {
  content: ''; display: inline-block;
  width: 5px; height: 5px; border-radius: 3px;
  background: var(--ts-primary);
}
.ts-eyebrow--plain::before { display: none; }
```

### 11.6 Section header

Used at the top of every feature / pricing / content section. Centred,
max-width 720 px.

Structure:

```
[eyebrow pill]
# H2 Title (gradient-text on 1–3 words optional)
muted subtitle (1 line ideal, max 2)
```

Size: H2 `clamp(1.8rem, 2.5vw + 0.5rem, 3rem)`, subtitle 18px Roboto muted.
Margin-bottom under the section header ≈ `48–64 px` before content.

### 11.7 Status pill / animated dot

A breathing dot that signals "live / online / streaming":

```css
.pulse-dot {
  display: inline-block; border-radius: 999px;
  animation: ts-pulse-soft 2.2s ease-in-out infinite;
}
```

Typically `9 px`, backgrounded with `--ts-secondary` and a matching
`box-shadow: 0 0 10px var(--ts-secondary)` for bloom.

Used in: header status link.

### 11.8 Glass surface

Apply `.ts-glass` as a utility class to any container that should read as an
elevated panel:

```css
.ts-glass {
  background: var(--ts-glass-bg);
  backdrop-filter: blur(14px);
  -webkit-backdrop-filter: blur(14px);
  border: 1px solid var(--ts-glass-border);
}
```

Glass is **always** combined with a `border-radius`; plain glass rectangles
never appear.

### 11.9 Card patterns

All cards on the site share a repeatable shape:

- Glass surface (`.ts-glass`)
- Border-radius `10–12 px`
- Internal padding `20–32 px` depending on density
- Hover lift of `translateY(-2 to -4 px)` + a coloured shadow matching the
  card's accent
- A coloured icon tile in the top-left (square, `38–42 px`, border-radius
  `6–8 px`, 14% alpha bg + 30% alpha border, `color: var(--ts-primary-light)`)
- Title H3, 17–22 px, 700
- Muted body 13–15 px, line-height 1.55–1.65
- Optional pill bullets at bottom - monospace, 10–11 px, 8 px padding, 4 px
  radius, same 8% / 18% alpha treatment

**Do not** add radial glow blobs inside cards (they were removed) - keep the
card surface clean.

Card variants observed:

- **Feature card** (2×2 grid): 28 px padding, single accent colour
  (brand blue only).
- **Pricing card** (3 up): `clamp(0.5rem, 2vw, 1.5rem)` padding, per-plan
  `--plan-accent` / `--plan-accent-rgb` custom props so each tier can tint
  its icon, CTA, and hover shadow.
- **Triad card** (3 up, compact): 22 px padding, horizontal layout with
  icon + copy inline.
- **FAQ item**: full-width collapsing panel - see §11.12.
- **Contact channel**: horizontal pill with icon tile + label/value + trailing
  chevron; slides 3 px right on hover and highlights border.

### 11.10 Popular / "most popular" treatment

- Border widens to `1.5px` and uses plan accent at 45% alpha
- Gradient surface: `linear-gradient(180deg, rgba(primary, .06) 0%,
  var(--ts-glass-bg) 50%)`
- Ambient shadow: `0 8px 30px rgba(plan-accent, .12)`
- A top ribbon/badge: pills centred, `transform: translateX(-50%)` at
  `top: -13px`, filled with plan accent + 0.4 alpha glow shadow, 10 px text
  800 weight uppercase

### 11.11 Toggle (billing cycle)

A two-option segmented control with a sliding thumb:

- Outer pill `.ts-glass` + `border-radius: 999px` + 5 px padding
- Thumb: `position: absolute; top/bottom/left: 5px; width: calc(50% - 5px)`,
  filled with `--ts-gradient-primary`, 999 radius, ~14 px blue shadow
- Slides `translateX(100%)` when the second option is active
- Buttons sit on top, `z-index: 1`, padding `11 30 px`, uppercase `0.1em` ls,
  muted → white when active
- Optional "Save 20%" floating chip: anchored top-right, `-11px` / `-14px`
  offset, green gradient, grows to `scale(1.05)` when annual is active.

### 11.12 FAQ accordion

```css
.faq__item {
  border-radius: 8px;
  border: 1px solid var(--ts-glass-border);
  background: var(--ts-glass-bg);
  backdrop-filter: blur(12px);
  transition: border-color 200ms ease;
}
.faq__item.is-open { border-color: rgba(var(--ts-primary-rgb), 0.35); }

.faq__button {
  width: 100%; padding: 18px 22px;
  display: flex; align-items: center; justify-content: space-between;
  background: transparent; border: none; cursor: pointer; text-align: left;
  color: var(--ts-text); font-size: 15px; font-weight: 600;
}

.faq__chev {
  transition: transform 220ms cubic-bezier(.22, 1, .36, 1),
              color 220ms ease;
}
.faq__chev.is-open { transform: rotate(180deg); color: var(--ts-primary-light); }

.faq__panel {
  max-height: 0; overflow: hidden;
  transition: max-height 220ms cubic-bezier(.22, 1, .36, 1);
}
.faq__panel.is-open { max-height: 320px; }
```

**Important**: chevron and panel *must share the same duration and easing*
so they animate in lockstep - otherwise it reads as "arrow turns, then body
opens" (we fixed this). 220 ms feels right.

### 11.13 Terminal / code panel

Used for showcasing SDKs. Structure:

1. Outer frame: `var(--ts-code-bg)` bg, 10 px radius,
   `1px solid var(--ts-glass-border)`, elevated shadow (see §8).
2. Chrome bar: 3 macOS-style dots (red #ff5f57, yellow #febc2e, green #28c840)
   + a monospace filename on the left, a Copy button on the right.
3. Language tabs: small 13 px icons + label, underline-on-active style.
4. `<pre>` with `padding: 24 px`, `font-size: 13.5 px`, `line-height: 1.75`,
   `color: #cbd5e1`, `max-height: 480 px`, `overflow: auto`.

Syntax highlighting uses token classes:

```css
.tok-keyword { color: var(--ts-primary-light); }
.tok-string  { color: #86EFAC; }
.tok-comment { color: #64748B; font-style: italic; }
.tok-fn      { color: #FCD34D; }
.tok-num     { color: #F0ABFC; }
.tok-op      { color: #CBD5E1; }
```

Keep the highlighter minimal and hand-rolled - no Prism / shiki runtime.

### 11.14 Icon system

- UI glyphs: [Lucide](https://lucide.dev).
- Brand glyphs: [Simple Icons](https://simpleicons.org).
- **Never** ship a runtime icon library or a build-time icon component from
  npm. Check individual SVG files into `static/icons/lucide/*.svg` and
  `static/icons/simple-icons/*.svg`.
- Each SVG is monochrome and uses `currentColor`, so text colour classes
  control appearance.
- An `icons.ts` registry maps camelCase names (`arrowRight`, `brandDiscord`)
  to public paths. `Icon.svelte` renders a `<span>` that uses the SVG as a CSS
  `mask` over `background-color: currentColor`.
- Typical icon sizes: 11 px (chip / chevron), 14 px (button / status),
  16 px (nav), 18–20 px (card icon tile).

### 11.15 Animated price counter

For toggles that change numbers (billing, unit pricing), use a tweened
counter rather than a hard swap. The site uses Svelte's `tweened` with
`cubicOut`, 650 ms. Formatting rule: drop trailing `.00` so integers stay
crisp ($49 not $49.00), keep 2 decimals otherwise ($24.99).

```ts
// pseudocode
const display = tweened(value, { duration: 650, easing: cubicOut })
$: display.set(value)

const format = n => {
  const r = Math.round(n * 100) / 100
  return Number.isInteger(r) ? r.toString() : r.toFixed(2)
}
```

Render inside a `<span>` with `font-variant-numeric: tabular-nums` to avoid
digits jumping horizontally.

**Do not** add a glow / drop-shadow during the transition - we removed it
because it felt flashy.

### 11.16 Social buttons (footer)

32 × 32 px rounded-rect (`7px` radius) with **brand-coloured** tint per
network. The colour, bg, and border are provided per-link as inline styles;
the component just wires them up:

```js
{ name: 'GitHub',   color: '#F1F5F9', bg: 'rgba(241,245,249,0.08)', border: 'rgba(241,245,249,0.18)' }
{ name: 'Discord',  color: '#5865F2', bg: 'rgba(88,101,242,0.12)', border: 'rgba(88,101,242,0.35)' }
{ name: 'X',        color: '#FFFFFF', bg: 'rgba(255,255,255,0.06)', border: 'rgba(255,255,255,0.18)' }
{ name: 'LinkedIn', color: '#0A66C2', bg: 'rgba(10,102,194,0.14)', border: 'rgba(10,102,194,0.38)' }
{ name: 'Email',    color: '#10B981', bg: 'rgba(16,185,129,0.12)', border: 'rgba(16,185,129,0.35)' }
```

All lift `translateY(-2px)` and gain a brand-blue shadow on hover.

---

## 12. Page / section patterns

### 12.1 Hero

Composition top-to-bottom, all centred:

1. (optional) World-map widget as the background - see §13.
2. Wordmark mark (`logo-principal.svg`) with a soft breathing blue radial
   glow behind it (via the absolute `.hero__brand-glow` div animated with
   `ts-breathe`).
3. Headline (H1). Use gradient text on the trailing phrase only.
4. Muted subtitle (max 560–620 px wide, single concrete sentence).
5. Two CTAs: primary + glass secondary.
6. Highlight pills: 3 glass pills `padding: 9px 16px`, `gap: 10px`, `font-size
   13px`, each with a 15 × 15 icon (tinted primary-light) + label.
7. Section min-height: `clamp(580px, calc(78vh - 72px), 760px)`. Inner
   padding `40px`. No per-section background colour - the body glow handles
   the backdrop.

**Keep the hero quiet.** No crosshairs, grids, scanlines, or stacked orbs -
all of those were added and later removed because they fragmented the page.

### 12.2 Content section (Features, Pricing, Code panel, etc.)

```
[SectionHeader - eyebrow + H2 + muted subtitle, centred, max 720]
  48–64 px gutter
[Grid / panel of cards, same visual language]
```

Common grids:
- 2 × 2 feature cards (max-width 1240, gap 18)
- 3 × pricing cards (max-width 1240, gap `clamp(0.375rem, 1.3vw, 1rem)`) -
  stays on a single row at every width; card content scales down instead
- 3 × triad cards, horizontal layout (max-width 1120, gap 16)
- 1 × code terminal with 1.2fr preview column, gap 72 at desktop, gap 40
  stacked on mobile

### 12.3 Final CTA block

- Centred inside a bordered glass panel: `border: 1px solid
  rgba(var(--ts-primary-rgb), 0.28)`, `border-radius: 16px`,
  inner padding `48 40 40 px`, headline + muted copy + actions + perks list.
- Panel background stacks a radial blue glow with the glass bg:

```css
background:
  radial-gradient(ellipse 60% 100% at 50% 0%,
                  rgba(var(--ts-primary-rgb), 0.18), transparent 70%),
  var(--ts-glass-bg);
```

- Ambient shadow: `0 18px 48px rgba(var(--ts-primary-rgb), 0.16)` + inset
  `0 1px 0 rgba(255,255,255,0.05)`.
- Perks row: 3 short items (`checkCircle` icon tinted `--ts-secondary` at
  green), `font-size: 12px`, `gap: 22px`, flex-wrap.

---

## 13. World-map widget (optional)

Used in the hero to imply "multi-region". It is composed at build time from
a dotted equirectangular world map plus computed region coordinates so pins
and connection lines stay pixel-aligned with the continent dots.

Requirements:

- Library: [`dotted-map`](https://github.com/NTag/dotted-map) (MIT) as a
  *devDependency only*. Do not ship it at runtime.
- Generator script writes `static/world-map.svg` (brand-tinted dots on a
  transparent background) and a TS manifest (`world-map.generated.ts`)
  with viewBox dimensions + region (x, y) coordinates.
- Pins and connection arcs are overlays rendered by the component - they
  share a single "frame" element sized to the map's aspect ratio so the
  three layers stay aligned.
- Arcs use `preserveAspectRatio="none"` inline SVG paths
  (`M x1 y1 Q cx cy x2 y2`) with animated `stroke-dashoffset`.
- Pins are absolutely-positioned DOM spans with `left`/`top` expressed as
  percentages of the frame - a solid core `box-shadow: 0 0 0 2px
  rgba(5,5,8,0.9)` to punch through the map, plus a ring that expands via
  `@keyframes` from scale 1 → 3.8 fading to 0 over 2.6 s.
- The whole container uses a soft radial mask-image so the map dissolves
  into the body glow instead of ending in a rectangle:

```css
mask-image: radial-gradient(ellipse 95% 92% at 50% 50%,
  #000 0%, #000 70%, rgba(0,0,0,0.35) 88%, transparent 100%);
```

- Topology: a **ring** between the region hubs (currently 3; each region
  connects to the next, last closes back to first). Don't use full mesh (too
  noisy) or random pairs (reads like mistakes).
- Map size: `width: min(1600px, 100%)` with `aspect-ratio: 119 / 60` - lets
  the entire map fit inside the viewport while keeping regions at the right
  geographic coords.

---

## 14. Responsive breakpoints

The site uses a handful of max-width breakpoints, not a formal grid system:

| Breakpoint | Common switches |
|---|---|
| `max-width: 1240px` | Header collapses to hamburger |
| `max-width: 960px` | Code panel stacks, Footer grid → 2 cols, Contact grid → 1 col |
| `max-width: 900px` | 404 page stacks |
| `max-width: 860px` | Triad grid → 1 col, About sections → 1 col |
| `max-width: 760px` | Detailed pricing table header hides, rows stack |
| `max-width: 720px` | Hero padding/height shrinks, highlight pills stack |
| `max-width: 640px` | Most "final mobile" adjustments (Features → 1 col, FAQ title shrink, CTA card padding drops) |
| `max-width: 560px` | Footer grid → 1 col, meta row stacks, popular pricing badge collapses to its icon |
| `max-width: 420px` | Pricing card header stacks |

The only `@media (min-width: ...)` query keeps the CodePanel headline on one
line at `min-width: 961px`; everything else is mobile-friendly by default and
uses max-width overrides.

Section `*__inner` containers shrink their gutter from `0 40px` to
`0 20px` at their first mobile breakpoint.

---

## 15. Accessibility checklist

- Always set a visible focus ring (globally):

```css
:focus-visible { outline: 2px solid var(--ts-primary); outline-offset: 2px; border-radius: 4px; }
```

- Every toggle must announce state: `role="tablist"` / `role="tab"` /
  `aria-selected` on billing toggles; `aria-expanded` + `aria-controls` on
  the FAQ button, plus an `id`-matched `aria-controls` target on the panel.
- Decorative SVGs must carry `aria-hidden="true"`; meaningful icons need a
  `role="img"` + `aria-label`.
- Social link buttons always include an `aria-label={social.name}`.
- Text contrast: `--ts-text` (#F1F5F9) vs `--ts-bg-base` (#050508) = ≈17:1.
  Muted (`#9BACCB`) stays above 7:1.
- Every animation and scroll-reveal honours `prefers-reduced-motion` (see
  §9 snippet).

---

## 16. Content & voice guidelines

- **Grounded.** State what the product does; never state metrics or
  certifications you can't back up. Do not say "SOC 2 Type II", "99.99%
  uptime", "sub-20 ms latency", "20+ regions", "Fortune 500 companies" etc
  unless it is literally true and verifiable.
- **Casing rhythm.** Sentence case for headlines and body. UPPERCASE for
  eyebrows, pill labels, button text, status chips, column headings. Never
  Title Case for a whole paragraph.
- **Keep Free separate from paid billing.** Free requires no payment card.
  Starter is billed $30 yearly only ($2.50/month equivalent); Pro and Team
  renew monthly. Do not advertise trials or annual Pro/Team discounts.
  Enterprise is $999/month support plus usage through sales. Subscription
  prices are approved; allowances and usage rates remain proposed, and usage
  charges are not yet collected. Do not imply checkout or billing readiness.
- **Export + no-lock-in is a first-class story.** FAQ addresses this
  directly: rate-limit on exports is for platform health, never charges,
  never locks users in.
- **Two-sentence descriptions.** Subtitles and card body text fit into 1–2
  short sentences. If it needs a third sentence, trim.
- **Gradient phrase trick.** H1/H2 headlines often wrap onto 2 lines; the
  trailing short phrase gets `.ts-text-gradient` applied to it. Keep the
  gradient phrase to 2–3 words.

---

## 17. Quick-start checklist

If you're applying this design to a new page, work in this order:

1. Drop the token block from §3 into your global CSS.
2. Import the three font families from §1 (preload Quantico locally,
   preconnect Google Fonts for Roboto + Roboto Mono).
3. Paint the body glow from §10 (no other backgrounds).
4. Add the sticky header and footer from §11.1–11.2.
5. Pick section patterns from §12 - every page starts with a
   `SectionHeader`, wraps content in `max-width: 1240px` + `0 40px` gutter,
   and uses section `padding: 80–88px 0`.
6. For any new card or panel, reach for `.ts-glass` + one of the card
   patterns in §11.9 before inventing new styles.
7. Buttons, inputs, eyebrows, gradient text, pulse-dot - compose from
   §11.3–11.7, do not write new variants.
8. Respect `prefers-reduced-motion` from day one.
9. Copy passes the grounded-voice test (§16) - strip any uptime / latency /
   certification claim that isn't literally verifiable.

---

## 18. Appendix: file layout reference

```
src/
├── app.html                     # document shell (§1)
├── index.css                    # tokens + utilities (§3, §9, §10)
├── lib/
│   ├── site.ts                  # nav, footer, social data
│   ├── i18n/                    # locales + message dictionaries
│   ├── icons.ts                 # icon registry
│   ├── highlight.ts             # code tokenizer (§11.13)
│   ├── motion/reveal.ts         # IntersectionObserver action
│   └── world-map.generated.ts   # generated region coords (§13)
├── components/
│   ├── Header.svelte            # §11.1
│   ├── Footer.svelte            # §11.2
│   ├── Icon.svelte              # §11.14
│   ├── LanguageSelector.svelte  # header + footer locale switch
│   ├── ScrollReveal.svelte      # wrapper around reveal action
│   ├── AnimatedPrice.svelte     # §11.15
│   ├── RouteLink.svelte         # internal nav
│   ├── ExternalLink.svelte      # external nav
│   └── sections/
│       ├── SectionHeader.svelte # §11.6
│       ├── Hero.svelte          # §12.1
│       ├── Features.svelte      # §11.9 (feature card)
│       ├── CodePanel.svelte     # §11.13
│       ├── PricingSection.svelte# §11.9, §11.10, §11.11, §11.15
│       ├── PricingFAQ.svelte    # §11.12
│       ├── DetailedPricingTable.svelte
│       ├── PricingFeatureTriad.svelte # §11.9 (triad card)
│       ├── PricingCTA.svelte    # simple final CTA
│       └── CtaSection.svelte    # §12.3
├── pages/
│   ├── Home.svelte              # Hero → CodePanel → Features → Pricing → Cta
│   ├── Pricing.svelte           # PricingSection → Triad → Detailed → FAQ → CTA
│   ├── About.svelte
│   ├── Contact.svelte
│   └── NotFound.svelte
└── routes/                      # SvelteKit adapters that mount pages
static/
├── logo-principal.svg  logo-white.svg  logo-blue.svg  logo-black.svg
├── icon-blue.svg       icon-white.svg  icon-black.svg
├── world-map.svg
├── 404-number.webp     404-illustration.webp
├── fonts/quantico.woff2  quantico-bold.woff2  jetbrains-mono.woff2  ...
├── icons/lucide/*.svg  icons/simple-icons/*.svg
└── language-icons/*.svg
scripts/
├── icons-sync.mjs            # regenerates static/icons + src/lib/icons.ts from manifest
├── icons-manifest.mjs
└── world-map-sync.mjs        # regenerates static/world-map.svg + manifest
```

Tech: SvelteKit 2 + Svelte 5 + Vite 8 + Tailwind 4 (for `@theme` tokens) +
TypeScript. Static prerendering via `@sveltejs/adapter-static`.

Not all of that stack is required on a consumer - the tokens, utilities, and
component patterns above are what define the look.
