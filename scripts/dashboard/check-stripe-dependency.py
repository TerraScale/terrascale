#!/usr/bin/env python3
"""Enforce the approved Stripe-only transitive Newtonsoft dependency boundary."""
import json
from pathlib import Path
import sys

assets = json.loads(Path(sys.argv[1]).read_text())
stripe_edges = 0
for target in assets['targets'].values():
    for package, entry in target.items():
        for dependency in entry.get('dependencies', {}):
            if 'newtonsoft' not in dependency.lower():
                continue
            if not package.startswith('Stripe.net/') or dependency != 'Newtonsoft.Json':
                raise SystemExit(f'Only Stripe.net may bring Newtonsoft.Json into a dashboard project: {package} -> {dependency}')
            stripe_edges += 1
for framework in assets['project']['frameworks'].values():
    if 'Stripe.net' not in framework.get('dependencies', {}):
        raise SystemExit('The exception requires a direct reference to the official Stripe.net SDK')
    if any('newtonsoft' in dependency.lower() for dependency in framework.get('dependencies', {})):
        raise SystemExit('Direct Newtonsoft package references are banned; use System.Text.Json')
if not stripe_edges:
    raise SystemExit('The exception requires an actual Stripe.net -> Newtonsoft.Json dependency')
print('Verified dashboard Stripe.net -> Newtonsoft.Json boundary; direct package references and other introducers are banned')
