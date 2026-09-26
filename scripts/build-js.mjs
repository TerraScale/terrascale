// Bundles the dashboard's browser scripts into web/static/js/. The code editor
// entry stays tiny; Monaco, its workers, and the codicon font are emitted as
// content-hashed siblings that the entry downloads once the page has loaded.
//
//   node scripts/build-js.mjs             minified bundle
//   node scripts/build-js.mjs --release   minified, plus maximum-quality .br
//                                         siblings served in place of the files
//   node scripts/build-js.mjs --watch     readable, rebuilt on every source change
//
// modern-monaco fetches its editor core, grammars, and themes from esm.sh by
// default. The plugins below rewrite those loads so every byte is self-hosted
// and fits the dashboard's Content-Security-Policy.
import { createHash } from 'node:crypto';
import { watch } from 'node:fs';
import { mkdir, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { basename, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { brotliCompress, constants } from 'node:zlib';
import * as esbuild from 'esbuild';

const repository = fileURLToPath(new URL('../', import.meta.url));
const source = resolve(repository, 'web/assets/js');
const outdir = resolve(repository, 'web/static/js');
const monacoDist = resolve(repository, 'node_modules/modern-monaco/dist');
const watchMode = process.argv.includes('--watch');
const release = process.argv.includes('--release');

const shared = {
  bundle: true,
  format: 'esm',
  target: 'es2022',
  platform: 'browser',
  minify: !watchMode,
  legalComments: 'none',
  logLevel: 'warning',
  outdir,
  write: false,
  metafile: true,
};

// Monaco runs its editor and JSON language services in module workers that it
// locates relative to its own chunk; they are separate, hashed entries.
const workers = {
  'editor-worker': resolve(monacoDist, 'editor-worker-main.mjs'),
  'json-worker': resolve(monacoDist, 'lsp/json/worker.mjs'),
};

// Replacements applied to modern-monaco sources, keyed by dist-relative path.
// Every pattern must match so an upstream change fails the build loudly.
function monacoPatches(workerFiles, codiconFile) {
  // Monaco and modern-monaco inject <style> elements at runtime; give them the
  // page nonce, still readable as the .nonce property of any nonced script,
  // so the CSP admits them.
  const nonced = 'Object.assign(document.createElement("style"), { nonce: document.querySelector("script[nonce]")?.nonce ?? "" })';
  const styleElements = [/document\.createElement\("style"\)/g, nonced];
  return {
    'core.mjs': [
      // Load the editor core as a bundled chunk instead of from esm.sh.
      [/import\(\s*\/\* webpackIgnore: true \*\/\s*editorCoreModuleUrl\s*\)/, 'import("./editor-core.mjs")'],
      // Highlighting uses Shiki's JavaScript regex engine, so the inlined
      // Oniguruma WebAssembly build is never needed.
      [/import \{ getWasmInstance \} from "\.\/shiki-wasm\.mjs";\n/, ''],
      [/setDefaultWasmLoader\(getWasmInstance\);/, ''],
      styleElements,
    ],
    'editor-core.mjs': [
      [/new URL\("\.\/editor-worker-main\.mjs", import\.meta\.url\)/, `new URL("./${workerFiles['editor-worker']}", import.meta.url)`],
      [/url\(data:font\/ttf;base64,[A-Za-z0-9+/=]+\)/, `url(/js/${codiconFile})`],
      styleElements,
    ],
    'lsp/json/setup.mjs': [
      [/new URL\("\.\/worker\.mjs", import\.meta\.url\)/, `new URL("./${workerFiles['json-worker']}", import.meta.url)`],
    ],
  };
}

function patchPlugin(patches) {
  return {
    name: 'modern-monaco-patches',
    setup(build) {
      build.onLoad({ filter: /modern-monaco[\\/]dist[\\/].+\.mjs$/ }, async ({ path }) => {
        const name = path.slice(monacoDist.length + 1).replaceAll('\\', '/');
        let contents = await readFile(path, 'utf8');
        for (const [pattern, replacement] of patches[name] ?? []) {
          if (!pattern.test(contents)) {
            throw new Error(`modern-monaco ${name}: pattern ${pattern} no longer matches`);
          }
          pattern.lastIndex = 0;
          contents = contents.replace(pattern, replacement);
        }
        return { contents, loader: 'js' };
      });
    },
  };
}

// The codicon font ships base64-encoded inside Monaco's CSS; write it out as a
// hashed file so it caches independently and font-src can stay 'self'.
async function extractCodicon() {
  const core = await readFile(resolve(monacoDist, 'editor-core.mjs'), 'utf8');
  const match = core.match(/url\(data:font\/ttf;base64,([A-Za-z0-9+/=]+)\)/);
  if (!match) {
    throw new Error('modern-monaco editor-core.mjs: codicon font not found');
  }
  const font = Buffer.from(match[1], 'base64');
  return { name: `codicon.${hash(font)}.ttf`, contents: font };
}

// Matches the shape of esbuild's [hash]: eight uppercase alphanumerics.
function hash(contents) {
  return createHash('sha256').update(contents).digest('hex').slice(0, 8).toUpperCase();
}

async function build() {
  const codicon = await extractCodicon();
  const workerResult = await esbuild.build({
    ...shared,
    entryPoints: Object.entries(workers).map(([out, input]) => ({ in: input, out })),
    entryNames: '[name].[hash]',
  });
  const workerFiles = {};
  for (const [output, meta] of Object.entries(workerResult.metafile.outputs)) {
    const name = basename(output);
    workerFiles[name.split('.')[0]] = name;
    if (!meta.entryPoint) {
      throw new Error(`worker build emitted an unexpected chunk: ${name}`);
    }
  }

  const mainResult = await esbuild.build({
    ...shared,
    entryPoints: [resolve(source, 'code-editor/index.js')],
    entryNames: 'code-editor',
    chunkNames: '[name].[hash]',
    splitting: true,
    plugins: [patchPlugin(monacoPatches(workerFiles, codicon.name))],
  });

  const files = [
    ...workerResult.outputFiles,
    ...mainResult.outputFiles,
    { path: resolve(outdir, codicon.name), contents: codicon.contents },
  ];
  // Write the new build before pruning the old one, so a page loading during a
  // development rebuild never finds its chunks missing.
  await mkdir(outdir, { recursive: true });
  await Promise.all(files.map(file => writeFile(file.path, file.contents)));
  if (release) {
    await Promise.all(files.map(precompress));
  }
  const current = new Set(files.flatMap(file => [basename(file.path), `${basename(file.path)}.br`]));
  const stale = (await readdir(outdir)).filter(name => !current.has(name));
  await Promise.all(stale.map(name => rm(resolve(outdir, name))));
  return files.reduce((total, file) => total + file.contents.length, 0);
}

// Brotli at quality 11 is too slow to run per request but shrinks the Monaco
// core by a fifth over the server's on-the-fly compression.
const compress = promisify(brotliCompress);
async function precompress(file) {
  const compressed = await compress(file.contents, {
    params: {
      [constants.BROTLI_PARAM_QUALITY]: constants.BROTLI_MAX_QUALITY,
      [constants.BROTLI_PARAM_SIZE_HINT]: file.contents.length,
    },
  });
  await writeFile(`${file.path}.br`, compressed);
}

const bytes = await build();
console.log(`JS: ${bytes} bytes${watchMode ? ' (dev)' : ''} → ${outdir}`);

if (watchMode) {
  let timer;
  watch(source, { recursive: true }, () => {
    clearTimeout(timer);
    timer = setTimeout(async () => {
      try {
        console.log(`JS: rebuilt ${await build()} bytes`);
      } catch (error) {
        // Keep watching; the last good output stays in place.
        console.error(`JS: build failed\n${error.message}`);
      }
    }, 30);
  });
  console.log('JS: watching web/assets/js/');
}
