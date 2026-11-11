#!/usr/bin/env python3
"""Reject helper-text classes removed by the simple-ui standard (.agents/skills/simple-ui/SKILL.md).

Cards have titles, fields have labels, stats have values: no subtitles, hints, captions or eyebrows.
Also reject a Datastar attribute, in single or double quotes, whose Razor value is a C# conditional with a branch that is
null, null!, null as T, default, default(T) or default!, also in a conditional nested in a branch: Razor renders that branch
as an empty value and Datastar throws on it. That check skips Razor and HTML comments and does not follow a conditional
inside a call, a lambda or a switch expression.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCES = [ROOT / 'src/TerraScale.Dashboard', ROOT / 'src/TerraScale.Authentication/Features']
SUFFIXES = {'.cshtml', '.cs', '.js', '.mjs', '.css'}
SKIP = ('node_modules', 'bin', 'obj', 'static', 'vendor')
BANNED = re.compile(r'\bts-(hint|card-description|empty-detail|stat-detail|eyebrow|lede)\b')
DATASTAR_NAMES = r'(?:init|text|show|effect)(?:__[\w.-]+)*|(?:on|attr|class|computed|style)[-:][\w:.-]+'
DATASTAR_VALUE = re.compile(rf'\s(data-(?:{DATASTAR_NAMES}))=(["\'])@\(')
OPENERS = {'(': ')', '[': ']', '{': '}'}
NULL = re.compile(r'(\([\w.?<>, ]+\)\s*)?null!?|null\s+as\s+[\w.?<>, ]+|default(\([\w.?<>, ]+\))?!?')
COMMENT = re.compile(r'@\*.*?\*@|<!--.*?-->', re.DOTALL)


def is_literal(text, index):
    return text[index] in '"\'' or text[index] in '$@' and text[index + 1:index + 3].lstrip('$@')[:1] == '"'


def literal_end(text, start):
    if text[start] == "'":
        return text.index("'", start + (3 if text[start + 1] == '\\' else 2)) + 1
    quote = start
    while text[quote] in '$@':
        quote += 1
    interpolated = '$' in text[start:quote]
    verbatim = '@' in text[start:quote]
    index = quote + 1
    while index < len(text):
        char = text[index]
        if char == '\\' and not verbatim or char == '"' and verbatim and text[index + 1:index + 2] == '"':
            index += 2
        elif char == '"':
            return index + 1
        elif interpolated and char == '{' and text[index + 1:index + 2] == '{':
            index += 2
        elif interpolated and char == '{':
            index = closing(text, index + 1, '}') + 1
        else:
            index += 1
    raise ValueError(f'unterminated literal at offset {start}')


def closing(text, start, close):
    index = start
    while index < len(text):
        char = text[index]
        if is_literal(text, index):
            index = literal_end(text, index)
        elif char in OPENERS:
            index = closing(text, index + 1, OPENERS[char]) + 1
        elif char == close:
            return index
        else:
            index += 1
    raise ValueError(f'no {close} after offset {start}')


def skeleton(expression):
    parts = []
    index = 0
    while index < len(expression):
        char = expression[index]
        if is_literal(expression, index):
            index = literal_end(expression, index)
            parts.append('""')
        elif expression.startswith('/*', index):
            index = expression.index('*/', index + 2) + 2
        elif char in OPENERS:
            end = closing(expression, index + 1, OPENERS[char])
            parts.append(char + skeleton(expression[index + 1:end]) + OPENERS[char])
            index = end + 1
        else:
            parts.append(char)
            index += 1
    return ''.join(parts)


def top_level(expression):
    depth = 0
    for index, char in enumerate(expression):
        if char in OPENERS:
            depth += 1
        elif char in OPENERS.values():
            depth -= 1
        elif depth == 0:
            yield index, char


def unwrapped(branch):
    branch = branch.strip()
    while branch.startswith('(') and closing(branch, 1, ')') == len(branch) - 1:
        branch = branch[1:-1].strip()
    return branch


def conditional(expression, index):
    following = expression[index + 1:index + 2]
    return expression[index] == '?' and following not in ('?', '.', '[') and expression[index - 1:index] != '?'


def has_null_branch(expression):
    expression = unwrapped(expression)
    question = None
    nested = 0
    for index, char in top_level(expression):
        if question is None:
            question = index if conditional(expression, index) else None
        elif conditional(expression, index):
            nested += 1
        elif char == ':' and ':' not in (expression[index - 1:index], expression[index + 1:index + 2]):
            if nested:
                nested -= 1
                continue
            branches = [unwrapped(expression[question + 1:index]), unwrapped(expression[index + 1:])]
            return any(NULL.fullmatch(branch) or has_null_branch(branch) for branch in branches)
    return False


def null_datastar_values(text):
    text = COMMENT.sub(lambda comment: '\n' * comment.group(0).count('\n'), text)
    for match in DATASTAR_VALUE.finditer(text):
        end = closing(text, match.end(), ')')
        if text[end + 1:end + 2] == match.group(2) and has_null_branch(skeleton(text[match.end():end])):
            yield text.count('\n', 0, match.start(1)) + 1, match.group(1)


failures = []
datastar = []
for source in SOURCES:
    for path in source.rglob('*'):
        if path.suffix not in SUFFIXES or any(part in SKIP for part in path.relative_to(ROOT).parts):
            continue
        text = path.read_text(encoding='utf-8')
        for number, line in enumerate(text.splitlines(), 1):
            if match := BANNED.search(line):
                failures.append(f'{path.relative_to(ROOT)}:{number}: {match.group(0)}')
        if path.suffix == '.cshtml':
            datastar.extend(f'{path.relative_to(ROOT)}:{number}: {name}' for number, name in null_datastar_values(text))

if failures:
    print('Helper text is banned by the simple-ui standard; fix the layout instead:', file=sys.stderr)
    print('\n'.join(failures), file=sys.stderr)
if datastar:
    print('A Datastar attribute that needs a value gets null from a C# conditional;', file=sys.stderr)
    print('Razor renders that branch as an empty value and Datastar throws on it:', file=sys.stderr)
    print('\n'.join(datastar), file=sys.stderr)
if failures or datastar:
    sys.exit(1)
