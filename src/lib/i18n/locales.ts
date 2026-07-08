// Locale catalogue. Codes mirror the documentation site's hreflang set
// (https://docs.terrascale.tech/{en,es,pt-br}/) so the two sites agree on how
// a language is identified. Each entry is shown in its own endonym in the
// selector — the convention used on the docs site.

export type LocaleCode = 'en' | 'es' | 'pt-br'

export type LocaleMeta = {
  /** Stable identifier persisted in localStorage and used as the store value. */
  code: LocaleCode
  /** Endonym shown in the selector (e.g. "Español", not "Spanish"). */
  label: string
  /** Compact code shown on the selector trigger. */
  short: string
  /** BCP-47 tag applied to <html lang>. */
  bcp47: string
}

export const LOCALES: LocaleMeta[] = [
  { code: 'en', label: 'English', short: 'EN', bcp47: 'en' },
  { code: 'es', label: 'Español', short: 'ES', bcp47: 'es' },
  { code: 'pt-br', label: 'Português (Brasil)', short: 'PT', bcp47: 'pt-BR' },
]

export const DEFAULT_LOCALE: LocaleCode = 'en'

export const isLocale = (value: unknown): value is LocaleCode =>
  typeof value === 'string' && LOCALES.some((l) => l.code === value)

export const localeMeta = (code: LocaleCode): LocaleMeta =>
  LOCALES.find((l) => l.code === code) ?? LOCALES[0]
