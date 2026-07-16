// Client-side internationalisation for the static landing page.
//
// Why client-side (reactive) and not path-based like the docs site: the landing
// page is prerendered to a fixed route set (see CLAUDE.md — "do not add routes
// without an explicit request"), so locale switching swaps content in place via
// a reactive store rather than navigating to /es, /pt-br, etc. The user-facing
// behaviour is identical to the docs selector: pick a language, the page
// content updates and the choice is remembered.
//
// SSR safety: the store initialises to DEFAULT_LOCALE so prerendered HTML is
// deterministic English. The saved/browser locale is resolved on mount
// (initLocale), after hydration, so there is no hydration mismatch.

import { writable, derived, get } from 'svelte/store'
import { en, type Messages } from './messages/en'
import { ptBr } from './messages/pt-br'
import { es } from './messages/es'
import {
  LOCALES,
  DEFAULT_LOCALE,
  isLocale,
  localeMeta,
  type LocaleCode,
  type LocaleMeta,
} from './locales'

export type { LocaleCode, LocaleMeta, Messages }

const STORAGE_KEY = 'ts-locale'
const PRECOMPUTED_KEY = '__TS_LOCALE__'

const messages: Record<LocaleCode, Messages> = {
  en,
  es,
  'pt-br': ptBr,
}

/** Active locale. Starts at DEFAULT_LOCALE on both server and client. */
export const locale = writable<LocaleCode>(DEFAULT_LOCALE)

/**
 * Full message object for the active locale. Components bind to this (e.g.
 * `$dict.hero.subtitle`) so structured copy (arrays of cards, FAQ entries)
 * switches reactively without per-string wiring.
 */
export const dict = derived(locale, ($l) => messages[$l] ?? messages[DEFAULT_LOCALE])

/**
 * Resolve the user's locale and apply it. Call once on mount from the layout.
 * Order of precedence:
 *   1. value precomputed by the inline script in app.html (handles the
 *      pre-paint <html lang> and avoids re-reading storage off the main path),
 *   2. localStorage (returning visitor's explicit choice),
 *   3. navigator language (first-visit best guess),
 *   4. DEFAULT_LOCALE.
 */
export function initLocale(): void {
  if (typeof window === 'undefined') return
  const resolved = readPrecomputed() ?? readStored() ?? detectBrowser() ?? DEFAULT_LOCALE
  if (resolved !== DEFAULT_LOCALE) locale.set(resolved)
  syncDocumentLang(resolved)
}

export function setLocale(code: LocaleCode): void {
  locale.set(code)
  persist(code)
  syncDocumentLang(code)
}

/** Dotted-key accessor for scalar strings used outside a component context. */
export function t(key: string): string {
  const parts = key.split('.')
  let cur: unknown = get(dict)
  for (const p of parts) {
    if (cur && typeof cur === 'object' && p in (cur as Record<string, unknown>)) {
      cur = (cur as Record<string, unknown>)[p]
    } else {
      return key
    }
  }
  return typeof cur === 'string' ? cur : key
}

function readPrecomputed(): LocaleCode | null {
  return isLocale((window as unknown as Record<string, unknown>)[PRECOMPUTED_KEY])
    ? ((window as unknown as Record<string, unknown>)[PRECOMPUTED_KEY] as LocaleCode)
    : null
}

function readStored(): LocaleCode | null {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY)
    return isLocale(stored) ? stored : null
  } catch {
    return null
  }
}

function detectBrowser(): LocaleCode | null {
  if (typeof navigator === 'undefined') return null
  const langs = navigator.languages ?? [navigator.language]
  for (const l of langs) {
    const norm = (l ?? '').toLowerCase()
    if (norm.startsWith('pt')) return 'pt-br'
    if (norm.startsWith('es')) return 'es'
    if (norm.startsWith('en')) return 'en'
  }
  return null
}

function persist(code: LocaleCode): void {
  if (typeof window === 'undefined') return
  try {
    window.localStorage.setItem(STORAGE_KEY, code)
  } catch {
    /* storage blocked — the choice holds for the session only */
  }
}

function syncDocumentLang(code: LocaleCode): void {
  if (typeof document === 'undefined') return
  document.documentElement.lang = localeMeta(code).bcp47
}

export { LOCALES, DEFAULT_LOCALE }
