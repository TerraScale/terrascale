<div align="center">

# TerraScale Design System

**Hybrid Neo-Brutalist UI Framework**

A modern design system blending sharp brutalist geometry with polished glassmorphism, vibrant gradients, and ambient glow effects.

`#2563EB` | `#10B981` | `#0891B2` | `#8B5CF6`

[Quick Reference](#quick-reference) | [Colors](#color-palette) | [Effects](#effects) | [Glassmorphism](#glassmorphism) | [Gradients](#gradients) | [Components](#component-patterns)

</div>

---

## Quick Reference

> Copy-paste ready values for rapid development

| Category | Token | Value |
|----------|-------|-------|
| Primary | `--ts-color-primary` | `#2563EB` |
| Secondary | `--ts-color-secondary` | `#10B981` |
| Accent | `--ts-color-accent` | `#0891B2` |
| Tertiary | `--ts-color-tertiary` | `#8B5CF6` |
| Background | `--ts-bg-surface` | `#0A0A0E` |
| Text | `--ts-color-text` | `#F8FAFC` |
| Font | `--ts-font-display` | `'Quantico', system-ui, sans-serif` |
| Radius | `--ts-radius-md` | `6px` |
| Shadow | `--ts-shadow-soft` | `0 4px 6px rgba(0,0,0,0.35)` |
| Glow | `--ts-shadow-glow-primary` | `0 0 20px rgba(37,99,235,0.4)` |
| Glass | `--ts-glass-bg` | `rgba(255,255,255,0.06)` |
| Blur | `--ts-blur-lg` | `12px` |

---

## Table of Contents

1. [Design Philosophy](#design-philosophy)
2. [Color Palette](#color-palette)
3. [Typography](#typography)
4. [Spacing Scale](#spacing-scale)
5. [Effects](#effects)
6. [Glassmorphism](#glassmorphism)
7. [Gradients](#gradients)
8. [Glow Effects](#glow-effects)
9. [Layout](#layout)
10. [Component Patterns](#component-patterns)
11. [Decorative Patterns](#decorative-patterns)
12. [Accessibility](#accessibility)
13. [Implementation Examples](#implementation-examples)
14. [Assets & Source Files](#assets--source-files)

---

## Design Philosophy

> TerraScale blends **Neo-Brutalist** sharpness with **modern polish**: bold geometry meets glassmorphism, gradients, and ambient glow.

### Core Principles

| Principle | Implementation |
|-----------|----------------|
| Hybrid Geometry | Border radius: 4-16px (sharp but polished) |
| Layered Shadows | Brutal + soft + glow shadow options |
| Single Typeface | Quantico for ALL text |
| Ultra-Dark Mode | Near-black backgrounds with vibrant accents |
| Glassmorphism | Frosted glass effects with backdrop blur |
| Gradient Accents | Button, border, and text gradients |
| Ambient Glow | Hover and focus glow effects |

> **Font Requirement**
>
> **Quantico is the ONLY font allowed in TerraScale.** Do not introduce any other fonts (including monospace fonts like JetBrains Mono, Fira Code, etc.). This ensures consistent branding and reduces bundle size.

---

## Color Palette

### Primary Colors

| Token | Value | Preview | Usage |
|-------|-------|---------|-------|
| `--ts-color-primary` | `#2563EB` | ![#2563EB](https://via.placeholder.com/16/2563EB/2563EB) | Primary actions, links, accents |
| `--ts-color-primary-light` | `#3B82F6` | ![#3B82F6](https://via.placeholder.com/16/3B82F6/3B82F6) | Hover states, lighter accents |
| `--ts-color-primary-dark` | `#1D4ED8` | ![#1D4ED8](https://via.placeholder.com/16/1D4ED8/1D4ED8) | Active states, darker accents |
| `--ts-color-primary-glow` | `#60A5FA` | ![#60A5FA](https://via.placeholder.com/16/60A5FA/60A5FA) | Glow effects, highlights |

### Secondary Colors

| Token | Value | Preview | Usage |
|-------|-------|---------|-------|
| `--ts-color-secondary` | `#10B981` | ![#10B981](https://via.placeholder.com/16/10B981/10B981) | Success, secondary actions |
| `--ts-color-secondary-light` | `#34D399` | ![#34D399](https://via.placeholder.com/16/34D399/34D399) | Success hover states |
| `--ts-color-secondary-dark` | `#059669` | ![#059669](https://via.placeholder.com/16/059669/059669) | Success active states |
| `--ts-color-secondary-glow` | `#6EE7B7` | ![#6EE7B7](https://via.placeholder.com/16/6EE7B7/6EE7B7) | Success glow effects |

### Accent Colors (Cyan)

| Token | Value | Preview | Usage |
|-------|-------|---------|-------|
| `--ts-color-accent` | `#0891B2` | ![#0891B2](https://via.placeholder.com/16/0891B2/0891B2) | Tertiary accents, highlights |
| `--ts-color-accent-light` | `#22D3EE` | ![#22D3EE](https://via.placeholder.com/16/22D3EE/22D3EE) | Accent hover states |
| `--ts-color-accent-dark` | `#0E7490` | ![#0E7490](https://via.placeholder.com/16/0E7490/0E7490) | Accent active states |
| `--ts-color-accent-glow` | `#67E8F9` | ![#67E8F9](https://via.placeholder.com/16/67E8F9/67E8F9) | Cyan glow effects |

### Tertiary Colors (Violet)

| Token | Value | Preview | Usage |
|-------|-------|---------|-------|
| `--ts-color-tertiary` | `#8B5CF6` | ![#8B5CF6](https://via.placeholder.com/16/8B5CF6/8B5CF6) | Premium accents, special features |
| `--ts-color-tertiary-light` | `#A78BFA` | ![#A78BFA](https://via.placeholder.com/16/A78BFA/A78BFA) | Tertiary hover states |
| `--ts-color-tertiary-dark` | `#7C3AED` | ![#7C3AED](https://via.placeholder.com/16/7C3AED/7C3AED) | Tertiary active states |
| `--ts-color-tertiary-glow` | `#C4B5FD` | ![#C4B5FD](https://via.placeholder.com/16/C4B5FD/C4B5FD) | Violet glow effects |

### Semantic Colors

| Token | Value | Preview | Usage |
|-------|-------|---------|-------|
| `--ts-color-info` | `#38BDF8` | ![#38BDF8](https://via.placeholder.com/16/38BDF8/38BDF8) | Informational messages |
| `--ts-color-success` | `#10B981` | ![#10B981](https://via.placeholder.com/16/10B981/10B981) | Success states |
| `--ts-color-warning` | `#FBBF24` | ![#FBBF24](https://via.placeholder.com/16/FBBF24/FBBF24) | Warning messages |
| `--ts-color-error` | `#F43F5E` | ![#F43F5E](https://via.placeholder.com/16/F43F5E/F43F5E) | Error states, destructive actions |

### Background Colors

| Token | Value | Preview | Usage |
|-------|-------|---------|-------|
| `--ts-bg-base` | `#050508` | ![#050508](https://via.placeholder.com/16/050508/050508) | Page background (darkest) |
| `--ts-bg-surface` | `#0A0A0E` | ![#0A0A0E](https://via.placeholder.com/16/0A0A0E/0A0A0E) | Card backgrounds |
| `--ts-bg-elevated` | `#0C0C10` | ![#0C0C10](https://via.placeholder.com/16/0C0C10/0C0C10) | Appbar, elevated surfaces |
| `--ts-bg-card` | `#080810` | ![#080810](https://via.placeholder.com/16/080810/080810) | Drawer, card backgrounds |

### Text Colors

| Token | Value | Preview | Usage |
|-------|-------|---------|-------|
| `--ts-color-text` | `#F8FAFC` | ![#F8FAFC](https://via.placeholder.com/16/F8FAFC/F8FAFC) | Primary text (near-white) |
| `--ts-color-text-muted` | `#94A3B8` | ![#94A3B8](https://via.placeholder.com/16/94A3B8/94A3B8) | Secondary/muted text |
| `--ts-color-text-disabled` | `#475569` | ![#475569](https://via.placeholder.com/16/475569/475569) | Disabled text |
| `--ts-color-divider` | `#1A1A24` | ![#1A1A24](https://via.placeholder.com/16/1A1A24/1A1A24) | Dividers and borders |

### RGB Variants (for rgba usage)

```css
--ts-color-primary-rgb: 37, 99, 235;
--ts-color-secondary-rgb: 16, 185, 129;
--ts-color-accent-rgb: 8, 145, 178;
--ts-color-tertiary-rgb: 139, 92, 246;
```

---

## Typography

### Font Family

> **Quantico is the ONLY font used in TerraScale.**

| Token | Value | Usage |
|-------|-------|-------|
| `--ts-font-display` | `'Quantico', system-ui, -apple-system, sans-serif` | ALL text elements |

### Font Loading

```html
<!-- Preload critical fonts -->
<link rel="preload" href="fonts/quantico/Quantico-Regular.woff2" as="font" type="font/woff2" crossorigin>
<link rel="preload" href="fonts/quantico/Quantico-Bold.woff2" as="font" type="font/woff2" crossorigin>
```

### Typography Scale

| Element | Size | Weight | Line Height | Letter Spacing |
|---------|------|--------|-------------|----------------|
| H1 | 2.75rem (44px) | 700 | 1.1 | -0.03em |
| H2 | 2.25rem (36px) | 700 | 1.15 | -0.02em |
| H3 | 1.75rem (28px) | 700 | 1.2 | -0.01em |
| H4 | 1.375rem (22px) | 700 | 1.3 | normal |
| H5 | 1.125rem (18px) | 700 | 1.35 | normal |
| H6 | 1rem (16px) | 700 | 1.4 | normal |
| Body1 | 1rem (16px) | 400 | 1.6 | 0.01em |
| Body2 | 0.875rem (14px) | 400 | 1.5 | normal |
| Button | 0.8125rem (13px) | 600 | 1.4 | 0.05em, UPPERCASE |
| Caption | 0.6875rem (11px) | 500 | 1.4 | 0.03em |

### Fluid Typography

```css
--ts-font-size-h1: clamp(1.75rem, 1.5rem + 1.5vw, 2.75rem);
--ts-font-size-h2: clamp(1.5rem, 1.25rem + 1.25vw, 2.25rem);
--ts-font-size-h3: clamp(1.25rem, 1.1rem + 0.75vw, 1.75rem);
--ts-font-size-h4: clamp(1.125rem, 1rem + 0.5vw, 1.375rem);
--ts-font-size-body1: clamp(0.875rem, 0.85rem + 0.15vw, 1rem);
--ts-font-size-metric: clamp(1.5rem, 1.25rem + 1vw, 2.25rem);
```

---

## Spacing Scale

> Extended scale with generous breathing room

| Token | Value | Pixels | Usage |
|-------|-------|--------|-------|
| `--ts-space-1` | 0.25rem | 4px | Tight spacing |
| `--ts-space-2` | 0.5rem | 8px | Compact elements |
| `--ts-space-3` | 0.75rem | 12px | Small gaps |
| `--ts-space-4` | 1rem | 16px | Default spacing |
| `--ts-space-5` | 1.25rem | 20px | Comfortable gaps |
| `--ts-space-6` | 1.5rem | 24px | Card padding |
| `--ts-space-7` | 1.75rem | 28px | Generous padding |
| `--ts-space-8` | 2rem | 32px | Section gaps |
| `--ts-space-10` | 2.5rem | 40px | Large sections |
| `--ts-space-12` | 3rem | 48px | Major sections |
| `--ts-space-16` | 4rem | 64px | Page sections |
| `--ts-space-20` | 5rem | 80px | Hero sections |
| `--ts-space-24` | 6rem | 96px | Large separations |

---

## Effects

### Border Radius

> Hybrid approach: polished but not overly rounded

| Token | Value | Usage |
|-------|-------|-------|
| `--ts-radius-none` | `0px` | Pure brutalist elements |
| `--ts-radius-sm` | `4px` | Chips, tags, status dots |
| `--ts-radius-md` | `6px` | Buttons, inputs (default) |
| `--ts-radius-lg` | `8px` | Cards, containers |
| `--ts-radius-xl` | `12px` | Modals, dialogs |
| `--ts-radius-2xl` | `16px` | Feature cards, hero sections |
| `--ts-radius-pill` | `9999px` | Pills, toggles |

### Shadow System

#### Brutal Shadows (Brand Identity)

| Token | Value | Usage |
|-------|-------|-------|
| `--ts-shadow-brutal-sm` | `2px 2px 0 rgba(0, 0, 0, 0.5)` | Small elements |
| `--ts-shadow-brutal` | `4px 4px 0 rgba(0, 0, 0, 0.6)` | Default brutal |
| `--ts-shadow-brutal-lg` | `6px 6px 0 rgba(0, 0, 0, 0.7)` | Large elements |
| `--ts-shadow-brutal-xl` | `8px 8px 0 rgba(0, 0, 0, 0.75)` | Hero elements |

#### Soft Shadows (Modern Polish)

| Token | Value | Usage |
|-------|-------|-------|
| `--ts-shadow-soft-sm` | `0 2px 4px rgba(0,0,0,0.3), 0 1px 2px rgba(0,0,0,0.2)` | Subtle elevation |
| `--ts-shadow-soft` | `0 4px 6px rgba(0,0,0,0.35), 0 2px 4px rgba(0,0,0,0.25)` | Default soft |
| `--ts-shadow-soft-lg` | `0 10px 15px rgba(0,0,0,0.4), 0 4px 6px rgba(0,0,0,0.3)` | Cards, modals |
| `--ts-shadow-soft-xl` | `0 20px 25px rgba(0,0,0,0.45), 0 10px 10px rgba(0,0,0,0.35)` | Popovers |

#### Layered Shadows (Depth)

```css
--ts-shadow-layered:
  0 1px 2px rgba(0, 0, 0, 0.3),
  0 2px 4px rgba(0, 0, 0, 0.25),
  0 4px 8px rgba(0, 0, 0, 0.2),
  0 8px 16px rgba(0, 0, 0, 0.15);

--ts-shadow-layered-lg:
  0 2px 4px rgba(0, 0, 0, 0.35),
  0 4px 8px rgba(0, 0, 0, 0.3),
  0 8px 16px rgba(0, 0, 0, 0.25),
  0 16px 32px rgba(0, 0, 0, 0.2);
```

### Borders

| Token | Value | Usage |
|-------|-------|-------|
| `--ts-border-default` | `1px solid rgba(255, 255, 255, 0.08)` | Standard border |
| `--ts-border-strong` | `2px solid rgba(255, 255, 255, 0.12)` | Emphasized borders |
| `--ts-border-accent` | `2px solid var(--ts-color-primary)` | Primary accent |
| `--ts-border-subtle` | `1px solid rgba(255, 255, 255, 0.04)` | Subtle dividers |

### Transitions

| Token | Value | Usage |
|-------|-------|-------|
| `--ts-transition-fast` | `100ms ease-out` | Hover states |
| `--ts-transition-normal` | `150ms ease-out` | Standard |
| `--ts-transition-slow` | `250ms ease-out` | Larger animations |
| `--ts-transition-spring` | `300ms cubic-bezier(0.175, 0.885, 0.32, 1.275)` | Bouncy effects |

---

## Glassmorphism

### Backdrop Blur

| Token | Value | Usage |
|-------|-------|-------|
| `--ts-blur-sm` | `4px` | Subtle blur |
| `--ts-blur-md` | `8px` | Medium blur |
| `--ts-blur-lg` | `12px` | Standard glass |
| `--ts-blur-xl` | `16px` | Heavy blur |
| `--ts-blur-2xl` | `24px` | Maximum blur |

### Glass Backgrounds

| Token | Value | Usage |
|-------|-------|-------|
| `--ts-glass-bg-subtle` | `rgba(255, 255, 255, 0.02)` | Very subtle |
| `--ts-glass-bg-light` | `rgba(255, 255, 255, 0.04)` | Light glass |
| `--ts-glass-bg` | `rgba(255, 255, 255, 0.06)` | Default glass |
| `--ts-glass-bg-strong` | `rgba(255, 255, 255, 0.08)` | Strong glass |
| `--ts-glass-bg-heavy` | `rgba(10, 10, 14, 0.7)` | Dark frosted |

### Glass Borders

| Token | Value | Usage |
|-------|-------|-------|
| `--ts-glass-border-subtle` | `1px solid rgba(255, 255, 255, 0.06)` | Subtle edge |
| `--ts-glass-border` | `1px solid rgba(255, 255, 255, 0.10)` | Default glass border |
| `--ts-glass-border-strong` | `1px solid rgba(255, 255, 255, 0.15)` | Strong edge |
| `--ts-glass-border-bright` | `1px solid rgba(255, 255, 255, 0.20)` | Bright edge |

### Glass Utility Classes

```css
/* Basic glass */
.ts-glass {
  background: var(--ts-glass-bg);
  backdrop-filter: blur(var(--ts-blur-lg));
  -webkit-backdrop-filter: blur(var(--ts-blur-lg));
  border: var(--ts-glass-border);
  border-radius: var(--ts-radius-lg);
}

/* Frosted glass - heavier effect */
.ts-glass-frosted {
  background: var(--ts-glass-bg-strong);
  backdrop-filter: blur(var(--ts-blur-xl));
  -webkit-backdrop-filter: blur(var(--ts-blur-xl));
  border: var(--ts-glass-border-strong);
  border-radius: var(--ts-radius-lg);
}

/* Heavy glass - maximum blur */
.ts-glass-heavy {
  background: var(--ts-glass-bg-heavy);
  backdrop-filter: blur(var(--ts-blur-2xl));
  -webkit-backdrop-filter: blur(var(--ts-blur-2xl));
  border: var(--ts-glass-border-bright);
  border-radius: var(--ts-radius-xl);
}

/* Colored glass variants */
.ts-glass-primary {
  background: rgba(37, 99, 235, 0.1);
  backdrop-filter: blur(var(--ts-blur-lg));
  -webkit-backdrop-filter: blur(var(--ts-blur-lg));
  border: 1px solid rgba(37, 99, 235, 0.2);
}

.ts-glass-accent {
  background: rgba(8, 145, 178, 0.1);
  backdrop-filter: blur(var(--ts-blur-lg));
  -webkit-backdrop-filter: blur(var(--ts-blur-lg));
  border: 1px solid rgba(8, 145, 178, 0.2);
}

.ts-glass-tertiary {
  background: rgba(139, 92, 246, 0.1);
  backdrop-filter: blur(var(--ts-blur-lg));
  -webkit-backdrop-filter: blur(var(--ts-blur-lg));
  border: 1px solid rgba(139, 92, 246, 0.2);
}
```

---

## Gradients

### Button Gradients

| Token | Value | Usage |
|-------|-------|-------|
| `--ts-gradient-primary` | `linear-gradient(135deg, #3B82F6 0%, #2563EB 50%, #1D4ED8 100%)` | Primary buttons |
| `--ts-gradient-primary-hover` | `linear-gradient(135deg, #60A5FA 0%, #3B82F6 50%, #2563EB 100%)` | Primary hover |
| `--ts-gradient-secondary` | `linear-gradient(135deg, #34D399 0%, #10B981 50%, #059669 100%)` | Secondary buttons |
| `--ts-gradient-accent` | `linear-gradient(135deg, #22D3EE 0%, #0891B2 50%, #0E7490 100%)` | Accent buttons |
| `--ts-gradient-tertiary` | `linear-gradient(135deg, #A78BFA 0%, #8B5CF6 50%, #7C3AED 100%)` | Tertiary buttons |

### Special Gradients

```css
/* Rainbow gradient */
--ts-gradient-rainbow: linear-gradient(
  135deg,
  #2563EB 0%,
  #8B5CF6 35%,
  #0891B2 70%,
  #10B981 100%
);

/* Cosmic gradient */
--ts-gradient-cosmic: linear-gradient(
  135deg,
  #1D4ED8 0%,
  #7C3AED 50%,
  #0E7490 100%
);

/* Sunset gradient */
--ts-gradient-sunset: linear-gradient(
  135deg,
  #F43F5E 0%,
  #8B5CF6 50%,
  #2563EB 100%
);
```

### Mesh Gradient Backgrounds

```css
/* Primary mesh */
--ts-mesh-primary:
  radial-gradient(ellipse at 20% 20%, rgba(37, 99, 235, 0.15) 0%, transparent 50%),
  radial-gradient(ellipse at 80% 80%, rgba(139, 92, 246, 0.12) 0%, transparent 50%),
  radial-gradient(ellipse at 40% 80%, rgba(8, 145, 178, 0.10) 0%, transparent 50%);

/* Cosmic mesh */
--ts-mesh-cosmic:
  radial-gradient(ellipse at 0% 0%, rgba(37, 99, 235, 0.2) 0%, transparent 40%),
  radial-gradient(ellipse at 100% 0%, rgba(139, 92, 246, 0.15) 0%, transparent 40%),
  radial-gradient(ellipse at 50% 100%, rgba(8, 145, 178, 0.15) 0%, transparent 40%);

/* Subtle mesh */
--ts-mesh-subtle:
  radial-gradient(ellipse at 30% 20%, rgba(37, 99, 235, 0.08) 0%, transparent 50%),
  radial-gradient(ellipse at 70% 70%, rgba(8, 145, 178, 0.06) 0%, transparent 50%);
```

### Gradient Border Technique

```css
/* Gradient border using pseudo-element */
.ts-border-gradient {
  position: relative;
  background: var(--ts-bg-surface);
  border-radius: var(--ts-radius-lg);
}

.ts-border-gradient::before {
  content: '';
  position: absolute;
  inset: 0;
  border-radius: inherit;
  padding: 2px;
  background: linear-gradient(135deg, #3B82F6, #8B5CF6, #0891B2);
  -webkit-mask:
    linear-gradient(#fff 0 0) content-box,
    linear-gradient(#fff 0 0);
  -webkit-mask-composite: xor;
  mask-composite: exclude;
  pointer-events: none;
}

/* Animated gradient border */
.ts-border-gradient-animated::before {
  background: linear-gradient(
    135deg,
    #3B82F6, #8B5CF6, #0891B2, #10B981, #3B82F6
  );
  background-size: 300% 300%;
  animation: gradient-shift 4s ease infinite;
}

@keyframes gradient-shift {
  0%, 100% { background-position: 0% 50%; }
  50% { background-position: 100% 50%; }
}
```

### Text Gradients

```css
/* Gradient text utilities */
.ts-text-gradient {
  background: var(--ts-gradient-primary);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}

.ts-text-gradient-accent {
  background: linear-gradient(135deg, #22D3EE, #0891B2);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}

.ts-text-gradient-rainbow {
  background: var(--ts-gradient-rainbow);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}

/* Animated gradient text */
.ts-text-gradient-animated {
  background: linear-gradient(90deg, #3B82F6, #8B5CF6, #0891B2, #3B82F6);
  background-size: 200% auto;
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
  animation: text-gradient-flow 3s linear infinite;
}

@keyframes text-gradient-flow {
  0% { background-position: 0% center; }
  100% { background-position: 200% center; }
}
```

---

## Glow Effects

### Glow Shadows

| Token | Value | Usage |
|-------|-------|-------|
| `--ts-shadow-glow-primary-sm` | `0 0 10px rgba(37, 99, 235, 0.3)` | Subtle primary glow |
| `--ts-shadow-glow-primary` | `0 0 20px rgba(37, 99, 235, 0.4), 0 0 40px rgba(37, 99, 235, 0.2)` | Default primary glow |
| `--ts-shadow-glow-primary-lg` | `0 0 30px rgba(37, 99, 235, 0.5), 0 0 60px rgba(37, 99, 235, 0.3)` | Large primary glow |
| `--ts-shadow-glow-accent` | `0 0 20px rgba(8, 145, 178, 0.4), 0 0 40px rgba(8, 145, 178, 0.2)` | Accent glow |
| `--ts-shadow-glow-secondary` | `0 0 20px rgba(16, 185, 129, 0.4), 0 0 40px rgba(16, 185, 129, 0.2)` | Secondary glow |
| `--ts-shadow-glow-tertiary` | `0 0 20px rgba(139, 92, 246, 0.4), 0 0 40px rgba(139, 92, 246, 0.2)` | Tertiary glow |
| `--ts-shadow-glow-error` | `0 0 20px rgba(244, 63, 94, 0.4), 0 0 40px rgba(244, 63, 94, 0.2)` | Error glow |

### Hover Glow Effect

```css
.ts-hover-glow {
  position: relative;
  transition: all var(--ts-transition-normal);
}

.ts-hover-glow::before {
  content: '';
  position: absolute;
  inset: -2px;
  border-radius: inherit;
  background: var(--ts-gradient-primary);
  opacity: 0;
  filter: blur(12px);
  transition: opacity var(--ts-transition-normal);
  z-index: -1;
}

.ts-hover-glow:hover::before {
  opacity: 0.5;
}
```

### Focus Glow

```css
.ts-focus-glow:focus-visible {
  outline: none;
  box-shadow:
    0 0 0 2px var(--ts-bg-surface),
    0 0 0 4px var(--ts-color-primary),
    0 0 20px rgba(37, 99, 235, 0.4);
}
```

### Neon Effect

```css
.ts-neon-active {
  box-shadow:
    0 0 5px var(--ts-color-primary),
    0 0 10px var(--ts-color-primary),
    0 0 20px rgba(37, 99, 235, 0.5),
    0 0 40px rgba(37, 99, 235, 0.3);
}

/* Pulsing neon */
.ts-neon-pulse {
  animation: neon-pulse 2s ease-in-out infinite;
}

@keyframes neon-pulse {
  0%, 100% {
    box-shadow:
      0 0 5px var(--ts-color-primary),
      0 0 10px var(--ts-color-primary),
      0 0 20px rgba(37, 99, 235, 0.4);
  }
  50% {
    box-shadow:
      0 0 10px var(--ts-color-primary),
      0 0 20px var(--ts-color-primary),
      0 0 40px rgba(37, 99, 235, 0.6),
      0 0 60px rgba(37, 99, 235, 0.3);
  }
}
```

### Ambient Card Glow

```css
.ts-card-glow {
  position: relative;
}

.ts-card-glow::after {
  content: '';
  position: absolute;
  inset: 0;
  border-radius: inherit;
  opacity: 0;
  transition: opacity var(--ts-transition-normal);
  pointer-events: none;
  background: radial-gradient(
    ellipse at center,
    rgba(37, 99, 235, 0.15) 0%,
    transparent 70%
  );
  filter: blur(20px);
  z-index: -1;
}

.ts-card-glow:hover::after {
  opacity: 1;
}
```

---

## Layout

### Responsive Breakpoints

| Token | Min-Width | Target Devices |
|-------|-----------|----------------|
| `xs` | 0px | Mobile phones (portrait) |
| `sm` | 600px | Mobile phones (landscape), small tablets |
| `md` | 960px | Tablets (portrait), small laptops |
| `lg` | 1280px | Laptops, desktops |
| `xl` | 1920px | Large desktops, high-res displays |
| `xxl` | 2560px | Ultra-wide, 4K displays |

### Grid Pattern Background

```css
--ts-grid-pattern: linear-gradient(rgba(37, 99, 235, 0.03) 1px, transparent 1px),
                   linear-gradient(90deg, rgba(37, 99, 235, 0.03) 1px, transparent 1px);

.grid-background {
  background-color: var(--ts-bg-base);
  background-image: var(--ts-grid-pattern);
  background-size: 24px 24px;
}
```

---

## Component Patterns

### Pattern Summary

| Component | Key Effects |
|-----------|-------------|
| Stats Card | Glass background, gradient border-top, ambient glow |
| Primary Button | Gradient fill, shine effect, hover glow |
| Glass Button | Frosted glass, gradient border on hover |
| Badge | Subtle glow, glowing status dot |
| Table | Hover glow, gradient left accent |
| Input | Glass background, focus glow ring |

### Stats Card (Glass + Glow)

```css
.ts-stats-card {
  background: var(--ts-glass-bg);
  backdrop-filter: blur(var(--ts-blur-lg));
  -webkit-backdrop-filter: blur(var(--ts-blur-lg));
  border: var(--ts-glass-border);
  border-top: 3px solid var(--ts-color-primary);
  border-radius: var(--ts-radius-lg);
  padding: var(--ts-space-7);
  position: relative;
  overflow: hidden;
  transition: all var(--ts-transition-normal);
}

/* Subtle gradient overlay */
.ts-stats-card::before {
  content: '';
  position: absolute;
  inset: 0;
  background: linear-gradient(
    135deg,
    rgba(37, 99, 235, 0.05) 0%,
    transparent 50%
  );
  pointer-events: none;
}

/* Ambient glow on hover */
.ts-stats-card::after {
  content: '';
  position: absolute;
  inset: 0;
  border-radius: inherit;
  opacity: 0;
  transition: opacity var(--ts-transition-normal);
  pointer-events: none;
  background: radial-gradient(
    ellipse at center,
    rgba(37, 99, 235, 0.12) 0%,
    transparent 70%
  );
  filter: blur(20px);
  z-index: -1;
}

.ts-stats-card:hover {
  transform: translateY(-2px);
  border-color: rgba(37, 99, 235, 0.3);
}

.ts-stats-card:hover::after {
  opacity: 1;
}

/* Metric value with gradient text */
.ts-stats-card .metric-value {
  font-family: var(--ts-font-display);
  font-size: var(--ts-font-size-metric);
  font-weight: 700;
  background: var(--ts-gradient-primary);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}
```

### Primary Button (Gradient + Glow)

```css
.ts-btn-primary {
  background: var(--ts-gradient-primary);
  border: none;
  border-radius: var(--ts-radius-md);
  color: #ffffff;
  font-family: var(--ts-font-display);
  font-weight: 600;
  font-size: 0.8125rem;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  padding: var(--ts-space-3) var(--ts-space-7);
  position: relative;
  overflow: hidden;
  transition: all var(--ts-transition-normal);
  box-shadow: var(--ts-shadow-soft);
  cursor: pointer;
}

/* Glow layer */
.ts-btn-primary::before {
  content: '';
  position: absolute;
  inset: -4px;
  background: var(--ts-gradient-primary);
  border-radius: inherit;
  opacity: 0;
  filter: blur(12px);
  transition: opacity var(--ts-transition-normal);
  z-index: -1;
}

/* Shine effect */
.ts-btn-primary::after {
  content: '';
  position: absolute;
  top: 0;
  left: -100%;
  width: 100%;
  height: 100%;
  background: linear-gradient(
    90deg,
    transparent,
    rgba(255, 255, 255, 0.2),
    transparent
  );
  transition: left 0.5s ease;
}

.ts-btn-primary:hover {
  transform: translateY(-2px);
  box-shadow: var(--ts-shadow-soft-lg);
}

.ts-btn-primary:hover::before {
  opacity: 0.5;
}

.ts-btn-primary:hover::after {
  left: 100%;
}

.ts-btn-primary:active {
  transform: translateY(0);
  box-shadow: var(--ts-shadow-soft-sm);
}

.ts-btn-primary:focus-visible {
  outline: none;
  box-shadow:
    0 0 0 2px var(--ts-bg-surface),
    0 0 0 4px var(--ts-color-primary),
    var(--ts-shadow-glow-primary-sm);
}
```

### Glass Button

```css
.ts-btn-glass {
  background: var(--ts-glass-bg);
  backdrop-filter: blur(var(--ts-blur-md));
  -webkit-backdrop-filter: blur(var(--ts-blur-md));
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: var(--ts-radius-md);
  color: var(--ts-color-text);
  font-family: var(--ts-font-display);
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.03em;
  padding: var(--ts-space-3) var(--ts-space-6);
  transition: all var(--ts-transition-normal);
  cursor: pointer;
}

.ts-btn-glass:hover {
  background: rgba(37, 99, 235, 0.1);
  border-color: var(--ts-color-primary);
  color: var(--ts-color-primary-light);
  box-shadow: var(--ts-shadow-glow-primary-sm);
}
```

### Badge with Glow

```css
.ts-badge {
  display: inline-flex;
  align-items: center;
  gap: var(--ts-space-2);
  font-family: var(--ts-font-display);
  font-size: 0.6875rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  padding: 6px 14px;
  border-radius: var(--ts-radius-sm);
  transition: all var(--ts-transition-fast);
}

.ts-badge-primary {
  background: rgba(37, 99, 235, 0.15);
  border: 1px solid rgba(37, 99, 235, 0.3);
  color: var(--ts-color-primary-light);
}

.ts-badge-primary:hover {
  box-shadow: var(--ts-shadow-glow-primary-sm);
}

.ts-badge-success {
  background: rgba(16, 185, 129, 0.15);
  border: 1px solid rgba(16, 185, 129, 0.3);
  color: var(--ts-color-secondary-light);
}

.ts-badge-accent {
  background: rgba(8, 145, 178, 0.15);
  border: 1px solid rgba(8, 145, 178, 0.3);
  color: var(--ts-color-accent-light);
}

.ts-badge-tertiary {
  background: rgba(139, 92, 246, 0.15);
  border: 1px solid rgba(139, 92, 246, 0.3);
  color: var(--ts-color-tertiary-light);
}

/* Glowing status dot */
.ts-badge .status-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: currentColor;
  box-shadow: 0 0 6px currentColor;
}
```

### Table with Hover Glow

```css
.ts-table {
  width: 100%;
  border-collapse: separate;
  border-spacing: 0;
}

.ts-table th {
  background: var(--ts-bg-elevated);
  font-family: var(--ts-font-display);
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--ts-color-text-muted);
  padding: var(--ts-space-5);
  text-align: left;
  border-bottom: 2px solid rgba(255, 255, 255, 0.08);
}

.ts-table td {
  font-family: var(--ts-font-display);
  padding: var(--ts-space-5);
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
  transition: all var(--ts-transition-fast);
}

.ts-table tbody tr {
  position: relative;
  transition: all var(--ts-transition-fast);
}

.ts-table tbody tr:hover {
  background: rgba(37, 99, 235, 0.06);
}

.ts-table tbody tr:hover td {
  border-bottom-color: rgba(37, 99, 235, 0.2);
}

.ts-table tbody tr:hover td:first-child {
  box-shadow: inset 3px 0 0 var(--ts-color-primary);
}
```

### Input with Glass & Focus Glow

```css
.ts-input {
  background: var(--ts-glass-bg-subtle);
  backdrop-filter: blur(var(--ts-blur-sm));
  -webkit-backdrop-filter: blur(var(--ts-blur-sm));
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: var(--ts-radius-md);
  color: var(--ts-color-text);
  font-family: var(--ts-font-display);
  padding: var(--ts-space-3) var(--ts-space-4);
  transition: all var(--ts-transition-normal);
}

.ts-input:hover {
  border-color: rgba(255, 255, 255, 0.15);
}

.ts-input:focus {
  outline: none;
  border-color: var(--ts-color-primary);
  box-shadow:
    0 0 0 3px rgba(37, 99, 235, 0.15),
    var(--ts-shadow-glow-primary-sm);
}
```

---

## Decorative Patterns

### Terminal Comment Prefix

```css
.metric-label::before {
  content: '// ';
  color: var(--ts-color-primary);
  opacity: 0.7;
}
```

### Terminal Prompt Prefix

```css
.ts-drawer-logo-text::before {
  content: '> ';
  color: var(--ts-color-secondary);
}
```

### Inline Code

```css
.ts-code {
  font-family: var(--ts-font-display);
  background: var(--ts-glass-bg);
  backdrop-filter: blur(var(--ts-blur-sm));
  border: var(--ts-glass-border-subtle);
  border-radius: var(--ts-radius-sm);
  padding: 2px 8px;
  color: var(--ts-color-primary-light);
}
```

---

## Accessibility

### Color Contrast

| Combination | Ratio | Compliance |
|-------------|-------|------------|
| Primary text (#F8FAFC) on dark (#0A0A0E) | 15.8:1 | WCAG AAA |
| Muted text (#94A3B8) on dark (#0A0A0E) | 6.4:1 | WCAG AA |
| Primary color (#2563EB) on dark (#0A0A0E) | 4.6:1 | WCAG AA (large text) |

### Reduced Motion

```css
@media (prefers-reduced-motion: reduce) {
  *,
  *::before,
  *::after {
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 0.01ms !important;
    scroll-behavior: auto !important;
  }

  .ts-stats-card:hover,
  .ts-btn-primary:hover {
    transform: none !important;
  }

  .ts-neon-pulse,
  .ts-text-gradient-animated,
  .ts-border-gradient-animated::before {
    animation: none !important;
  }
}
```

### Focus States

All interactive elements include visible focus rings with glow effects for keyboard navigation.

---

## Implementation Examples

### CSS Custom Properties (Complete)

```css
:root {
  /* ===== COLORS ===== */

  /* Primary */
  --ts-color-primary: #2563EB;
  --ts-color-primary-light: #3B82F6;
  --ts-color-primary-dark: #1D4ED8;
  --ts-color-primary-glow: #60A5FA;
  --ts-color-primary-rgb: 37, 99, 235;

  /* Secondary */
  --ts-color-secondary: #10B981;
  --ts-color-secondary-light: #34D399;
  --ts-color-secondary-dark: #059669;
  --ts-color-secondary-glow: #6EE7B7;
  --ts-color-secondary-rgb: 16, 185, 129;

  /* Accent */
  --ts-color-accent: #0891B2;
  --ts-color-accent-light: #22D3EE;
  --ts-color-accent-dark: #0E7490;
  --ts-color-accent-glow: #67E8F9;
  --ts-color-accent-rgb: 8, 145, 178;

  /* Tertiary */
  --ts-color-tertiary: #8B5CF6;
  --ts-color-tertiary-light: #A78BFA;
  --ts-color-tertiary-dark: #7C3AED;
  --ts-color-tertiary-glow: #C4B5FD;
  --ts-color-tertiary-rgb: 139, 92, 246;

  /* Semantic */
  --ts-color-info: #38BDF8;
  --ts-color-success: #10B981;
  --ts-color-warning: #FBBF24;
  --ts-color-error: #F43F5E;

  /* Backgrounds */
  --ts-bg-base: #050508;
  --ts-bg-surface: #0A0A0E;
  --ts-bg-elevated: #0C0C10;
  --ts-bg-card: #080810;

  /* Text */
  --ts-color-text: #F8FAFC;
  --ts-color-text-muted: #94A3B8;
  --ts-color-text-disabled: #475569;
  --ts-color-divider: #1A1A24;

  /* ===== TYPOGRAPHY ===== */
  --ts-font-display: 'Quantico', system-ui, -apple-system, sans-serif;

  /* ===== SPACING ===== */
  --ts-space-1: 0.25rem;
  --ts-space-2: 0.5rem;
  --ts-space-3: 0.75rem;
  --ts-space-4: 1rem;
  --ts-space-5: 1.25rem;
  --ts-space-6: 1.5rem;
  --ts-space-7: 1.75rem;
  --ts-space-8: 2rem;
  --ts-space-10: 2.5rem;
  --ts-space-12: 3rem;
  --ts-space-16: 4rem;
  --ts-space-20: 5rem;

  /* ===== BORDER RADIUS ===== */
  --ts-radius-none: 0px;
  --ts-radius-sm: 4px;
  --ts-radius-md: 6px;
  --ts-radius-lg: 8px;
  --ts-radius-xl: 12px;
  --ts-radius-2xl: 16px;
  --ts-radius-pill: 9999px;

  /* ===== SHADOWS ===== */

  /* Brutal */
  --ts-shadow-brutal-sm: 2px 2px 0 rgba(0, 0, 0, 0.5);
  --ts-shadow-brutal: 4px 4px 0 rgba(0, 0, 0, 0.6);
  --ts-shadow-brutal-lg: 6px 6px 0 rgba(0, 0, 0, 0.7);

  /* Soft */
  --ts-shadow-soft-sm: 0 2px 4px rgba(0,0,0,0.3), 0 1px 2px rgba(0,0,0,0.2);
  --ts-shadow-soft: 0 4px 6px rgba(0,0,0,0.35), 0 2px 4px rgba(0,0,0,0.25);
  --ts-shadow-soft-lg: 0 10px 15px rgba(0,0,0,0.4), 0 4px 6px rgba(0,0,0,0.3);

  /* Glow */
  --ts-shadow-glow-primary-sm: 0 0 10px rgba(37, 99, 235, 0.3);
  --ts-shadow-glow-primary: 0 0 20px rgba(37, 99, 235, 0.4), 0 0 40px rgba(37, 99, 235, 0.2);
  --ts-shadow-glow-accent: 0 0 20px rgba(8, 145, 178, 0.4), 0 0 40px rgba(8, 145, 178, 0.2);
  --ts-shadow-glow-tertiary: 0 0 20px rgba(139, 92, 246, 0.4), 0 0 40px rgba(139, 92, 246, 0.2);

  /* ===== GLASSMORPHISM ===== */
  --ts-blur-sm: 4px;
  --ts-blur-md: 8px;
  --ts-blur-lg: 12px;
  --ts-blur-xl: 16px;
  --ts-blur-2xl: 24px;

  --ts-glass-bg-subtle: rgba(255, 255, 255, 0.02);
  --ts-glass-bg: rgba(255, 255, 255, 0.06);
  --ts-glass-bg-strong: rgba(255, 255, 255, 0.08);

  --ts-glass-border: 1px solid rgba(255, 255, 255, 0.10);
  --ts-glass-border-strong: 1px solid rgba(255, 255, 255, 0.15);

  /* ===== GRADIENTS ===== */
  --ts-gradient-primary: linear-gradient(135deg, #3B82F6 0%, #2563EB 50%, #1D4ED8 100%);
  --ts-gradient-secondary: linear-gradient(135deg, #34D399 0%, #10B981 50%, #059669 100%);
  --ts-gradient-accent: linear-gradient(135deg, #22D3EE 0%, #0891B2 50%, #0E7490 100%);
  --ts-gradient-tertiary: linear-gradient(135deg, #A78BFA 0%, #8B5CF6 50%, #7C3AED 100%);
  --ts-gradient-rainbow: linear-gradient(135deg, #2563EB 0%, #8B5CF6 35%, #0891B2 70%, #10B981 100%);

  /* ===== TRANSITIONS ===== */
  --ts-transition-fast: 100ms ease-out;
  --ts-transition-normal: 150ms ease-out;
  --ts-transition-slow: 250ms ease-out;
  --ts-transition-spring: 300ms cubic-bezier(0.175, 0.885, 0.32, 1.275);
}
```

### React/Tailwind CSS

```jsx
// tailwind.config.js
module.exports = {
  theme: {
    extend: {
      colors: {
        primary: { DEFAULT: '#2563EB', light: '#3B82F6', dark: '#1D4ED8', glow: '#60A5FA' },
        secondary: { DEFAULT: '#10B981', light: '#34D399', dark: '#059669', glow: '#6EE7B7' },
        accent: { DEFAULT: '#0891B2', light: '#22D3EE', dark: '#0E7490', glow: '#67E8F9' },
        tertiary: { DEFAULT: '#8B5CF6', light: '#A78BFA', dark: '#7C3AED', glow: '#C4B5FD' },
        surface: '#0A0A0E',
        background: '#050508',
      },
      fontFamily: {
        display: ['Quantico', 'system-ui', 'sans-serif'],
      },
      borderRadius: {
        sm: '4px',
        DEFAULT: '6px',
        lg: '8px',
        xl: '12px',
        '2xl': '16px',
      },
      boxShadow: {
        brutal: '4px 4px 0 rgba(0, 0, 0, 0.6)',
        soft: '0 4px 6px rgba(0,0,0,0.35), 0 2px 4px rgba(0,0,0,0.25)',
        'glow-primary': '0 0 20px rgba(37, 99, 235, 0.4), 0 0 40px rgba(37, 99, 235, 0.2)',
      },
      backdropBlur: {
        xs: '4px',
        sm: '8px',
        DEFAULT: '12px',
        lg: '16px',
        xl: '24px',
      },
    },
  },
};

// Component example
function StatsCard({ label, value }) {
  return (
    <div className="relative bg-white/[0.06] backdrop-blur-[12px] border border-white/10
                    border-t-[3px] border-t-primary rounded-lg p-7 overflow-hidden
                    transition-all duration-150 hover:-translate-y-0.5 hover:border-primary/30
                    group">
      {/* Gradient overlay */}
      <div className="absolute inset-0 bg-gradient-to-br from-primary/5 to-transparent pointer-events-none" />

      {/* Ambient glow */}
      <div className="absolute inset-0 opacity-0 group-hover:opacity-100 transition-opacity
                      bg-radial-gradient from-primary/15 to-transparent blur-[20px] -z-10" />

      <span className="font-display text-xs uppercase tracking-wider text-muted">
        <span className="text-primary/70">// </span>{label}
      </span>
      <p className="font-display text-4xl font-bold bg-gradient-to-r from-primary-light via-primary to-primary-dark
                    bg-clip-text text-transparent">{value}</p>
    </div>
  );
}
```

### Astro

```astro
---
// src/styles/theme.css
---
<style is:global>
  :root {
    --ts-color-primary: #2563EB;
    --ts-color-tertiary: #8B5CF6;
    --ts-bg-surface: #0A0A0E;
    --ts-glass-bg: rgba(255, 255, 255, 0.06);
    --ts-blur-lg: 12px;
    --ts-gradient-primary: linear-gradient(135deg, #3B82F6, #2563EB, #1D4ED8);
    --ts-shadow-glow-primary-sm: 0 0 10px rgba(37, 99, 235, 0.3);
  }

  .ts-stats-card {
    background: var(--ts-glass-bg);
    backdrop-filter: blur(var(--ts-blur-lg));
    -webkit-backdrop-filter: blur(var(--ts-blur-lg));
    border: 1px solid rgba(255, 255, 255, 0.1);
    border-top: 3px solid var(--ts-color-primary);
    border-radius: 8px;
    padding: 1.75rem;
    transition: all 150ms ease-out;
  }

  .ts-stats-card:hover {
    transform: translateY(-2px);
    box-shadow: var(--ts-shadow-glow-primary-sm);
  }

  .metric-value {
    background: var(--ts-gradient-primary);
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
    background-clip: text;
  }
</style>
```

### Vue 3

```vue
<template>
  <div class="ts-stats-card">
    <span class="metric-label">{{ label }}</span>
    <p class="metric-value">{{ value }}</p>
  </div>
</template>

<style scoped>
.ts-stats-card {
  background: rgba(255, 255, 255, 0.06);
  backdrop-filter: blur(12px);
  -webkit-backdrop-filter: blur(12px);
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-top: 3px solid var(--ts-color-primary);
  border-radius: 8px;
  padding: 1.75rem;
  position: relative;
  overflow: hidden;
  transition: all 150ms ease-out;
}

.ts-stats-card::before {
  content: '';
  position: absolute;
  inset: 0;
  background: linear-gradient(135deg, rgba(37, 99, 235, 0.05), transparent);
  pointer-events: none;
}

.ts-stats-card:hover {
  transform: translateY(-2px);
  border-color: rgba(37, 99, 235, 0.3);
  box-shadow: 0 0 10px rgba(37, 99, 235, 0.3);
}

.metric-label {
  font-family: var(--ts-font-display);
  font-size: 0.6875rem;
  text-transform: uppercase;
  letter-spacing: 0.1em;
  color: var(--ts-color-text-muted);
}

.metric-label::before {
  content: '// ';
  color: var(--ts-color-primary);
  opacity: 0.7;
}

.metric-value {
  font-family: var(--ts-font-display);
  font-size: 2.25rem;
  font-weight: 700;
  background: linear-gradient(135deg, #3B82F6, #2563EB, #1D4ED8);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}
</style>
```

---

## Assets & Source Files

### Font Files

| Font | Weights | Format | Location |
|------|---------|--------|----------|
| Quantico | 400, 700 (+ italic) | woff2 | `wwwroot/fonts/quantico/` |

**Required files (self-hosted, NOT from CDN):**

- `Quantico-Regular.woff2`
- `Quantico-Bold.woff2`
- `Quantico-Italic.woff2`
- `Quantico-BoldItalic.woff2`

> **Important:** Quantico is the ONLY font allowed. Fonts MUST be self-hosted.

### Icons

Material Design Icons (recommended):

| Size | Pixels |
|------|--------|
| Small | 18-20px |
| Medium | 24px |
| Large | 32-40px |

### Source Files

| File | Description |
|------|-------------|
| `wwwroot/app.css` | Complete CSS design system |
| `TerraScaleDashboardTheme.cs` | MudBlazor theme configuration |
| `wwwroot/fonts/` | Self-hosted font files |
| `Pages/ThemeDemoPage.razor` | Live theme showcase |
