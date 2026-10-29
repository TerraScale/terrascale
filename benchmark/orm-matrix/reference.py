import getpass
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
from urllib.parse import quote


with tempfile.TemporaryDirectory(prefix='orm-reference-') as directory:
    root = Path(directory)
    data = root / 'data'
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    subprocess.run(['initdb', '-D', str(data), '-A', 'trust', '--no-locale', '-E', 'UTF8'], check=True, stdout=subprocess.DEVNULL)
    control = ['pg_ctl', '-D', str(data), '-w']
    try:
        subprocess.run(control + ['-l', str(root / 'postgres.log'), '-o', f'-h 127.0.0.1 -p {port} -k {directory}', 'start'], check=True, stdout=subprocess.DEVNULL)
        environment = dict(os.environ, MATRIX_SURFACE='postgres-reference', DATABASE_URL=f'postgresql://{quote(getpass.getuser(), safe="")}@127.0.0.1:{port}/postgres?sslmode=disable')
        result = subprocess.run(sys.argv[1:], env=environment)
    finally:
        subprocess.run(control + ['-m', 'immediate', 'stop'], check=False, stdout=subprocess.DEVNULL)
    sys.exit(result.returncode)
