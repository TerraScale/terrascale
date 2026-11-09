#!/usr/bin/env python3
import json
from pathlib import Path
import sys

INTRODUCERS = ('Microsoft.EntityFrameworkCore.Design/', 'Microsoft.CodeAnalysis.Workspaces.MSBuild/')
DESIGN = 'Microsoft.EntityFrameworkCore.Design'

assets = json.loads(Path(sys.argv[1]).read_text())
edges = 0
for target in assets['targets'].values():
    for package, entry in target.items():
        for dependency in entry.get('dependencies', {}):
            if 'newtonsoft' not in dependency.lower():
                continue
            if not package.startswith(INTRODUCERS) or dependency != 'Newtonsoft.Json':
                raise SystemExit(f'Only Entity Framework Core design tooling may bring Newtonsoft.Json into a design host: {package} -> {dependency}')
            edges += 1
for framework in assets['project']['frameworks'].values():
    dependencies = framework.get('dependencies', {})
    if any('newtonsoft' in dependency.lower() for dependency in dependencies):
        raise SystemExit('Direct Newtonsoft package references are banned; use System.Text.Json')
    if DESIGN not in dependencies or dependencies[DESIGN].get('suppressParent', '').replace(' ', '').lower() != 'all':
        raise SystemExit(f'A design host references {DESIGN} with PrivateAssets="all"')
if not edges:
    raise SystemExit(f'The exception requires an actual {DESIGN} -> Newtonsoft.Json dependency')
print('Verified the design-time-only Newtonsoft.Json boundary of an Entity Framework Core design host')
