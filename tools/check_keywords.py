import io, os, re, sys

sys.stdout.reconfigure(encoding='utf-8')

ROOT = r'E:/text/reshadow/game/src/KernelV3'
SCHEMA = os.path.join(ROOT, 'Core/GameSchema.cs')
KNOWN_UNUSED = {'attack_limit'}

src = io.open(SCHEMA, encoding='utf-8').read()
m = re.search(r'BuiltinKeywords\s*=\s*(?:new\[\])?\s*\{(.*?)\}', src, re.S)
if not m:
    print('ERROR: cannot locate BuiltinKeywords')
    sys.exit(2)
keywords = re.findall(r'"([^"]+)"', m.group(1))

files = []
for dp, _, fns in os.walk(ROOT):
    for fn in fns:
        if fn.endswith('.cs'):
            files.append(os.path.join(dp, fn))

def code_lines(path):
    """只返回真正的代码行：剔掉 // 行注释与 /* */ 块注释。字符串里的 // 不影响判定。"""
    txt = io.open(path, encoding='utf-8', errors='replace').read()
    txt = re.sub(r'/\*.*?\*/', '', txt, flags=re.S)
    for line in txt.split(chr(10)):
        s = line.lstrip()
        if s.startswith('//'):
            continue
        yield line

rows = []
for kw in keywords:
    reads, writes, mentions = 0, 0, []
    needle = '"%s"' % kw
    for f in files:
        if os.path.basename(f) in ('GameSchema.cs', 'VmSelfTest.cs'):
            continue
        for line in code_lines(f):
            if needle not in line:
                continue
            mentions.append(os.path.basename(f))
            if 'Has(' in line or 'Keyword(' in line or 'KeywordValues' in line:
                reads += 1
            elif 'kw =' in line or 'Keywords.Add' in line or 'Keyword =' in line:
                writes += 1
    rows.append((kw, writes, reads, sorted(set(mentions))))

print('Registered keywords: %d' % len(keywords))
print()
print('%-24s %6s %6s  %s' % ('keyword', 'write', 'read', 'files'))
print('-' * 78)
bad = []
for kw, w, r, ms in rows:
    tag = ''
    if r == 0:
        if kw in KNOWN_UNUSED:
            tag = '[known-unused]'
        elif w > 0:
            tag = '<<< WRITE-ONLY'
            bad.append(kw)
        else:
            tag = '<<< NO READ SITE'
            bad.append(kw)
    print('%-24s %6d %6d  %s %s' % (kw, w, r, ','.join(ms)[:40], tag))

print()
if bad:
    print('FAIL: %d keyword(s) have no read site: %s' % (len(bad), ', '.join(bad)))
    sys.exit(1)
print('OK: every keyword has a read site (or is a known-unused placeholder)')