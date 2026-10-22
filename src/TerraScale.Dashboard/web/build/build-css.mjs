// Bundles the dashboard's shared and feature styles into the one stylesheet
// served from web/static/css/terrascale.css.
//
//   node build/build-css.mjs [output]   minified, default web/static/css/terrascale.css
//   node build/build-css.mjs --dev      readable development bundle
//   node build/build-css.mjs --watch    readable, rebuilt on every source CSS change
//   node build/build-css.mjs --watch --minify  minified output with CSS watching
//   node build/build-css.mjs --release  minified, plus a maximum-quality .br
//                                         sibling served in place of the file
//   node build/build-css.mjs --check    exit 1 when the output differs from a fresh bundle
import { createHash } from 'node:crypto';
import { readFileSync, watch } from 'node:fs';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { brotliCompress, constants } from 'node:zlib';
import browserslist from 'browserslist';
import { browserslistToTargets, bundleAsync } from 'lightningcss';

const web = fileURLToPath(new URL('../', import.meta.url));
const source = resolve(web, 'assets');
const publicRoot = resolve(web, 'static');
const entry = resolve(source, 'css/terrascale.css');
const args = process.argv.slice(2);
const watchMode = args.includes('--watch');
const release = args.includes('--release');
const checkMode = args.includes('--check');
const devMode = watchMode || args.includes('--dev');
const minify = args.includes('--minify') || !devMode;
const output = resolve(args.find(arg => !arg.startsWith('--')) ?? resolve(publicRoot, 'css/terrascale.css'));

// Resolve the checked-in Browserslist query (web/.browserslistrc) through the
// locked caniuse-lite database, then convert its browser/version pairs for lightningcss.
const targets = browserslistToTargets(browserslist(undefined, { path: web }));

async function build() {
  // bundle() caches file contents inside the process, so a watcher would keep
  // emitting the first build; a resolver that always reads from disk avoids it.
  const { code, warnings } = await bundleAsync({
    filename: entry,
    minify,
    targets,
    errorRecovery: false,
    resolver: { read: file => readFile(file, 'utf8') },
    visitor: { Url: fingerprint },
  });
  if (warnings.length > 0) {
    throw new Error(warnings.map(w => `${w.loc.filename}:${w.loc.line}:${w.loc.column} ${w.message}`).join('\n'));
  }

  const bundled = code;
  const current = await readFile(output).catch(() => null);
  if (checkMode) {
    // A check only compares, so it leaves the stylesheet and its .br alone.
    if (!current?.equals(bundled)) {
      console.error(`CSS: ${output} is stale; run make css`);
      process.exit(1);
    }
    return bundled.length;
  }
  if (!current?.equals(bundled)) {
    await mkdir(dirname(output), { recursive: true });
    // Keep the served asset valid while the development watcher rebuilds it.
    const temporary = `${output}.${process.pid}.tmp`;
    await writeFile(temporary, bundled);
    await rename(temporary, output);
  }
  // A brotli sibling from an earlier release build would shadow this one.
  await (release ? precompress(output, bundled) : rm(`${output}.br`, { force: true }));
  return bundled.length;
}

// Root-relative url()s name files served from web/static. They get the
// content hash AssetUrls (Platform/Assets) appends, so the asset handler caches them as
// immutable and a changed file is fetched under a new URL.
function fingerprint(url) {
  if (!url.url.startsWith('/') || url.url.startsWith('//')) {
    return url;
  }
  // The file lookup needs the decoded path; the emitted URL keeps the
  // specifier as written so escapes, queries and fragments survive.
  const path = new URL(url.url, 'file:').pathname;
  let contents;
  try {
    contents = readFileSync(resolve(publicRoot, `.${path}`));
  } catch {
    throw new Error(`url(${url.url}) is not a file in web/static`);
  }
  // static.Version: the first six bytes of the SHA-256, in hex.
  const version = createHash('sha256').update(contents).digest('hex').slice(0, 12);
  const [resource, fragment] = url.url.split(/(?=#)/, 2);
  const separator = resource.includes('?') ? '&' : '?';
  return { ...url, url: `${resource}${separator}v=${version}${fragment ?? ''}` };
}

// Brotli at quality 11 is too slow to run per request but shrinks the
// stylesheet by a quarter over the server's dynamic compression.
const compress = promisify(brotliCompress);
async function precompress(file, contents) {
  const compressed = await compress(contents, {
    params: {
      [constants.BROTLI_PARAM_QUALITY]: constants.BROTLI_MAX_QUALITY,
      [constants.BROTLI_PARAM_SIZE_HINT]: contents.length,
    },
  });
  // Swap it in whole, like the stylesheet itself, so the server never sends a
  // partly written brotli stream.
  const temporary = `${file}.br.${process.pid}.tmp`;
  await writeFile(temporary, compressed);
  await rename(temporary, `${file}.br`);
}

const bytes = await build();
console.log(`CSS: ${bytes} bytes${minify ? '' : ' (dev)'} ${checkMode ? 'match' : '→'} ${output}`);

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
  for (const dir of [resolve(source, 'css'), resolve(web, '../Features')]) {
    watch(dir, { recursive: true }, (_, file) => file?.endsWith('.css') && rebuild());
  }
  console.log('CSS: watching web/assets/css/ and src/TerraScale.Dashboard/Features/');
}
