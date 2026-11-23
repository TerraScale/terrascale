// i18n fallback sync for ts-docs (lenient policy).
// Usage: node scripts/i18n-sync.mjs [--check]
// Ensures every English page has a counterpart in every locale from
// docs/.vitepress/supportedLocales.mjs. Pages without a reviewed human
// translation ship the English body with a visible banner plus an
// `<!-- i18n:fallback:<locale> -->` marker. Removing the marker certifies
// a human-reviewed translation, after which sync leaves the file alone.
// --check exits non-zero when the tree differs from generated output.
import { readdirSync, readFileSync, statSync, writeFileSync, rmSync, mkdirSync } from 'node:fs';
import { dirname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { FALLBACK_BANNERS, LOCALES } from '../docs/.vitepress/supportedLocales.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const docs = join(root, 'docs');
const check = process.argv.includes('--check');
const localeCodes = new Set(LOCALES.map((l) => l.code).filter((c) => c !== 'en'));

const listMd = (dir) => {
  const out = [];
  const walk = (d) => {
    for (const e of readdirSync(d)) {
      const p = join(d, e);
      if (statSync(p).isDirectory()) {
        if (e === 'node_modules' || e === '.vitepress' || e === 'public') continue;
        if (d === dir && localeCodes.has(e)) continue; // locale trees live at root
        walk(p);
      } else if (e.endsWith('.md')) {
        out.push(relative(docs, p).split(sep).join('/'));
      }
    }
  };
  walk(dir);
  return out.sort();
};

const markerFor = (loc) => `<!-- i18n:fallback:${loc} -->`;

const rewriteLinks = (body, loc) => {
  const p = `/${loc}`;
  const rep = (m, open, path, close) => {
    if (/^\/(?:examples|brand|fonts)\//.test(path) || path === '/favicon.svg' || path === '/llms.txt') return m;
    if (path === '/') return `${open}${p}/${close}`;
    if (path === p || path.startsWith(`${p}/`)) return m;
    return `${open}${p}${path}${close}`;
  };
  body = body.replace(/(\]\()(\/[A-Za-z0-9_\-\.\/#?]*?)(\))/g, rep);
  body = body.replace(/(href=")(\/[A-Za-z0-9_\-\.\/#?]*)(")/g, rep);
  body = body.replace(/(src=")(\/[A-Za-z0-9_\-\.\/#?]*)(")/g, rep);
  // Home-hero frontmatter action links (`link: /path`).
  body = body.replace(/^(\s*link:\s*)(\/[A-Za-z0-9_\-\.\/#?]*?)(\s*)$/gm, (m, open, path, close) => {
    if (path === '/') return `${open}${p}/${close}`;
    if (path === p || path.startsWith(`${p}/`)) return m;
    return `${open}${p}${path}${close}`;
  });
  return body;
};

const buildFallback = (enText, loc, enRel) => {
  const m = enText.match(/^---\s*\n.*?\n---\s*\n?/s);
  const fm = m ? m[0] : '---\n---\n';
  let body = (m ? enText.slice(m[0].length) : enText).replace(/^\s+/, '');
  // Build-time `<<<` includes resolve relative to the including file, so a
  // locale copy would look under docs/<loc>/ and fail. Resolve them against
  // srcDir with the `@/` prefix (VitePress joins `@/...` onto srcDir).
  const base = enRel.includes('/') ? enRel.slice(0, enRel.lastIndexOf('/')) : '';
  body = body.replace(/^(\s*<<< *)(\.[^ \n]*)(.*)$/gm, (full, open, rel, rest) => {
    const parts = (base ? `${base}/` : '') + rel;
    const stack = [];
    for (const seg of parts.split('/')) {
      if (seg === '.' || seg === '') continue;
      if (seg === '..') stack.pop();
      else stack.push(seg);
    }
    if (stack.length === 0 || stack[0] === '..') return full; // escapes docs: leave alone
    return `${open}@/${stack.join('/')}${rest}`;
  });
  return `${fm}${markerFor(loc)}\n${FALLBACK_BANNERS[loc]}\n\n<div lang="en" dir="ltr">\n\n${body}\n</div>\n`;
};

let changed = [];
const enPages = listMd(docs);
for (const rel of enPages) {
  const enText = readFileSync(join(docs, rel), 'utf8');
  for (const loc of localeCodes) {
    const dst = join(docs, loc, rel);
    let cur = null;
    try {
      cur = readFileSync(dst, 'utf8');
    } catch {
      cur = null;
    }
    if (cur !== null && !cur.includes(markerFor(loc))) continue; // reviewed
    const want = rewriteLinks(buildFallback(enText, loc, rel), loc);
    if (cur === want) continue;
    changed.push(`${loc}/${rel}`);
    if (!check) {
      mkdirSync(dirname(dst), { recursive: true });
      writeFileSync(dst, want);
    }
  }
}

// Drop stale fallback copies whose English source was deleted; warn on
// reviewed orphans instead of deleting human work.
for (const loc of localeCodes) {
  let locFiles = [];
  try {
    locFiles = listMd(join(docs, loc)).map((r) => r.slice(loc.length + 1));
  } catch {
    continue;
  }
  const enSet = new Set(enPages);
  for (const rel of locFiles) {
    if (enSet.has(rel)) continue;
    const dst = join(docs, loc, rel);
    const cur = readFileSync(dst, 'utf8');
    if (cur.includes(markerFor(loc))) {
      changed.push(`deleted-stale:${loc}/${rel}`);
      if (!check) rmSync(dst);
    } else {
      console.warn(`warn: reviewed orphan without English source: ${loc}/${rel}`);
    }
  }
}

if (check && changed.length > 0) {
  console.error(`FAIL: i18n tree is stale (${changed.length}); run node scripts/i18n-sync.mjs`);
  for (const c of changed.slice(0, 20)) console.error(`  ${c}`);
  process.exit(1);
}
console.log(check ? 'i18n tree is up to date.' : `i18n sync: ${changed.length} file(s) written.`);
