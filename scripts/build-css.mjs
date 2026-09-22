import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { transform } from 'lightningcss';

const source = fileURLToPath(new URL('../src/TerraScale.Dashboard/wwwroot/css/terrascale.css', import.meta.url));
const output = resolve(process.argv[2] ?? 'obj/assets/terrascale.css');
if (output === source) {
  throw new Error('The minified output must not overwrite the source stylesheet.');
}

const input = await readFile(source);
const { code, warnings } = transform({ filename: source, code: input, minify: true });
if (warnings.length > 0) {
  throw new Error(warnings.map(warning => warning.message).join('\n'));
}

await mkdir(dirname(output), { recursive: true });
await writeFile(output, code);
console.log(`CSS: ${input.length} → ${code.length} bytes (${output})`);
