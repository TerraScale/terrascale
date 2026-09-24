// Bundles src/TerraScale.Dashboard/Styles/terrascale.css (and every partial it
// @imports) into the single stylesheet the dashboard serves.
//
//   node scripts/build-css.mjs [output]   minified, default wwwroot/css/terrascale.css
//   node scripts/build-css.mjs --dev      readable (no source map: dotnet watch reloads the
//                                         whole page when a non-CSS asset such as a .map changes)
//   node scripts/build-css.mjs --watch    --dev, rebuilt on every Styles/ or Features/ .css change
import { watch } from 'node:fs';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import browserslist from 'browserslist';
import { browserslistToTargets, bundle } from 'lightningcss';

const project = fileURLToPath(new URL('../src/TerraScale.Dashboard/', import.meta.url));
const entry = resolve(project, 'Styles/terrascale.css');
const repository = resolve(project, '../..');
const args = process.argv.slice(2);
const watchMode = args.includes('--watch');
const dev = watchMode || args.includes('--dev');
const output = resolve(args.find(arg => !arg.startsWith('--')) ?? resolve(project, 'wwwroot/css/terrascale.css'));

// Resolve the checked-in Browserslist query through the locked caniuse-lite
// database, then convert its browser/version pairs for lightningcss.
const targets = browserslistToTargets(browserslist(undefined, { path: repository }));

async function build() {
  const { code, warnings } = bundle({
    filename: entry,
    minify: !dev,
    targets,
    errorRecovery: false,
  });
  if (warnings.length > 0) {
    throw new Error(warnings.map(w => `${w.loc.filename}:${w.loc.line}:${w.loc.column} ${w.message}`).join('\n'));
  }

  // An unchanged file is left alone so dotnet watch has nothing to push.
  // Whitespace after a transition-list comma is optional; compacting it keeps
  // the reorganized bundle at or below the pre-split output size.
  const bundled = dev
    ? code
    : Buffer.from(code.toString().replace(/var\(--ts-t-control\) var\(--ts-ease\), /g, 'var(--ts-t-control) var(--ts-ease),'));
  const current = await readFile(output).catch(() => null);
  if (!current?.equals(bundled)) {
    await mkdir(dirname(output), { recursive: true });
    await writeFile(output, bundled);
  }
  return bundled.length;
}

const bytes = await build();
console.log(`CSS: ${bytes} bytes${dev ? ' (dev)' : ''} → ${output}`);

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
  for (const dir of ['Styles', 'Features']) {
    watch(resolve(project, dir), { recursive: true }, (_, file) => file?.endsWith('.css') && rebuild());
  }
  console.log('CSS: watching Styles/ and Features/');
}
