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

// The JSON language service would fetch any http(s) $schema a document names,
// from a worker the page's CSP does not govern; schemas stay local.
const workerPatches = {
  'lsp/json/worker.mjs': [
    [/return cache\.fetch\(url\)\.then\(\(res\) => res\.text\(\)\);/, 'return Promise.reject(new Error("Remote JSON schemas are disabled"));'],
  ],
};

// Replacements applied to modern-monaco sources, keyed by dist-relative path.
function monacoPatches(workerFiles, codiconFile) {
  // Monaco and modern-monaco inject <style> elements at runtime; give them the
  // page nonce, still readable as the .nonce property of the editor's own
  // nonced script, so the CSP admits them.
  const nonced = 'Object.assign(document.createElement("style"), { nonce: document.querySelector(\'script[src^="/js/code-editor.js"]\')?.nonce ?? "" })';
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

// Applies patches while esbuild loads each file. Every pattern must match, and
// every patched file must be part of the bundle, so an upstream change fails
// the build loudly instead of shipping unpatched code.
function patchPlugin(patches) {
  return {
    name: 'modern-monaco-patches',
    setup(build) {
      const applied = new Set();
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
        applied.add(name);
        return { contents, loader: 'js' };
      });
      build.onEnd(result => {
        const missing = Object.keys(patches).filter(name => !applied.has(name));
        if (missing.length > 0 && result.errors.length === 0) {
          throw new Error(`modern-monaco patches were never applied: ${missing.join(', ')}`);
        }
      });
    },
  };
}

// Nothing may load code from another origin at runtime.
function assertSelfHosted(files) {
  for (const file of files) {
    if (/import\(\s*["'`]https?:/.test(file.text)) {
      throw new Error(`${basename(file.path)} imports code from a remote origin`);
    }
  }
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

async function readVendor(name) {
  try {
    return await readFile(resolve(source, 'vendor', name));
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    throw new Error(`Missing web/assets/js/vendor/${name}; restore it from the official Datastar v1.0.4 release`, { cause: error });
  }
}

async function build() {
  const datastar = await readVendor('datastar.js');
  const datastarMap = await readVendor('datastar.js.map');
  const datastarLicense = await readVendor('LICENSE.datastar.md');
  const helpers = await readFile(resolve(repository, 'internal/ui/layout_helpers.go'), 'utf8');
  const pinned = helpers.match(/\bconst\s+datastarIntegrity\s*=\s*"(sha384-[A-Za-z0-9+/=]+)"/)?.[1];
  const integrity = `sha384-${createHash('sha384').update(datastar).digest('base64')}`;
  if (integrity !== pinned) {
    throw new Error(`vendor/datastar.js hashes to ${integrity}, but datastarIntegrity in internal/ui/layout_helpers.go is ${pinned ?? 'missing'}`);
  }
  // Pinned from the official v1.0.4 release alongside the bundle. A bundle
  // upgrade must refresh its source map too.
  const pinnedMap = 'sha384-pt2bLYQEhQctXw5tJaokvLV96hxW/GSwz2K8L+3xltbjrLNwyWS8PG/2NprOqeIS';
  if (`sha384-${createHash('sha384').update(datastarMap).digest('base64')}` !== pinnedMap) {
    throw new Error('vendor/datastar.js.map differs from the pinned official Datastar v1.0.4 source map');
  }
  const datastarFile = { path: resolve(outdir, 'datastar.js'), contents: datastar, text: datastar.toString('utf8') };
  const codicon = await extractCodicon();
  const workerResult = await esbuild.build({
    ...shared,
    entryPoints: Object.entries(workers).map(([out, input]) => ({ in: input, out })),
    entryNames: '[name].[hash]',
    plugins: [patchPlugin(workerPatches)],
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
  assertSelfHosted([
    ...workerResult.outputFiles,
    ...mainResult.outputFiles,
    datastarFile,
  ]);

  const files = [
    ...workerResult.outputFiles,
    ...mainResult.outputFiles,
    { path: resolve(outdir, codicon.name), contents: codicon.contents },
    // Keep the pinned Datastar bundle intact so the layout's SRI hash applies.
    // Serving it here avoids a CDN request on every document navigation.
    datastarFile,
    // The official bundle references this map. Keep it for production DevTools;
    // normal navigation does not request it.
    { path: resolve(outdir, 'datastar.js.map'), contents: datastarMap },
    { path: resolve(outdir, 'LICENSE.datastar.md'), contents: datastarLicense },
  ];
  // Write the new build before pruning the old one, so a page loading during a
  // development rebuild never finds its chunks missing.
  await mkdir(outdir, { recursive: true });
  await Promise.all(files.map(file => writeFile(file.path, file.contents)));
  if (release) {
    await Promise.all(files.map(precompress));
  }
  // Brotli siblings from an earlier release build would shadow newer files.
  const current = new Set(files.flatMap(file => {
    const name = basename(file.path);
    return release ? [name, `${name}.br`] : [name];
  }));
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
