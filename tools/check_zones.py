import io, os, re, sys

sys.stdout.reconfigure(encoding='utf-8')

ROOT = r'E:/text/reshadow/game/src/KernelV3'

def fail(msg):
    print('  FAIL: ' + msg)
    return False

print('== \u533a\u57df\u5bb9\u91cf\u68c0\u67e5 ==')

ps = io.open(os.path.join(ROOT, 'Battle/PlayerState.cs'), encoding='utf-8').read()
m = re.search(r'static int Capacity\(ZoneRef z\)\s*=>\s*z switch\s*\{(.*?)\};', ps, re.S)
capped = re.findall(r'ZoneRef\.(\w+)\s*=>', m.group(1)) if m else []
print('  \u6709\u5bb9\u91cf\u4e0a\u9650\u7684\u533a\u57df: %s' % ', '.join(capped))

bs = io.open(os.path.join(ROOT, 'Battle/BattleState.cs'), encoding='utf-8').read().split('\n')

# \u627e\u5230\u6ea2\u51fa\u5757\u7684\u8d77\u70b9\uff0c\u7136\u540e\u7528\u82b1\u62ec\u53f7\u8ba1\u6570\u627e\u5230\u5b83\u7684\u7ed3\u5c3e
start = None
for i, line in enumerate(bs):
    if 'if (cap >= 0 && list.Count >= cap)' in line:
        start = i
        break

depth = 0
end = start
for i in range(start, len(bs)):
    depth += bs[i].count('{') - bs[i].count('}')
    if depth == 0 and i > start:
        end = i
        break

over = '\n'.join(bs[start:end+1])
handled = re.findall(r'zone == ZoneRef\.(\w+)', over)
print('  \u6ea2\u51fa\u5904\u7406\u8986\u76d6: %s' % ', '.join(handled))

ok1 = True
for z in capped:
    if z not in handled:
        ok1 = fail('\u533a\u57df %s \u6709\u5bb9\u91cf\u4e0a\u9650\u4f46\u6ea2\u51fa\u5904\u7406\u7f3a\u5206\u652f' % z)
if ok1:
    print('  OK')

print()
print('== \u533a\u57df\u6620\u5c04\u68c0\u67e5 ==')

m2 = re.search(r'public List<RuntimeEntity> Zone\(ZoneRef z\)\s*=>\s*z switch\s*\{(.*?)\};', ps, re.S)
mapped = re.findall(r'ZoneRef\.(\w+)\s*=>', m2.group(1)) if m2 else []

ph = io.open(os.path.join(ROOT, 'Core/Physics.cs'), encoding='utf-8').read()
em = re.search(r'public enum ZoneRef\s*\{(.*?)\}', ph, re.S)
allz = []
for x in (em.group(1).split(',') if em else []):
    x = x.strip()
    if not x:
        continue
    allz.append(re.sub(r'=.*', '', x).strip())
print('  ZoneRef \u5168\u90e8\u533a\u57df: %s' % ', '.join(allz))

exempt = {'None', 'Leader'}
ok2 = True
for z in allz:
    if z in exempt:
        continue
    if z not in mapped:
        ok2 = fail('\u533a\u57df %s \u5728 Zone() \u91cc\u6ca1\u6709\u6620\u5c04' % z)
if ok2:
    print('  OK')

print()
if not (ok1 and ok2):
    print('FAIL: zone rules inconsistent')
    sys.exit(1)
print('OK: zone rules consistent')
