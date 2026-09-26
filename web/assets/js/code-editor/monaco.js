// Monaco, configured for the dashboard: Shiki highlighting with the
// TerraScale theme, SQL and JSON grammars, and the JSON language service.
// Everything is bundled; nothing is fetched from a CDN at runtime.
import { createJavaScriptRegexEngine } from '@shikijs/engine-javascript';
import { init } from 'modern-monaco/core';
import json from 'tm-grammars/grammars/json.json';
import sql from 'tm-grammars/grammars/sql.json';
import { theme } from './theme.js';

export function load() {
  return init({
    theme,
    langs: [sql, json],
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
