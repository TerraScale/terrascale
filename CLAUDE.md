# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
bun install          # Install dependencies
bun run lint         # Run ESLint - ALWAYS run before committing
bun run build        # Production build to dist/
bun run dev          # Dev server at http://localhost:5173/
bun run preview      # Preview production build at http://localhost:4173/
```

Add ShadCN components:
```bash
bunx shadcn@latest add <component> -y -o
```

## Architecture

React 19 SPA with Vite 7, TypeScript, Tailwind CSS 4, and React Router 7.

**Routing**: `src/App.tsx` defines routes:
- `/` → Home, `/about` → About, `/pricing` → Pricing, `/contact` → Contact, `*` → NotFound

**Structure**:
- `src/pages/` - Route page components (Home, About, Contact, Pricing, NotFound)
- `src/components/` - Shared components (Header, Footer, ScrollToTop, ContactModal)
- `src/components/ui/` - ShadCN UI components (button, accordion)
- `src/lib/utils.ts` - `cn()` helper for conditional Tailwind classes

**Path Alias**: `@/*` maps to `./src/*`

## Validation Workflow

1. Run `bun run lint` and fix errors
2. Run `bun run build` to verify production build
3. Test with `bun run dev` - check all routes: `/`, `/about`, `/pricing`, `/contact`

## ESLint Note

Unused variables cause lint errors. Fix by removing them or prefixing with underscore (pattern `^[A-Z_]` is ignored).
