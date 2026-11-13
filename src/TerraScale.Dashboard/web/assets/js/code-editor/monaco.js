// Monaco, configured for the dashboard: Shiki highlighting with the
// TerraScale theme, SQL and JSON grammars, and the JSON language service.
// Everything is bundled; nothing is fetched from a CDN at runtime.
import { createJavaScriptRegexEngine } from '@shikijs/engine-javascript';
import { init } from 'modern-monaco/core';
import css from 'tm-grammars/grammars/css.json';
import html from 'tm-grammars/grammars/html.json';
import javascript from 'tm-grammars/grammars/javascript.json';
import json from 'tm-grammars/grammars/json.json';
import sql from 'tm-grammars/grammars/sql.json';
import { theme } from './theme.js';

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
