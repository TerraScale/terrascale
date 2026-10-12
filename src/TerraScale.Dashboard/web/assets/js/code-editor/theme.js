// TerraScale editor theme, in VS Code theme format so Shiki highlights the
// code and Monaco styles the chrome. Colors come from web/assets/css/tokens.css:
// surfaces and ink for the frame, the accent blue for focus and selection, and
// the section tones for syntax.
const ink = '#eef1f7';
const ink2 = '#a7afbf';
const ink3 = '#6c7589';
const surface = '#11141c';
const surface2 = '#161a24';
const surface3 = '#1e2330';
const line = '#94a3b81f';
const lineStrong = '#94a3b838';
const accent = '#3b82f6';
const link = '#6ea8ff';

const tone = {
  violet: '#a78bfa',
  blue: '#60a5fa',
  cyan: '#22d3ee',
  teal: '#2dd4bf',
  emerald: '#34d399',
  amber: '#fbbf24',
  orange: '#fb923c',
  pink: '#f472b6',
  indigo: '#a5b4fc',
  danger: '#f87171',
};

export const theme = {
  name: 'terrascale',
  type: 'dark',
  colors: {
    focusBorder: accent,
    foreground: ink,
    descriptionForeground: ink2,
    errorForeground: tone.danger,
    'widget.shadow': '#00000099',
    'widget.border': lineStrong,

    'editor.background': surface,
    'editor.foreground': ink,
    'editorCursor.foreground': link,
    'editor.lineHighlightBackground': '#94a3b80d',
    'editor.lineHighlightBorder': '#00000000',
    'editor.selectionBackground': '#3b82f64d',
    'editor.inactiveSelectionBackground': '#3b82f626',
    'editor.selectionHighlightBackground': '#3b82f61f',
    'editor.wordHighlightBackground': '#3b82f61f',
    'editor.wordHighlightStrongBackground': '#3b82f633',
    'editor.findMatchBackground': '#fbbf2440',
    'editor.findMatchHighlightBackground': '#fbbf241f',
    'editor.foldBackground': '#3b82f614',
    'editorLineNumber.foreground': '#454d5f',
    'editorLineNumber.activeForeground': ink2,
    'editorIndentGuide.background1': line,
    'editorIndentGuide.activeBackground1': lineStrong,
    'editorWhitespace.foreground': '#94a3b833',
    'editorRuler.foreground': line,
    'editorBracketMatch.background': '#3b82f626',
    'editorBracketMatch.border': '#3b82f680',
    'editorBracketHighlight.foreground1': link,
    'editorBracketHighlight.foreground2': tone.violet,
    'editorBracketHighlight.foreground3': tone.cyan,
    'editorBracketHighlight.unexpectedBracket.foreground': tone.danger,
    'editorGutter.background': surface,
    'editorError.foreground': tone.danger,
    'editorWarning.foreground': tone.amber,
    'editorInfo.foreground': tone.blue,
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
    'editorSuggestWidget.selectedBackground': '#3b82f629',
    'editorSuggestWidget.selectedForeground': ink,
    'list.hoverBackground': surface3,
    'list.activeSelectionBackground': '#3b82f629',
    'list.activeSelectionForeground': ink,
    'list.highlightForeground': link,
    'quickInput.background': surface2,
    'quickInputList.focusBackground': '#3b82f629',
    'input.background': surface,
    'input.border': lineStrong,
    'input.foreground': ink,
    'inputOption.activeBorder': accent,
    'menu.background': surface2,
    'menu.foreground': ink,
    'menu.selectionBackground': '#3b82f629',
    'menu.separatorBackground': line,
    'scrollbar.shadow': '#00000000',
    'scrollbarSlider.background': '#94a3b829',
    'scrollbarSlider.hoverBackground': '#94a3b847',
    'scrollbarSlider.activeBackground': '#94a3b866',
  },
  tokenColors: [
    { settings: { foreground: ink } },
    {
      scope: ['comment', 'punctuation.definition.comment'],
      settings: { foreground: ink3, fontStyle: 'italic' },
    },
    {
      scope: ['keyword', 'storage', 'keyword.other.DML', 'keyword.other.DDL', 'keyword.other.order', 'keyword.other.alias'],
      settings: { foreground: tone.violet },
    },
    {
      scope: ['keyword.operator', 'keyword.operator.logical', 'keyword.operator.comparison', 'keyword.operator.math', 'keyword.operator.star'],
      settings: { foreground: tone.cyan },
    },
    {
      scope: ['string', 'string.quoted', 'punctuation.definition.string'],
      settings: { foreground: tone.emerald },
    },
    {
      scope: ['constant.character.escape', 'string.regexp'],
      settings: { foreground: tone.teal },
    },
    {
      scope: ['constant.numeric', 'constant.other.number'],
      settings: { foreground: tone.amber },
    },
    {
      scope: ['constant.language', 'constant.language.boolean', 'constant.language.null', 'constant.other'],
      settings: { foreground: tone.orange },
    },
    {
      scope: ['support.type.property-name.json', 'support.type.property-name', 'meta.object-literal.key'],
      settings: { foreground: link },
    },
    {
      scope: ['entity.name.function', 'support.function', 'support.function.aggregate', 'meta.function-call'],
      settings: { foreground: tone.indigo },
    },
    {
      scope: ['storage.type', 'support.type', 'entity.name.type'],
      settings: { foreground: tone.teal },
    },
    {
      scope: ['variable.parameter', 'variable.other.placeholder', 'text.variable'],
      settings: { foreground: tone.pink },
    },
    {
      scope: ['entity.name', 'constant.other.database-name', 'constant.other.table-name', 'variable'],
      settings: { foreground: ink },
    },
    {
      scope: ['punctuation', 'meta.brace', 'punctuation.separator', 'punctuation.terminator'],
      settings: { foreground: ink2 },
    },
    {
      scope: ['invalid', 'invalid.illegal'],
      settings: { foreground: tone.danger },
    },
  ],
};
