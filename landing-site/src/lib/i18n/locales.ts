export type LocaleCode = 'en'

export type LocaleMeta = {
  code: LocaleCode
  label: string
  short: string
  bcp47: string
}

export const LOCALES: LocaleMeta[] = [
  { code: 'en', label: 'English', short: 'EN', bcp47: 'en' },
]

export const DEFAULT_LOCALE: LocaleCode = 'en'

export const isLocale = (value: unknown): value is LocaleCode =>
  typeof value === 'string' && LOCALES.some((l) => l.code === value)

export const localeMeta = (code: LocaleCode): LocaleMeta =>
  LOCALES.find((l) => l.code === code) ?? LOCALES[0]
