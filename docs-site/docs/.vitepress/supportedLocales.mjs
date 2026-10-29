// First-class locale registry for ts-docs (VitePress i18n).
// Single source of truth shared by docs/.vitepress/config.ts (via import),
// scripts/i18n-sync.mjs (content port + English fallback), and
// scripts/i18n-status.mjs (coverage report).
//
// Policy (lenient): every English page has a counterpart in every locale.
// Pages without a reviewed human translation ship the English body with a
// visible banner in the reader's language plus an `<!-- i18n:fallback -->`
// marker. Removing the marker certifies a human-reviewed translation.
// CI never fails on coverage; run `npm run i18n:status` to see the table.
//
// Locales: en (root, no prefix) plus 9 prefixed locales covering the most
// common world languages and the GRU/SJC launch regions.

export const LOCALES = [
  { code: 'en', label: 'English', lang: 'en-US', dir: 'ltr', prefix: '' },
  { code: 'es', label: 'Español', lang: 'es', dir: 'ltr', prefix: 'es' },
  {
    code: 'pt-br',
    label: 'Português (Brasil)',
    lang: 'pt-BR',
    dir: 'ltr',
    prefix: 'pt-br',
  },
  { code: 'fr', label: 'Français', lang: 'fr', dir: 'ltr', prefix: 'fr' },
  { code: 'de', label: 'Deutsch', lang: 'de', dir: 'ltr', prefix: 'de' },
  { code: 'zh-cn', label: '简体中文', lang: 'zh-CN', dir: 'ltr', prefix: 'zh-cn' },
  { code: 'hi', label: 'हिन्दी', lang: 'hi', dir: 'ltr', prefix: 'hi' },
  { code: 'ar', label: 'العربية', lang: 'ar', dir: 'rtl', prefix: 'ar' },
  { code: 'ru', label: 'Русский', lang: 'ru', dir: 'ltr', prefix: 'ru' },
  { code: 'ja', label: '日本語', lang: 'ja', dir: 'ltr', prefix: 'ja' },
];

// Banner prepended (after frontmatter) to every unreviewed fallback page.
// No em dashes: commas, colons, or periods only.
export const FALLBACK_BANNERS = {
  es: '> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.',
  'pt-br':
    '> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.',
  fr: '> **Traduction en attente.** Cette page n\u2019a pas encore été relue en français et affiche le contenu original en anglais.',
  de: '> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.',
  'zh-cn': '> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。',
  hi: '> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।',
  ar: '> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.',
  ru: '> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.',
  ja: '> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。',
};
