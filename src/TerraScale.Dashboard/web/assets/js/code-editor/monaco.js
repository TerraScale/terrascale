// Monaco, configured for the dashboard: Shiki highlighting with the
// TerraScale theme, SQL and JSON grammars, and the JSON language service.
// Everything is bundled; nothing is fetched from a CDN at runtime.
import { createJavaScriptRegexEngine } from '@shikijs/engine-javascript';
import { init } from 'modern-monaco/core';
import json from 'tm-grammars/grammars/json.json';
import sql from 'tm-grammars/grammars/sql.json';
import { theme } from './theme.js';

const keyword = '\\b(?:async|await|break|case|catch|class|const|continue|default|else|export|extends|finally|for|from|function|if|import|let|new|of|return|static|switch|throw|try|var|while|yield)\\b';

const javascript = {
  name: 'javascript',
  scopeName: 'source.js',
  patterns: [{ include: '#code' }],
  repository: {
    code: {
      patterns: [
        { name: 'comment.line.double-slash.js', match: '//.*' },
        { name: 'comment.block.js', begin: '/\\*', end: '\\*/' },
        { name: 'string.quoted.double.js', match: '"(?:\\\\.|[^"\\\\])*"' },
        { name: 'string.quoted.single.js', match: "'(?:\\\\.|[^'\\\\])*'" },
        { name: 'string.template.js', begin: '`', end: '`' },
        { name: 'keyword.control.js', match: keyword },
        { name: 'constant.numeric.js', match: '\\b\\d+(?:\\.\\d+)?\\b' },
      ],
    },
  },
};

const html = { name: 'html', scopeName: 'text.html.basic', patterns: [{ match: '\\b\\B' }] };
const css = { name: 'css', scopeName: 'source.css', patterns: [{ match: '\\b\\B' }] };

export function load() {
  return init({
    theme,
    langs: [sql, json, javascript, html, css],
    engine: createJavaScriptRegexEngine(),
    lsp: {
      providers: {
        json: { import: () => import('modern-monaco/lsp/json/setup') },
      },
      json: { importMapCodeLens: false },
      formatting: { tabSize: 2, insertSpaces: true },
    },
  });
}
