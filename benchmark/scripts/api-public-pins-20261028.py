#!/usr/bin/env python3
"""Read-only public routing/protocol observation; not an authenticated performance test."""
import datetime
import ipaddress
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile


def regions_from_peers(path):
    regions = set()
    for line in Path(path).read_text().splitlines():
        fields = line.split()
        if not fields or fields[0].startswith('#'):
            continue
        if (len(fields) != 3 or not re.fullmatch(r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?', fields[0])
                or not re.fullmatch(r'[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?', fields[1])
                or str(ipaddress.IPv4Address(fields[2])) != fields[2]):
            raise ValueError('Malformed peers row')
        regions.add(fields[1])
    if not {'gru', 'sjc', 'fra'} <= regions:
        raise ValueError('Required serving region absent')
    return sorted(regions)


def observe(region, protocol, peers):
    with tempfile.TemporaryDirectory(prefix='api-public-pins-') as directory:
        headers, body = (Path(directory) / name for name in ('headers', 'body'))
        result = subprocess.run(['curl', '--silent', '--show-error', '--proto', '=https',
            '--connect-timeout', '10', '--max-time', '20', '--max-filesize', '65536',
            '--http1.1' if protocol == '1.1' else '--http2',
            '--header', 'X-TerraScale-Region-Pin: ' + region,
            '--dump-header', str(headers), '--output', str(body), '--write-out', '%{http_code} %{http_version}',
            'https://api.terrascale.tech/v1/regions'], capture_output=True, timeout=25)
        fields = result.stdout.decode('ascii').split()
        status, version = fields if len(fields) == 2 else ('unclassified', 'unclassified')
        blocks = [block for block in re.split(rb'\r?\n\r?\n', headers.read_bytes() if headers.exists() else b'') if block.startswith(b'HTTP/')]
        values = []
        if blocks:
            values = [line.partition(b':')[2].strip().decode('ascii', errors='replace')
                for line in blocks[-1].splitlines()[1:] if line.partition(b':')[0].lower() == b'x-terrabase-region']
        catalog = None
        try:
            data = json.loads(body.read_bytes())
            listed = data.get('regions') if isinstance(data, dict) else None
            if isinstance(listed, list) and all(isinstance(value, str) and value in peers for value in listed):
                catalog = listed
        except (OSError, ValueError):
            pass
        accepted = result.returncode == 0 and status == '200' and version == protocol and values == [region] and catalog == peers
        return dict(observed_at=datetime.datetime.now(datetime.timezone.utc).isoformat(), pin=region,
            requested_protocol=protocol, protocol=version, status=status, curl_exit=result.returncode,
            region_headers=[value if value in peers else 'other' for value in values], regions=catalog, accepted=accepted)


def main():
    if len(sys.argv) != 3:
        raise SystemExit('usage: api-public-pins-20261028.py PEERS NEW-OUTPUT.jsonl')
    peers = regions_from_peers(sys.argv[1])
    accepted = True
    descriptor = os.open(sys.argv[2], os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, 'w') as output:
        for region in peers:
            for protocol in ('1.1', '2'):
                row = observe(region, protocol, peers)
                output.write(json.dumps(row, separators=(',', ':')) + '\n')
                output.flush()
                print(json.dumps(row), flush=True)
                accepted &= row['accepted']
    return 0 if accepted else 1


if __name__ == '__main__':
    raise SystemExit(main())
