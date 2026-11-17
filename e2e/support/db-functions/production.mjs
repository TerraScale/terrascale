import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { runMatrix } from './run.mjs';

const executable = process.argv[2] ?? 'ts';
const scratch = 'c35-' + Date.now().toString(36);
const directory = mkdtempSync(join(tmpdir(), scratch + '-'));
const rows = [];
let project;
let selectors = [];
let cleaned = false;

function cli(args, json = true) {
  const result = spawnSync(executable, [...(json ? ['--json'] : []), ...selectors, ...args], {
    encoding: 'utf8', timeout: 660000, maxBuffer: 16 * 1024 * 1024,
  });
  if (result.error || result.status !== 0) {
    const code = /\b(serialization_conflict|quota_exceeded|forbidden_scope|invalid_key|key_expired|plan_unpublished|replica_not_ready)\b/.exec(result.stderr ?? '')?.[1];
    throw Object.assign(new Error('operator CLI failed: ' + args[0] + ' ' + (args[1] ?? '')), {
      code: code ?? 'operator_cli', status: result.status, ddl: args[0] === 'sql' && /^(CREATE|ALTER|DROP) /.test(args[2]),
    });
  }
  return json && result.stdout.trim() ? JSON.parse(result.stdout) : result.stdout;
}

async function check(name, action) {
  try {
    await action();
    rows.push({ name, status: 'PASS' });
    return true;
  } catch (error) {
    rows.push({ name, status: error.code === 'serialization_conflict' && error.ddl ? 'C34' : 'FAIL', code: error.code ?? error.name });
    return false;
  }
}

try {
  cli(['whoami']);
  const help = cli(['help'], false);
  assert.match(help, /db export/);
  assert.match(help, /db import/);
  assert.match(help, /-regions/);
  project = cli(['projects', 'create', scratch]);
  assert.match(project.id, /^[0-9A-HJKMNP-TV-Z]{26}$/);
  assert.equal(project.name, scratch);
  selectors = ['-org', project.organization_id, '-project', project.id];
  writeFileSync(join(directory, 'cleanup.json'), JSON.stringify({ project: project.id, organization: project.organization_id }), { mode: 0o600 });
  rows.push({ name: 'scratch-project-create', status: 'PASS' });
  cli(['-regions', 'gru,sjc,fra', 'db', 'create', scratch]);
  const database = cli(['db', 'info', scratch]);
  rows.push({ name: 'three-region-database-create', status: 'PASS' });
  const key = cli(['keys', 'create', scratch + '-rw']);
  const state = { projectId: project.id, databaseId: database.id, apiKey: key.key };
  const matrix = await runMatrix(state, 'production');
  if (matrix.some(cell => cell.status !== 'PASS')) process.exitCode = 1;
  const marker = await check('recovery-source-data', () => {
    cli(['sql', scratch, 'CREATE TABLE c35_recovery(id INTEGER PRIMARY KEY,value TEXT NOT NULL)']);
    cli(['sql', scratch, "INSERT INTO c35_recovery VALUES(1,'snapshot')"]);
  });
  if (marker) {
    let backup;
    await check('snapshot-create-list', () => {
      backup = cli(['backup', 'create', scratch]);
      assert.equal(backup.status, 'ready');
      assert(cli(['backup', 'list', scratch]).some(item => item.id === backup.id));
    });
    if (backup) await check('snapshot-restore-data', () => {
      cli(['sql', scratch, "UPDATE c35_recovery SET value='later' WHERE id=1"]);
      cli(['backup', 'restore', scratch, backup.id, scratch + '-restored']);
      const restored = cli(['sql', scratch + '-restored', 'SELECT value FROM c35_recovery WHERE id=1']);
      assert.deepEqual(restored.rows, [['snapshot']]);
    });
    await check('export-import-data', () => {
      const dump = join(directory, 'database.sql');
      cli(['-o', dump, 'db', 'export', scratch], false);
      cli(['-regions', 'gru,sjc,fra', 'db', 'create', scratch + '-imported']);
      cli(['-yes', 'db', 'import', scratch + '-imported', dump], false);
      const source = cli(['sql', scratch, 'SELECT value FROM c35_recovery WHERE id=1']);
      const imported = cli(['sql', scratch + '-imported', 'SELECT value FROM c35_recovery WHERE id=1']);
      assert.deepEqual(imported.rows, source.rows);
    });
  }
} catch (error) {
  rows.push({ name: project ? 'production-execution' : 'operator-preflight', status: project ? 'FAIL' : 'BLOCKED', code: error.code ?? error.name });
  process.exitCode = 1;
} finally {
  if (project) {
    cleaned = await check('scratch-cleanup', () => {
      const databases = cli(['db', 'list']);
      assert(Array.isArray(databases));
      for (const database of databases) cli(['-yes', 'db', 'delete', database.id], false);
      assert.equal(cli(['db', 'list']).length, 0);
      cli(['-yes', 'projects', 'delete', project.id], false);
    });
  } else cleaned = true;
  if (cleaned) rmSync(directory, { recursive: true });
  else process.stderr.write('cleanup journal: ' + join(directory, 'cleanup.json') + '\n');
  for (const row of rows) process.stdout.write(JSON.stringify({ target: 'production', surface: 'operator', ...row }) + '\n');
  process.stdout.write('| Target | Surface | Pass | Fail | C34 | Blocked |\n| --- | --- | ---: | ---: | ---: | ---: |\n');
  process.stdout.write('| production | operator | ' + ['PASS', 'FAIL', 'C34', 'BLOCKED'].map(status => rows.filter(row => row.status === status).length).join(' | ') + ' |\n');
  if (rows.some(row => row.status !== 'PASS')) process.exitCode = 1;
}
