#!/usr/bin/env python3
"""Reject helper-text classes removed by the simple-ui standard (.agents/skills/simple-ui/SKILL.md).

Cards have titles, fields have labels, stats have values: no subtitles, hints, captions or eyebrows.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCES = [ROOT / 'src/TerraScale.Dashboard', ROOT / 'src/TerraScale.Authentication/Features']
SUFFIXES = {'.cshtml', '.cs', '.js', '.mjs', '.css'}
SKIP = ('node_modules', 'bin', 'obj', 'static', 'vendor')
BANNED = re.compile(r'\bts-(hint|card-description|empty-detail|stat-detail|eyebrow|lede)\b')

failures = []
for source in SOURCES:
    for path in source.rglob('*'):
        if path.suffix not in SUFFIXES or any(part in SKIP for part in path.relative_to(ROOT).parts):
            continue
        for number, line in enumerate(path.read_text(encoding='utf-8').splitlines(), 1):
            if match := BANNED.search(line):
                failures.append(f'{path.relative_to(ROOT)}:{number}: {match.group(0)}')

if failures:
    print('Helper text is banned by the simple-ui standard; fix the layout instead:', file=sys.stderr)
    print('\n'.join(failures), file=sys.stderr)
    sys.exit(1)
