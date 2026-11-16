import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { createConnection } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { e2eRoot, stateDir } from '../support/stack';
import { nodeBuild, restartAfterKill, type Node } from './support';

export async function regionalHook(node: Node, overrides?: NodeJS.ProcessEnv) {
  const directory = mkdtempSync(join(tmpdir(), 'regional-hook-'));
  const original = nodeBuild(node);
  const pid = readFileSync(join(stateDir, 'pids', node.service + '.pid'), 'utf8').trim();
  const environment = Object.fromEntries(readFileSync(`/proc/${pid}/environ`, 'utf8').split('\0').filter(Boolean).map(value => {
    const at = value.indexOf('=');
    return [value.slice(0, at), value.slice(at + 1)];
  }));
  const build = process.env.E2E_REGIONAL_FENCE_BUILD ?? original;
  const dotnet = process.env.DOTNET_ROOT!;
  const references = new Map<string, string>();
  for (const pack of ['Microsoft.NETCore.App.Ref', 'Microsoft.AspNetCore.App.Ref']) {
    const folder = join(dotnet, 'packs', pack, '11.0.0/ref/net11.0');
    for (const file of readdirSync(folder)) if (file.endsWith('.dll')) references.set(file, join(folder, file));
  }
  for (const file of readdirSync(dirname(build))) {
    if (file.endsWith('.dll') && !references.has(file)) references.set(file, join(dirname(build), file));
  }
  const hook = join(directory, 'hook.dll');
  const response = join(directory, 'compile.rsp');
  writeFileSync(response, ['-nologo', '-target:library', '-nullable:enable', '-langversion:preview', '-out:' + hook,
    ...[...references.values()].map(file => '-r:' + file), join(e2eRoot, 'env/engine-crash-hook.cs')].join('\n'));
  execFileSync(join(dotnet, 'dotnet'), [join(dotnet, 'sdk/11.0.100/Roslyn/bincore/csc.dll'), '@' + response], { stdio: 'inherit' });
  const socket = join(directory, 'hook.sock');
  const restart = restartAfterKill(node, true, undefined, undefined, build, { ...overrides, DOTNET_STARTUP_HOOKS: hook, ENGINE_CRASH_SOCKET: socket });
  await restart();
  return {
    command: (operation: string, organization: string, project: string, database: string) => new Promise<string>((resolve, reject) => {
      const connection = createConnection(socket);
      let answer = '';
      connection.once('error', reject);
      connection.once('connect', () => connection.write([operation, organization, project, database].join(' ') + '\n'));
      connection.on('data', data => {
        answer += data.toString();
        if (answer.includes('\n')) {
          connection.destroy();
          resolve(answer.slice(0, answer.indexOf('\n')));
        }
      });
    }),
    close: async () => {
      await restartAfterKill(node, true, undefined, undefined, original, { ...Object.fromEntries(Object.keys(overrides ?? {}).map(name => [name, environment[name]])), DOTNET_STARTUP_HOOKS: environment.DOTNET_STARTUP_HOOKS, ENGINE_CRASH_SOCKET: environment.ENGINE_CRASH_SOCKET })();
      rmSync(directory, { recursive: true });
    },
  };
}
