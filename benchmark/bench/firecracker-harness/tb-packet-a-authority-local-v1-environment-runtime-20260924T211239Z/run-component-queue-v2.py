#!/usr/bin/env python3
"""Resume the accepted TerraBase component ELF queue serially with Firecracker-safe runtime IDs."""
from __future__ import annotations
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import time

EVIDENCE = Path(__file__).resolve().parent
BENCH = Path('/home/mariogk/Projects/TerraScale/ts-benchmark')
REPO = Path('/home/mariogk/Projects/TerraScale/terrabase')
QUEUE = REPO/'target/verification/tb-packet-a-authority-local-v1-20260924-environment-runtime-queue.json'
QUEUE_SHA = '14d5736681c6c6b77f86f8d561874eb8ead5e8cea067f116f789d0a58340ebad'
SOURCE_MANIFEST = REPO/'target/verification/tb-packet-a-authority-local-v1-20260924-final-source-snapshot/source.sha256'
SOURCE_SHA = '35ee2068a77afc496587fa847f497b2f339d9a939ad2ab36539e4f9d9d7ec924'
HARNESS = Path('/tmp/fch-runtime-repo-final-20260924/fc-harness')
HARNESS_SHA = '32266d9ec86404070d71f1b12ac43150208cb004634dde12afc6a3dd28be8f20'
AGENT = Path('/tmp/fch-runtime-repo-final-20260924/fc-guest-agent')
AGENT_SHA = '3a2b582bd911e654aaa1a7181e0761dee6231cc317270bceca62486b7ea0c6bb'
LEDGER = EVIDENCE/'runtime-ledger.jsonl'
RUNS = EVIDENCE/'runs'
OPERATORS = EVIDENCE/'operators'
FIRST_RUN_ID = 'tb-packet-a-env-20260924-01-document-default-distributed-open'
FIRST_MANIFEST = RUNS/FIRST_RUN_ID/'run-manifest.json'


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def record(event: dict) -> None:
    with LEDGER.open('a', encoding='utf-8') as f:
        f.write(json.dumps(event, sort_keys=True, separators=(',', ':'))+'\n')
        f.flush()
        os.fsync(f.fileno())


def check_global_identity() -> dict:
    if digest(QUEUE) != QUEUE_SHA:
        raise RuntimeError('accepted queue hash changed')
    if digest(SOURCE_MANIFEST) != SOURCE_SHA:
        raise RuntimeError('accepted source manifest hash changed')
    if digest(HARNESS) != HARNESS_SHA or digest(AGENT) != AGENT_SHA:
        raise RuntimeError('accepted runner/agent bytes changed')
    return json.loads(QUEUE.read_text())


def guest_test_summary(run_dir: Path) -> tuple[str | None, str]:
    archive = run_dir/'guest-output.tar.gz'
    if not archive.is_file():
        return None, 'guest output archive missing'
    try:
        with tarfile.open(archive, 'r:gz') as tar:
            member = next((m for m in tar.getmembers() if m.name in ('./stdout.log', 'stdout.log')), None)
            if member is None:
                return None, 'guest stdout missing from archive'
            stream = tar.extractfile(member)
            if stream is None:
                return None, 'guest stdout unreadable'
            text = stream.read().decode('utf-8', errors='replace')
    except (OSError, tarfile.TarError) as e:
        return None, f'guest archive invalid: {e}'
    matches = re.findall(r'test result: (ok|FAILED)\. ([^\n]+)', text)
    return (matches[-1][0]+'. '+matches[-1][1] if matches else None), text[-1800:]


def run_record(item: dict, index: int, run_id: str, run_dir: Path, log: Path,
               command: list[str], rc: int, wrapper_status: int | None = None) -> dict:
    manifest_path = run_dir/'run-manifest.json'
    manifest = json.loads(manifest_path.read_text()) if manifest_path.is_file() else None
    summary, test_tail = guest_test_summary(run_dir) if manifest else (None, 'no manifest')
    return {
        'event': 'completed', 'index': index, 'run_id': run_id,
        'scope': item['scope'], 'target': item['target'], 'features': item['features'],
        'payload_path': item['executable'], 'payload_sha256_expected': item['sha256'],
        'payload_sha256_observed': digest(Path(item['executable'])) if Path(item['executable']).is_file() else None,
        'source_manifest_sha256': item['source_manifest_sha256'],
        'source_path': item['source_path'], 'command_argv': command,
        'controller_path': str(HARNESS), 'controller_sha256': digest(HARNESS),
        'guest_agent_path': str(AGENT), 'guest_agent_sha256': digest(AGENT),
        'subprocess_exit_code': rc, 'wrapper_shell_exit_code': wrapper_status,
        'operator_log': str(log), 'operator_log_sha256': digest(log) if log.is_file() else None,
        'run_manifest': str(manifest_path) if manifest_path.is_file() else None,
        'run_manifest_sha256': digest(manifest_path) if manifest_path.is_file() else None,
        'run_status': manifest.get('status') if manifest else None,
        'guest_exit_code': manifest.get('guest_exit_code') if manifest else None,
        'guest_elapsed_ms': manifest.get('guest_elapsed_ms') if manifest else None,
        'guest_vcpu_count': manifest.get('guest_vcpu_count') if manifest else None,
        'guest_mem_size_mib': manifest.get('guest_mem_size_mib') if manifest else None,
        'guest_swap_disabled': manifest.get('guest_swap_disabled') if manifest else None,
        'guest_network': manifest.get('guest_network') if manifest else None,
        'host_swap_active': manifest.get('host_swap_active') if manifest else None,
        'test_summary': summary, 'test_output_tail': test_tail,
        'archive_sha256': digest(run_dir/'guest-output.tar.gz') if (run_dir/'guest-output.tar.gz').is_file() else None,
        'firecracker_log_sha256': digest(run_dir/'firecracker.log') if (run_dir/'firecracker.log').is_file() else None,
        'firecracker_internal_log_sha256': digest(run_dir/'firecracker-internal.log') if (run_dir/'firecracker-internal.log').is_file() else None,
        'guest_rootfs_copy_present_after_exit': (run_dir/'guest-rootfs.ext4').exists(),
        'recorded_firecracker_pid': (run_dir/'firecracker.pid').read_text().strip() if (run_dir/'firecracker.pid').is_file() else None,
    }


def verify_pass(recorded: dict, item: dict) -> None:
    if recorded['payload_sha256_observed'] != item['sha256']:
        raise RuntimeError('payload identity changed during run')
    if recorded['run_status'] != 'passed' or recorded['guest_exit_code'] != 0 or recorded['subprocess_exit_code'] != 0:
        raise RuntimeError(f"guest did not pass: run={recorded['run_status']} guest_exit={recorded['guest_exit_code']} runner_exit={recorded['subprocess_exit_code']}")
    if recorded['test_summary'] is None or not recorded['test_summary'].startswith('ok.'):
        raise RuntimeError(f"test summary missing/failed: {recorded['test_summary']}")
    if recorded['guest_vcpu_count'] != 4 or recorded['guest_mem_size_mib'] != 8192 or recorded['guest_swap_disabled'] is not True:
        raise RuntimeError('guest resource gate mismatch')
    if not recorded['guest_network'] or not recorded['guest_network'].startswith('disabled'):
        raise RuntimeError('guest network unexpectedly enabled')
    if recorded['guest_rootfs_copy_present_after_exit']:
        raise RuntimeError('run-owned writable rootfs remained after harness exit')


def main() -> int:
    queue = check_global_identity()
    items = queue['artifacts']
    if len(items) != 25:
        raise RuntimeError('accepted queue count changed')
    RUNS.mkdir(parents=True, exist_ok=True)
    OPERATORS.mkdir(parents=True, exist_ok=True)
    if not LEDGER.exists():
        item = items[0]
        run_dir = RUNS/FIRST_RUN_ID
        log = OPERATORS/(FIRST_RUN_ID+'.log')
        command = ['nice','-n','19','ionice','-c','3',str(HARNESS),'run','--benchmark-repo',str(BENCH),'--subject-repo',str(REPO),'--program',item['executable'],'--run-id',FIRST_RUN_ID,'--timeout-seconds','600','--output',str(run_dir),'--','--test-threads=1','--nocapture']
        prior = run_record(item, 1, FIRST_RUN_ID, run_dir, log, command, 0, wrapper_status=127)
        prior['note'] = 'The guest runner and workload exited 0. The surrounding login shell exited 127 afterward because /etc/bash_logout referenced an unset __ETC_BASHLOGOUT_SOURCED under set -u; preserved separately and not retried.'
        verify_pass(prior, item)
        record(prior)
    completed = 1
    print('VERIFIED 1/25 '+FIRST_RUN_ID+' '+items[0]['sha256'][:12], flush=True)
    for i in range(1, len(items)):
        item = items[i]
        index = i+1
        suffix = 'hooks' if item['features'] else 'default'
        safe_target = item['target'].replace('_', '-')
        retry = '-retry1' if index == 2 else ''
        run_id = f"tb-pa-env-20260924-{index:02d}{retry}-{safe_target}-{suffix}"
        run_dir = RUNS/run_id
        log = OPERATORS/(run_id+'.log')
        if run_dir.exists() or log.exists():
            raise RuntimeError(f'refusing to duplicate existing output for {run_id}')
        if digest(Path(item['executable'])) != item['sha256']:
            raise RuntimeError(f'payload hash changed before {run_id}')
        check_global_identity()
        command = ['nice','-n','19','ionice','-c','3',str(HARNESS),'run','--benchmark-repo',str(BENCH),'--subject-repo',str(REPO),'--program',item['executable'],'--run-id',run_id,'--timeout-seconds','600','--output',str(run_dir),'--','--test-threads=1','--nocapture']
        env = os.environ.copy()
        env['FC_BENCHMARK_REPO'] = str(BENCH)
        env['FC_GUEST_AGENT_PATH'] = str(AGENT)
        start = {'event':'started','index':index,'run_id':run_id,'scope':item['scope'],'target':item['target'],'features':item['features'],'payload_sha256':item['sha256'],'source_manifest_sha256':item['source_manifest_sha256'],'command_argv':command,'started_unix_seconds':time.time()}
        record(start)
        print(f"START {index}/25 {run_id} payload={item['sha256'][:12]}", flush=True)
        with log.open('wb') as f:
            process = subprocess.run(command, cwd=BENCH, env=env, stdout=f, stderr=subprocess.STDOUT, check=False)
        completed_record = run_record(item,index,run_id,run_dir,log,command,process.returncode)
        record(completed_record)
        try:
            verify_pass(completed_record,item)
        except Exception as e:
            print(f"FAIL {index}/25 {run_id}: {e}", flush=True)
            return 2
        completed += 1
        print(f"PASS {index}/25 {run_id} {completed_record['test_summary']} guest_ms={completed_record['guest_elapsed_ms']}", flush=True)
        if completed % 3 == 0 or completed == 25:
            print(f"PROGRESS {completed}/25 passed", flush=True)
    print(f'SUMMARY passed={completed} failed=0 total=25 ledger={LEDGER}', flush=True)
    return 0

if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f'ABORT: {exc}', file=sys.stderr, flush=True)
        raise SystemExit(3)
