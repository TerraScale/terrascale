#!/usr/bin/env python3
import json
from pathlib import Path
import sys

INTRODUCERS = ('Aspire.Hosting/', 'Aspire.Hosting.AppHost/', 'Aspire.Hosting.Dotnet/', 'StreamJsonRpc/')
REQUIRED = 'Aspire.Hosting.AppHost'

assets = json.loads(Path(sys.argv[1]).read_text())
edges = 0
for target in assets['targets'].values():
    for package, entry in target.items():
        for dependency in entry.get('dependencies', {}):
            if 'newtonsoft' not in dependency.lower():
                continue
            if not package.startswith(INTRODUCERS) or dependency != 'Newtonsoft.Json':
                raise SystemExit(f'Only Aspire hosting packages may bring Newtonsoft.Json into the local Aspire host: {package} -> {dependency}')
            edges += 1
for framework in assets['project']['frameworks'].values():
    dependencies = framework.get('dependencies', {})
    if any('newtonsoft' in dependency.lower() for dependency in dependencies):
        raise SystemExit('Direct Newtonsoft package references are banned; use System.Text.Json')
    if REQUIRED not in dependencies:
        raise SystemExit(f'The local Aspire host must reference {REQUIRED}')
if not edges:
    raise SystemExit(f'The exception requires an actual {REQUIRED} -> Newtonsoft.Json dependency')
print('Verified the local-dev-only Newtonsoft.Json boundary of TerraScale.Aspire')
