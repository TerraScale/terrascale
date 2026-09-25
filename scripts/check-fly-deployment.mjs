import { readFileSync } from 'node:fs';

const [mode, machinesPath, volumesPath] = process.argv.slice(2);

function fail(message) {
  console.error(`Fly deployment check: ${message}`);
  process.exit(1);
}

if (mode === 'preview-config') {
  if (!machinesPath || volumesPath) fail('usage: preview-config <fly.preview.toml>');
  const lines = readFileSync(machinesPath, 'utf8').split('\n').map(line => line.split('#', 1)[0]);
  if (lines.some(line => /^\s*swap_size_mb\s*=|^\s*\[\[?mounts\]?\]/.test(line))) {
    fail('preview config must not configure swap or volume mounts');
  }
  console.log('Fly preview config has no swap or volume mount');
  process.exit(0);
}

if (!['production', 'preview'].includes(mode) || !machinesPath || !volumesPath) {
  fail('usage: <production|preview> <machines.json> <volumes.json>');
}

const machines = JSON.parse(readFileSync(machinesPath, 'utf8'));
const volumes = JSON.parse(readFileSync(volumesPath, 'utf8'));
if (!Array.isArray(machines) || !Array.isArray(volumes)) fail('Fly CLI did not return JSON arrays');

const preview = mode === 'preview';
if (machines.length !== 1) fail(`${mode} has ${machines.length} Machines; expected one`);
if (volumes.length !== 0) fail(`${mode} has persistent volumes; the demo stores no state on disk`);
if (!preview && !machines.some(machine => machine.state === 'started')) fail('production has no warm Machine');

for (const machine of machines) {
  const config = machine.config ?? {};
  const guest = config.guest ?? {};
  const service = (config.services ?? []).find(item => item.internal_port === 8080);
  if (guest.cpu_kind !== 'shared' || guest.cpus !== (preview ? 1 : 2) || guest.memory_mb !== (preview ? 256 : 512)) {
    fail(`${mode} Machine ${machine.id} has the wrong CPU or RAM`);
  }
  if (config.env?.TS_DASHBOARD_PRODUCTION_DEMO !== '1') fail(`${mode} Machine ${machine.id} is not in mandatory demo mode`);
  if (!service || service.autostart !== true) fail(`${mode} Machine ${machine.id} does not auto-start`);
  if ((config.mounts ?? []).length !== 0) fail(`${mode} Machine ${machine.id} has a volume mount`);
  if (preview) {
    if (config.init?.swap_size_mb != null && config.init.swap_size_mb !== 0) fail(`preview Machine ${machine.id} has swap`);
    if (![true, 'stop'].includes(service.autostop) || service.min_machines_running !== 0) {
      fail(`preview Machine ${machine.id} has the wrong idle policy`);
    }
    continue;
  }
  if (config.init?.swap_size_mb !== 900) fail(`production Machine ${machine.id} does not have 900 MB swap`);
  if (service.autostop !== false || service.min_machines_running !== 1) {
    fail(`production Machine ${machine.id} has the wrong idle policy`);
  }
}

console.log(`Fly ${mode} deployment verified: ${machines.length} Machine(s), no volumes`);
