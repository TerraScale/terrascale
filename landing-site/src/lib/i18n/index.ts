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

export const locale = writable<LocaleCode>(DEFAULT_LOCALE)

export const dict = derived(locale, ($l) => messages[$l] ?? messages[DEFAULT_LOCALE])

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
    return
  }
}

function syncDocumentLang(code: LocaleCode): void {
  if (typeof document === 'undefined') return
  document.documentElement.lang = localeMeta(code).bcp47
}

export { LOCALES, DEFAULT_LOCALE }
