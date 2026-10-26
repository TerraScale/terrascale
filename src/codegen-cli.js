#!/usr/bin/env node
import { readFile, writeFile } from "node:fs/promises";
import { generateDocumentModels } from "./codegen.js";
import { parseExactJson } from "./v1/json.js";
const [source, name, output, ...extra] = process.argv.slice(2);
if (!source || !name || !output || extra.length) {
  process.stderr.write("Usage: terrascale-models declared-schema.json ModelName models.ts\nInput is the schema itself; use @terrascale/sdk/codegen to fetch a live declaration.\n");
  process.exitCode = 1;
} else {
  try {
    const generated = await generateDocumentModels(parseExactJson(await readFile(source, "utf8")), name);
    await writeFile(output, generated, "utf8");
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : "Model generation failed"}\n`);
    process.exitCode = 1;
  }
}
