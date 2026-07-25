import glob, sys

def lex_strip(src):
    """Single pass: comments and strings are consumed by whichever starts first.
    Two-phase stripping breaks either way -- '//' inside a string, or an
    apostrophe inside a comment eating the rest of the file as a char literal."""
    out = []
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        nxt = src[i+1] if i+1 < n else ''
        if c == '/' and nxt == '/':
            while i < n and src[i] != '\n': i += 1
            continue
        if c == '/' and nxt == '*':
            i += 2
            while i+1 < n and not (src[i] == '*' and src[i+1] == '/'): i += 1
            i += 2; continue
        if c == '@' and nxt == '"':
            i += 2
            while i < n:
                if src[i] == '"':
                    if i+1 < n and src[i+1] == '"': i += 2; continue
                    i += 1; break
                i += 1
            out.append('""'); continue
        if c == '$' and nxt == '"':
            # interpolated: keep brace contents, they are real code
            i += 2; out.append('"')
            depth = 0
            while i < n:
                ch = src[i]
                if ch == '\\': i += 2; continue
                if ch == '{':
                    if i+1 < n and src[i+1] == '{': i += 2; continue
                    depth += 1; out.append('{'); i += 1; continue
                if ch == '}':
                    if depth > 0: depth -= 1; out.append('}')
                    i += 1; continue
                if ch == '"' and depth == 0: i += 1; break
                if depth > 0: out.append(ch)
                i += 1
            out.append('"'); continue
        if c == '"':
            i += 1
            while i < n:
                if src[i] == '\\': i += 2; continue
                if src[i] == '"': i += 1; break
                i += 1
            out.append('""'); continue
        if c == "'":
            j = i + 1
            if j < n and src[j] == '\\': j += 2
            else: j += 1
            if j < n and src[j] == "'":
                out.append("''"); i = j + 1; continue
            out.append(' '); i += 1; continue   # stray apostrophe, not a char literal
        out.append(c); i += 1
    return ''.join(out)

pairs = {'(':')', '[':']', '{':'}'}
closers = {v:k for k,v in pairs.items()}
bad = 0
for path in sorted(glob.glob(sys.argv[1] if len(sys.argv) > 1 else '*.cs')):
    src = lex_strip(open(path, encoding='utf-8').read())
    stack, ok = [], True
    for idx, ch in enumerate(src):
        if ch in pairs: stack.append((ch, idx))
        elif ch in closers:
            if not stack or stack[-1][0] != closers[ch]:
                print(f'{path}: unexpected {ch!r} near line {src[:idx].count(chr(10))+1}'); ok = False; bad = 1; break
            stack.pop()
    if ok and stack:
        ch, idx = stack[-1]
        print(f'{path}: unclosed {ch!r} opened near line {src[:idx].count(chr(10))+1}'); bad = 1
    elif ok:
        print(f'{path}: OK')
sys.exit(bad)
