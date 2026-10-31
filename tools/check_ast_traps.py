#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""静态拦截编码 Agent 常见防呆陷阱。

每条规则都对应仓库里真实发生过的 Bug，不是泛泛的风格检查：
  陷阱 1  对 TargetSpec 静态属性用 ==/!=（引用判等恒假，死代码）
  陷阱 2  UnitState / StatKey 新增枚举值却没在 VmEval 里实现求值
  陷阱 3  TargetSpec 的克隆点漏拷字段（WithWhere / OmittedTargetSpec /
          NormalizeAllCount / InheritTarget）
  陷阱 4  在词法期之后把 Token 拼成字符串做子串匹配
  陷阱 5  无边界吞词 while (!c.IsAtEnd) c.Advance();

用法：python tools/check_ast_traps.py
"""
import io, os, re, sys

sys.stdout.reconfigure(encoding='utf-8')
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', 'src', 'KernelV3'))

errors = []


def read(rel):
    return io.open(os.path.join(ROOT, rel), encoding='utf-8').read()


def cs_files():
    for dp, _, fns in os.walk(ROOT):
        for fn in sorted(fns):
            if fn.endswith('.cs'):
                full = os.path.join(dp, fn)
                yield os.path.relpath(full, ROOT).replace(os.sep, '/'), full


def strip_comment(line):
    """去掉行尾注释，避免注释里的示例被误判为真代码。"""
    idx = line.find('//')
    if idx >= 0:
        line = line[:idx]
    return line.strip()


# ---------------------------------------------------------------- 陷阱 1
# TargetSpec 的静态便捷属性是 => new()，每次访问都是新实例；
# TargetSpec 是 class 且未重载 ==，所以 ==/!= 是引用比较，恒为 false。
TS_STATICS = r'(?:Self|SelfLeader|OpponentLeader|Previous|Created|Trigger|Item)'
RE_TS_EQ = re.compile(r'[=!]=\s*TargetSpec\.' + TS_STATICS + r'\b')

for rel, full in cs_files():
    txt = io.open(full, encoding='utf-8').read()
    for idx, raw in enumerate(txt.splitlines(), 1):
        s = strip_comment(raw)
        if not s or s.startswith('///') or s.startswith('*'):
            continue
        if RE_TS_EQ.search(s):
            errors.append('%s:%d: 禁止对 TargetSpec 静态属性做 ==/!= 引用判等（恒为 false），改按字段判断 | %s' % (rel, idx, s))


# ---------------------------------------------------------------- 陷阱 2
# 枚举新增值必须在求值端有分支，否则编译通过、AST 好看，运行时落进 default 恒假。
ENUM_GUARDS = [
    ('Core/Physics.cs', 'UnitState', 'Vm/VmEval.cs', 'UnitState.',
     {'None', 'EnteredThisTurn', 'Destroyed'}),
    ('Core/Physics.cs', 'StatKey', 'Vm/VmEval.cs', 'StatKey.',
     {'None'}),
]

for enum_rel, enum_name, eval_rel, prefix, exempt in ENUM_GUARDS:
    src = read(enum_rel)
    m = re.search(r'public enum\s+' + enum_name + r'\s*\{(.*?)\}', src, re.S)
    if not m:
        errors.append('%s: 找不到 enum %s（脚本规则过期，请同步更新 check_ast_traps.py）' % (enum_rel, enum_name))
        continue
    body = re.sub(r'///.*', '', m.group(1))
    body = re.sub(r'//.*', '', body)
    values = re.findall(r'^\s*([A-Za-z]\w*)\s*(?:=|,)', body, re.M)
    eval_src = read(eval_rel)
    for v in values:
        if v in exempt:
            continue
        if (prefix + v) not in eval_src:
            errors.append('%s: %s.%s 未在 %s 中实现求值分支（运行时落进 default，恒假/恒 0）' % (eval_rel, enum_name, v, eval_rel))


# ---------------------------------------------------------------- 陷阱 3
# TargetSpec 的每个字段都必须在所有克隆点被显式拷贝。
# 漏一个字段 = 经过一次条件追加 / instead 透镜 / Count 归一化后该字段静默丢失。
ts_src = read('Ast/TargetSpec.cs')
ts_props = set(re.findall(r'public\s+[\w.<>\[\]?]+\s+(\w+)\s*\{\s*get;\s*init;\s*\}', ts_src))

CLONE_POINTS = [
    ('Ast/TargetSpec.cs',
     r'public TargetSpec WithWhere\(BoolExpr extra\)\s*=>\s*new\(\)\s*\{(.*?)\};',
     'WithWhere'),
    ('Lang/ActionParser.cs',
     r'TargetSpec OmittedTargetSpec\(TargetSpec t\)\s*=>\s*new\(\)\s*\{(.*?)\};',
     'OmittedTargetSpec'),
    ('Lang/ActionParser.cs',
     r'TargetSpec NormalizeAllCount\(TargetSpec t\)\s*\{(?:.*?)return new TargetSpec\s*\{(.*?)\};',
     'NormalizeAllCount'),
    ('Lang/InsteadWeaver.cs',
     r'TargetSpec InheritTarget\(TargetSpec own, TargetSpec prev\)(?:.*?)return new TargetSpec\s*\{(.*?)\};',
     'InheritTarget'),
]

CLONE_EXEMPT = {
    'WithWhere': {'Where'},
    'OmittedTargetSpec': set(),
    'NormalizeAllCount': {'Count'},
    'InheritTarget': set(),
}

for rel, pattern, label in CLONE_POINTS:
    src = read(rel)
    m = re.search(pattern, src, re.S)
    if not m:
        errors.append('%s: 找不到克隆点 %s（脚本规则过期，请同步更新 check_ast_traps.py）' % (rel, label))
        continue
    body = m.group(1)
    copied = set(re.findall(r'\b(\w+)\s*=', body))
    missing = ts_props - copied - CLONE_EXEMPT[label]
    if missing:
        errors.append('%s: %s 漏拷贝字段 %s（经过该透镜后字段会静默丢失）' % (rel, label, sorted(missing)))


# ---------------------------------------------------------------- 陷阱 4
# 词法期之后把 Token 拼成字符串再 Contains/Substring/Regex，
# 会连兵种名、卡种名、归属限定词一起吞掉。
LANG_FILES = ['Lang/TargetParser.cs', 'Lang/ActionParser.cs',
              'Lang/ConditionCompiler.cs', 'Lang/PrefixParser.cs']
RE_SB = re.compile(r'new\s+(?:System\.Text\.)?StringBuilder\(\)')
RE_SUBSTR = re.compile(r'\.(?:Contains|StartsWith|EndsWith|Substring|IndexOf)\s*\(')

for rel in LANG_FILES:
    lines = read(rel).splitlines()
    for idx, raw in enumerate(lines, 1):
        s = strip_comment(raw)
        if not RE_SB.search(s):
            continue
        # 只收集名字（卡名 / 纹章名 / 牌组名）并整体交给词法层查表是允许的；
        # 拼完之后再 Contains / Substring / Regex 才是真吞词。
        window = chr(10).join(lines[idx - 1:min(idx + 18, len(lines))])
        if RE_SUBSTR.search(window):
            errors.append('%s:%d: 禁止把 Token 拼成字符串后做子串匹配（会吞掉兵种/卡种/归属限定词） | %s' % (rel, idx, s))


# ---------------------------------------------------------------- 陷阱 5
# 无边界吞词会吃掉句尾的比较方向与后续条件。
RE_SWALLOW = re.compile(r'while\s*\(\s*!\s*\w+\.IsAtEnd\s*\)\s*\w+\.Advance\(\)\s*;')

for rel, full in cs_files():
    if not rel.startswith('Lang/'):
        continue
    txt = io.open(full, encoding='utf-8').read()
    for idx, raw in enumerate(txt.splitlines(), 1):
        s = strip_comment(raw)
        if RE_SWALLOW.search(s):
            errors.append('%s:%d: 无边界吞词（必须在句读/连词边界前停下） | %s' % (rel, idx, s))


# ---------------------------------------------------------------- 报告
if errors:
    print('FAIL: 发现以下违反 AGENT_RULES.md 的代码陷阱：')
    for e in errors:
        print('  - ' + e)
    sys.exit(1)

print('OK: AST 与引擎防呆静态检查全部通过')
