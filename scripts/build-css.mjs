// Bundles the dashboard's shared and feature styles into the one stylesheet
// served from web/static/css/terrascale.css.
//
//   node scripts/build-css.mjs [output]   minified, default web/static/css/terrascale.css
//   node scripts/build-css.mjs --dev      readable development bundle
//   node scripts/build-css.mjs --watch    readable, rebuilt on every source CSS change
//   node scripts/build-css.mjs --watch --minify  minified output with CSS watching
import { watch } from 'node:fs';
import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import browserslist from 'browserslist';
import { browserslistToTargets, bundle } from 'lightningcss';

const repository = fileURLToPath(new URL('../', import.meta.url));
const source = resolve(repository, 'web/assets');
const entry = resolve(source, 'css/terrascale.css');
const args = process.argv.slice(2);
const watchMode = args.includes('--watch');
const devMode = watchMode || args.includes('--dev');
const minify = args.includes('--minify') || !devMode;
const output = resolve(args.find(arg => !arg.startsWith('--')) ?? resolve(repository, 'web/static/css/terrascale.css'));

// Resolve the checked-in Browserslist query through the locked caniuse-lite
// database, then convert its browser/version pairs for lightningcss.
const targets = browserslistToTargets(browserslist(undefined, { path: repository }));

async function build() {
  const { code, warnings } = bundle({
    filename: entry,
    minify,
    targets,
    errorRecovery: false,
  });
  if (warnings.length > 0) {
    throw new Error(warnings.map(w => `${w.loc.filename}:${w.loc.line}:${w.loc.column} ${w.message}`).join('\n'));
  }

  const bundled = code;
  const current = await readFile(output).catch(() => null);
  if (!current?.equals(bundled)) {
    await mkdir(dirname(output), { recursive: true });
    // Keep the served asset valid while the development watcher rebuilds it.
    const temporary = `${output}.${process.pid}.tmp`;
    await writeFile(temporary, bundled);
    await rename(temporary, output);
  }
  return bundled.length;
}

const bytes = await build();
console.log(`CSS: ${bytes} bytes${minify ? '' : ' (dev)'} → ${output}`);

if (watchMode) {
  let timer;
  const rebuild = () => {
    clearTimeout(timer);
    timer = setTimeout(async () => {
      try {
        console.log(`CSS: rebuilt ${await build()} bytes`);
      } catch (error) {
        // Keep watching; the last good output stays in place.
        console.error(`CSS: build failed\n${error.message}`);
      }
    }, 30);
  };
  for (const dir of ['css', 'Features']) {
    watch(resolve(source, dir), { recursive: true }, (_, file) => file?.endsWith('.css') && rebuild());
  }
  console.log('CSS: watching web/assets/css/ and web/assets/Features/');
}
