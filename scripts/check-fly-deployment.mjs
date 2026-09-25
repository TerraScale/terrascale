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

if (mode === 'production-spares') {
  if (!machinesPath || volumesPath) fail('usage: production-spares <machines.json>');
  const machines = JSON.parse(readFileSync(machinesPath, 'utf8'));
  if (!Array.isArray(machines) || machines.length !== 3) fail('expected exactly three provisioned production Machines');
  const running = machines.filter(machine => machine.state === 'started');
  if (running.length === 0) fail('at least one production Machine must be started');
  const keep = running.toSorted((a, b) => a.created_at.localeCompare(b.created_at))[0].id;
  for (const machine of running) {
    if (machine.id !== keep) console.log(machine.id);
  }
  process.exit(0);
}

if (!['production', 'production-three', 'preview'].includes(mode) || !machinesPath || !volumesPath) {
  fail('usage: <production|production-three|preview> <machines.json> <volumes.json>');
}

const machines = JSON.parse(readFileSync(machinesPath, 'utf8'));
const volumes = JSON.parse(readFileSync(volumesPath, 'utf8'));
if (!Array.isArray(machines) || !Array.isArray(volumes)) fail('Fly CLI did not return JSON arrays');

const preview = mode === 'preview';
const expectedCount = mode === 'production-three' ? 3 : 1;
if (machines.length !== expectedCount) fail(`${mode} has ${machines.length} Machines; expected ${expectedCount}`);
if (preview && volumes.length !== 0) fail('preview has persistent volumes');
if (!preview && !machines.some(machine => machine.state === 'started')) fail('production has no warm Machine');

const mountedVolumes = new Set();
for (const machine of machines) {
  const config = machine.config ?? {};
  const guest = config.guest ?? {};
  const service = (config.services ?? []).find(item => item.internal_port === 8080);
  if (guest.cpu_kind !== 'shared' || guest.cpus !== (preview ? 1 : 2) || guest.memory_mb !== (preview ? 256 : 512)) {
    fail(`${mode} Machine ${machine.id} has the wrong CPU or RAM`);
  }
  if (config.env?.TS_DASHBOARD_PRODUCTION_DEMO !== '1') fail(`${mode} Machine ${machine.id} is not in mandatory demo mode`);
  if (!service || service.autostart !== true) fail(`${mode} Machine ${machine.id} does not auto-start`);
  if (preview) {
    if (config.swap_size_mb != null && config.swap_size_mb !== 0) fail(`preview Machine ${machine.id} has swap`);
    if ((config.mounts ?? []).length !== 0) fail(`preview Machine ${machine.id} has a volume mount`);
    if (![true, 'stop'].includes(service.autostop) || service.min_machines_running !== 0) {
      fail(`preview Machine ${machine.id} has the wrong idle policy`);
    }
    continue;
  }
  if (config.swap_size_mb !== 900) fail(`production Machine ${machine.id} does not have 900 MB swap`);
  if (service.autostop !== false || service.min_machines_running !== 1) {
    fail(`production Machine ${machine.id} has the wrong idle policy`);
  }
  const mounts = config.mounts ?? [];
  if (mounts.length !== 1 || mounts[0].path !== '/data' || !mounts[0].volume) {
    fail(`production Machine ${machine.id} does not have one /data volume`);
  }
  const volume = volumes.find(item => item.id === mounts[0].volume);
  if (!volume) fail(`production Machine ${machine.id} mounts a volume absent from the app volume list`);
  if (volume.name !== 'dashboard_data') fail(`production Machine ${machine.id} mounts ${volume.name}; expected dashboard_data`);
  if (volume.size_gb !== 1) fail(`production volume ${volume.id} is ${volume.size_gb} GB; expected exactly 1 GB`);
  if (mountedVolumes.has(volume.id)) fail(`production volume ${volume.id} is mounted twice`);
  mountedVolumes.add(volume.id);
}

if (!preview && mountedVolumes.size !== volumes.length) {
  const extra = volumes.filter(volume => !mountedVolumes.has(volume.id)).map(volume => volume.id);
  fail(`production has ${volumes.length} app volumes for ${mountedVolumes.size} Machine mounts; extra or duplicate volumes: ${extra.join(', ') || 'duplicate IDs'}`);
}

console.log(`Fly ${mode} deployment verified: ${machines.length} Machine(s), ${mountedVolumes.size} mounted volume(s)`);
