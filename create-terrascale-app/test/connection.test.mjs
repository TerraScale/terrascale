import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import ts from "typescript";

const source = await readFile(new URL("../template/connection.ts", import.meta.url), "utf8");
const { outputText } = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 } });
const { readConnectionSettings } = await import(`data:text/javascript;base64,${Buffer.from(outputText).toString("base64")}`);
const settings = { project: "d00d0000-0000-4000-8000-000000000001", database: "starter_dev", baseUrl: "https://gru.api.terrascale.tech" };

test("connection uses a ts-auth project UUID, database name and bare API origin", () => {
  assert.deepEqual(readConnectionSettings({
    project: ` ${settings.project} `, database: ` ${settings.database} `, baseUrl: `${settings.baseUrl}/`,
  }), settings);
  assert.equal(readConnectionSettings({ ...settings, database: "a".repeat(63) }).database.length, 63);
});

test("connection rejects missing, malformed or display-name project IDs", () => {
  for (const project of [undefined, "", "my-project", "project_123", settings.project.toUpperCase(), `${settings.project}/other`]) {
    assert.throws(() => readConnectionSettings({ ...settings, project }), /lowercase UUID/);
  }
});

test("connection rejects path injection and names outside the contract", () => {
  for (const database of [undefined, "", "Posts", "123", "posts/other", "posts?limit=1", "a".repeat(64)]) {
    assert.throws(() => readConnectionSettings({ ...settings, database }), /database name/);
  }
  // A UUID can also be a valid name: syntax alone cannot prove which resource exists.
  assert.equal(readConnectionSettings({ ...settings, database: settings.project }).database, settings.project);
});

test("connection requires HTTPS without URL credentials or route suffixes", () => {
  for (const baseUrl of [undefined, "", "not a URL", "http://api.example.test", "https://user:secret@api.example.test", "https://api.example.test/v1", "https://api.example.test/?token=secret", "https://api.example.test/#fragment", "file:///tmp/data"]) {
    assert.throws(() => readConnectionSettings({ ...settings, baseUrl }), /HTTPS/);
  }
  for (const baseUrl of ["http://localhost:3001", "http://127.0.0.1:3001", "http://[::1]:3001"]) {
    assert.equal(readConnectionSettings({ ...settings, baseUrl }).baseUrl, baseUrl);
  }
});
