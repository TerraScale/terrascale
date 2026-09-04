// i18n coverage report for ts-docs (informational, never fails).
// Usage: node scripts/i18n-status.mjs
// Counts reviewed pages (no marker) vs English-fallback pages
// (`<!-- i18n:fallback -->`) per locale. Lenient policy: this report is
// visibility only; CI does not gate on coverage.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { LOCALES } from '../docs/.vitepress/supportedLocales.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const docs = join(root, 'docs');

const listMd = (dir) => {
  const out = [];
  const walk = (d) => {
    for (const e of readdirSync(d)) {
      const p = join(d, e);
      if (statSync(p).isDirectory()) {
        if (e === 'node_modules' || e === '.vitepress' || e === 'public') continue;
        walk(p);
      } else if (e.endsWith('.md')) out.push(p);
    }
  };
  try {
    walk(dir);
  } catch {
    return [];
  }
  return out;
};

const enTotal = listMd(docs).filter(
  (p) => !LOCALES.some((l) => l.code !== 'en' && relative(docs, p).split(sep)[0] === l.code),
).length;

console.log(`English source pages: ${enTotal}\n`);
console.log('locale      reviewed  fallback  missing   coverage');
for (const { code } of LOCALES.filter((l) => l.code !== 'en')) {
  const files = listMd(join(docs, code));
  let reviewed = 0;
  for (const f of files) {
    if (!readFileSync(f, 'utf8').includes(`<!-- i18n:fallback:${code} -->`)) reviewed += 1;
  }
  const fallback = files.length - reviewed;
  const missing = Math.max(0, enTotal - files.length);
  const pct = enTotal === 0 ? 100 : Math.round((reviewed / enTotal) * 100);
  console.log(
    `${code.padEnd(11)} ${String(reviewed).padStart(8)} ${String(fallback).padStart(9)} ${String(missing).padStart(8)}   ${pct}% reviewed`,
  );
}
