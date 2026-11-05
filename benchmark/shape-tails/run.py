import os
from pathlib import Path
import subprocess
import sys
import time


tag = os.environ['F9F5_PROOF_TAG']
directory = Path(__file__).parent
mode = sys.argv[1]
if mode not in ('tails', 'load'):
    raise SystemExit('Use tails or load')


def run(script, *arguments):
    subprocess.run([sys.executable, str(directory / script), *arguments], check=True)


try:
    subprocess.run(['ts-throwaway-account', tag], check=True)
    run('account_database.py')
    if mode == 'load':
        run('bounded_cdc.py', 'prepare')
    time.sleep(35)
    if mode == 'tails':
        run('end_user.py')
        for region in ('sjc', 'fra'):
            run('run_region.py', region)
    else:
        run('bounded_cdc.py', 'measure')
finally:
    try:
        result = subprocess.run(['ts-throwaway-account', '--delete', tag])
        if result.returncode:
            time.sleep(5)
            subprocess.run(['ts-throwaway-account', '--delete', tag], check=True)
    finally:
        private = Path.home() / '.local/state/ts-throwaway-account'
        for suffix in ('.token', '.proof.json'):
            (private / (tag + suffix)).unlink(missing_ok=True)
