// TerraScale editor theme, in VS Code theme format so Shiki highlights the
// code and Monaco styles the chrome. Every colour is a --ts-* token from
// web/assets/css/tokens.css: surfaces and ink for the frame, the accent for
// focus and selection, and the --ts-syn-* tokens for syntax. This module loads
// after the page, so the tokens are read from the live stylesheet.

// Tokens are light-dark() pairs, so resolve each one through a probe's
// computed colour into the hex Monaco and Shiki expect.
const probe = document.createElement('span');
probe.hidden = true;
document.body.append(probe);
const hex = value => Math.round(value).toString(16).padStart(2, '0');
const token = name => {
  probe.style.color = `var(${name})`;
  const [r, g, b, a = 1] = getComputedStyle(probe).color.match(/[\d.]+/g).map(Number);
  return `#${hex(r)}${hex(g)}${hex(b)}${a < 1 ? hex(a * 255) : ''}`;
};
const alpha = (color, amount) => color.slice(0, 7) + hex(amount * 255);

const ink = token('--ts-ink');
const ink2 = token('--ts-ink-2');
const ink3 = token('--ts-ink-3');
const surface = token('--ts-surface');
const surface2 = token('--ts-surface-2');
const surface3 = token('--ts-surface-3');
const line = token('--ts-line');
const lineStrong = token('--ts-line-strong');
const accent = token('--ts-accent');
const link = token('--ts-link');
const shadow = token('--ts-shadow');
const danger = token('--ts-danger');
const warning = token('--ts-warning');
const info = token('--ts-info');
const syn = {
  keyword: token('--ts-syn-keyword'),
  string: token('--ts-syn-string'),
  number: token('--ts-syn-number'),
  comment: token('--ts-syn-comment'),
  fn: token('--ts-syn-fn'),
  punct: token('--ts-syn-punct'),
};
const series2 = token('--ts-chart-2');
const series3 = token('--ts-cat-3');
probe.remove();

export const theme = {
  name: 'terrascale',
  type: 'dark',
  colors: {
    focusBorder: accent,
    foreground: ink,
    descriptionForeground: ink2,
    errorForeground: danger,
    'widget.shadow': shadow,
    'widget.border': lineStrong,

    'editor.background': surface,
    'editor.foreground': ink,
    'editorCursor.foreground': link,
    'editor.lineHighlightBackground': alpha(ink, 0.04),
    'editor.lineHighlightBorder': '#00000000',
    'editor.selectionBackground': alpha(accent, 0.3),
    'editor.inactiveSelectionBackground': alpha(accent, 0.15),
    'editor.selectionHighlightBackground': alpha(accent, 0.12),
    'editor.wordHighlightBackground': alpha(accent, 0.12),
    'editor.wordHighlightStrongBackground': alpha(accent, 0.2),
    'editor.findMatchBackground': alpha(warning, 0.25),
    'editor.findMatchHighlightBackground': alpha(warning, 0.12),
    'editor.foldBackground': alpha(accent, 0.08),
    'editorLineNumber.foreground': ink3,
    'editorLineNumber.activeForeground': ink2,
    'editorIndentGuide.background1': line,
    'editorIndentGuide.activeBackground1': lineStrong,
    'editorWhitespace.foreground': lineStrong,
    'editorRuler.foreground': line,
    'editorBracketMatch.background': alpha(accent, 0.15),
    'editorBracketMatch.border': alpha(accent, 0.5),
    'editorBracketHighlight.foreground1': link,
    'editorBracketHighlight.foreground2': series3,
    'editorBracketHighlight.foreground3': series2,
    'editorBracketHighlight.unexpectedBracket.foreground': danger,
    'editorGutter.background': surface,
    'editorError.foreground': danger,
    'editorWarning.foreground': warning,
    'editorInfo.foreground': info,
    'editorLink.activeForeground': link,
    'editorOverviewRuler.border': '#00000000',
    'editorStickyScroll.background': surface,
    'editorStickyScrollHover.background': surface2,

    'editorWidget.background': surface2,
    'editorWidget.foreground': ink,
    'editorWidget.border': lineStrong,
    'editorHoverWidget.background': surface2,
    'editorHoverWidget.border': lineStrong,
    'editorSuggestWidget.background': surface2,
    'editorSuggestWidget.border': lineStrong,
    'editorSuggestWidget.foreground': ink2,
    'editorSuggestWidget.highlightForeground': link,
    'editorSuggestWidget.selectedBackground': alpha(accent, 0.16),
    'editorSuggestWidget.selectedForeground': ink,
    'list.hoverBackground': surface3,
    'list.activeSelectionBackground': alpha(accent, 0.16),
    'list.activeSelectionForeground': ink,
    'list.highlightForeground': link,
    'quickInput.background': surface2,
    'quickInputList.focusBackground': alpha(accent, 0.16),
    'input.background': surface,
    'input.border': lineStrong,
    'input.foreground': ink,
    'inputOption.activeBorder': accent,
    'menu.background': surface2,
    'menu.foreground': ink,
    'menu.selectionBackground': alpha(accent, 0.16),
    'menu.separatorBackground': line,
    'scrollbar.shadow': '#00000000',
    'scrollbarSlider.background': lineStrong,
    'scrollbarSlider.hoverBackground': alpha(ink3, 0.4),
    'scrollbarSlider.activeBackground': alpha(ink3, 0.55),
  },
  tokenColors: [
    { settings: { foreground: ink } },
    {
      scope: ['comment', 'punctuation.definition.comment'],
      settings: { foreground: syn.comment, fontStyle: 'italic' },
    },
    {
      scope: ['keyword', 'storage', 'keyword.other.DML', 'keyword.other.DDL', 'keyword.other.order', 'keyword.other.alias'],
      settings: { foreground: syn.keyword },
    },
    {
      scope: ['keyword.operator', 'keyword.operator.logical', 'keyword.operator.comparison', 'keyword.operator.math', 'keyword.operator.star'],
      settings: { foreground: syn.keyword },
    },
    {
      scope: ['string', 'string.quoted', 'punctuation.definition.string'],
      settings: { foreground: syn.string },
    },
    {
      scope: ['constant.character.escape', 'string.regexp'],
      settings: { foreground: syn.string },
    },
    {
      scope: ['constant.numeric', 'constant.other.number'],
      settings: { foreground: syn.number },
    },
    {
      scope: ['constant.language', 'constant.language.boolean', 'constant.language.null', 'constant.other'],
      settings: { foreground: syn.number },
    },
    {
      scope: ['support.type.property-name.json', 'support.type.property-name', 'meta.object-literal.key'],
      settings: { foreground: syn.keyword },
    },
    {
      scope: ['entity.name.function', 'support.function', 'support.function.aggregate', 'meta.function-call'],
      settings: { foreground: syn.fn },
    },
    {
      scope: ['storage.type', 'support.type', 'entity.name.type'],
      settings: { foreground: syn.keyword },
    },
    {
      scope: ['variable.parameter', 'variable.other.placeholder', 'text.variable'],
      settings: { foreground: syn.number },
    },
    {
      scope: ['entity.name', 'constant.other.database-name', 'constant.other.table-name', 'variable'],
      settings: { foreground: ink },
    },
    {
      scope: ['punctuation', 'meta.brace', 'punctuation.separator', 'punctuation.terminator'],
      settings: { foreground: syn.punct },
    },
    {
      scope: ['invalid', 'invalid.illegal'],
      settings: { foreground: danger },
    },
  ],
};
