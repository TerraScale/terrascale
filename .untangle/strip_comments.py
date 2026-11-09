import sys, pathlib

def comment_start(line: str):
    i = 0
    n = len(line)
    while i < n:
        c = line[i]
        if c == '"':
            if i > 0 and line[i-1] == '@':
                i += 1
                while i < n:
                    if line[i] == '"':
                        if i + 1 < n and line[i+1] == '"':
                            i += 2
                            continue
                        break
                    i += 1
            elif line[i:i+3] == '"""':
                depth = 3
                while i + depth < n and line[i+depth] == '"':
                    depth += 1
                close = '"' * depth
                j = line.find(close, i + depth)
                if j == -1:
                    return None
                i = j + depth
                continue
            else:
                i += 1
                while i < n:
                    if line[i] == '\\':
                        i += 2
                        continue
                    if line[i] == '"':
                        break
                    i += 1
            i += 1
            continue
        if c == "'":
            i += 1
            while i < n:
                if line[i] == '\\':
                    i += 2
                    continue
                if line[i] == "'":
                    break
                i += 1
            i += 1
            continue
        if c == '/' and i + 1 < n and line[i+1] == '/' and (i == 0 or line[i-1] != ':'):
            return i
        i += 1
    return None

total = 0
for arg in sys.argv[1:]:
    p = pathlib.Path(arg)
    out = []
    removed = 0
    for line in p.read_text().splitlines():
        idx = comment_start(line)
        if idx is None:
            out.append(line)
            continue
        removed += 1
        stripped = line[:idx].rstrip()
        if stripped:
            out.append(stripped)
    p.write_text('\n'.join(out) + '\n')
    total += removed
    if removed:
        print(f"{p}: {removed}")
print("TOTAL", total)
