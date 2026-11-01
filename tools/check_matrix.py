# -*- coding: utf-8 -*-
"""AST 支持矩阵校验。

背景：四端（英文/脚本/积木/表单）× 三向（正向编译/反向反编译/打印）＝ 12 个落点。
90/91/92 三轮文档审查都抓到同一类问题：加了 A 端忘了 B 端。
靠人脑记 12 个落点必然漏，所以把「哪个节点/字段必须在哪个方法里出现」写成表，用脚本校验。

三类检查：
  A. 节点覆盖：EffectNode 的每个子类，必须在各端的派发方法里出现
  B. 字段往返：反复漏过的字段，必须在各端各方向出现
  C. 分支顺序：特判必须排在通用分支之前（否则写了也走不到）
"""
import io
import re
import sys

ROOT = 'src/KernelV3'

FILES = {
    'block': ROOT + '/Diy/DiyBlockModel.cs',
    'script': ROOT + '/Diy/ReshadowScriptCompiler.cs',
    'form': ROOT + '/Diy/DiyFormModel.cs',
    'print': ROOT + '/Lang/AstEnglishPrinter.cs',
}

SRC = {k: io.open(v, encoding='utf-8').read() for k, v in FILES.items()}

MOD = re.compile(r'^\s*(public|private|internal|protected)\b')


def _skip_literal(text, i):
    """若 text[i] 是字符串/字符字面量/注释起点，返回其后的位置，否则返回 i。

    注释必须一起跳过：注释里出现的 '{' / '}' 会打乱括号配对，
    让方法体的边界算错（本会话已因此误报过一次）。
    """
    if text.startswith('//', i):
        k = text.find(chr(10), i)
        return len(text) if k < 0 else k + 1
    if text[i] == '"':
        j = i + 1
        while j < len(text):
            if text[j] == '\\':
                j += 2
                continue
            if text[j] == '"':
                return j + 1
            j += 1
        return j
    if text[i] == "'":
        j = i + 1
        while j < len(text):
            if text[j] == '\\':
                j += 2
                continue
            if text[j] == "'":
                return j + 1
            j += 1
        return j
    return i


def method_body(src, name, nth=0):
    """取方法体。跳过字符串与字符字面量，避免 '{' / '}' 被误当成代码块。"""
    lines = src.split(chr(10))
    starts = []
    for idx, l in enumerate(lines):
        if not MOD.match(l):
            continue
        # 精确要求「方法名后紧跟参数表」，避免 ParseTargetArgsBare 这种
        # 转发方法被误当成 ParseTargetArgsCore（两者名字互相包含）。
        if not re.search(r'\b' + re.escape(name) + r'\s*\(', l):
            continue
        # name 前必须是分隔符（空格/括号/返回类型后的空格），
        # 不能是别的标识符的一部分。用「名字前紧跟非单词字符或行首」判定。
        seg = l[:l.find(name)]
        if seg and (seg[-1].isalnum() or seg[-1] == '_'):
            continue
        starts.append(idx)
    if len(starts) <= nth:
        return None
    # 同名候选取最长的那一个：转发方法只有一行，真实定义在另一个候选上。
    if len(starts) > 1:
        bodies = []
        for st in starts:
            t = chr(10).join(lines[st:])
            jj = t.find('{')
            if jj < 0:
                bodies.append((len(t), st))
                continue
            dd = 0
            kk = jj
            while kk < len(t):
                if t[kk] == '{':
                    dd += 1
                elif t[kk] == '}':
                    dd -= 1
                    if dd == 0:
                        break
                kk += 1
            bodies.append((kk - jj, st))
        bodies.sort(reverse=True)
        starts = [bodies[0][1]]
    text = chr(10).join(lines[starts[0]:])

    # 参数表右括号
    i = text.find('(')
    depth = 0
    while i < len(text):
        i2 = _skip_literal(text, i)
        if i2 != i:
            i = i2
            continue
        if text[i] == '(':
            depth += 1
        elif text[i] == ')':
            depth -= 1
            if depth == 0:
                break
        i += 1
    if i >= len(text):
        return None

    # 跳空白与行注释：表达式体可能写在注释之后（"签名\n// 说明\n=> ..."）
    j = i + 1
    while j < len(text):
        if text[j] in ' \t\r\n':
            j += 1
            continue
        if text.startswith('//', j):
            k = text.find(chr(10), j)
            j = len(text) if k < 0 else k + 1
            continue
        break

    if text.startswith('=>', j):
        k = j + 2
        depth = 0
        while k < len(text):
            k2 = _skip_literal(text, k)
            if k2 != k:
                k = k2
                continue
            c = text[k]
            if c in '({[':
                depth += 1
            elif c in ')}]':
                depth -= 1
            elif c == ';' and depth == 0:
                return text[j:k + 1]
            k += 1
        return None

    if j < len(text) and text[j] == '{':
        depth = 0
        k = j
        while k < len(text):
            k2 = _skip_literal(text, k)
            if k2 != k:
                k = k2
                continue
            if text[k] == '{':
                depth += 1
            elif text[k] == '}':
                depth -= 1
                if depth == 0:
                    return text[j:k + 1]
            k += 1
    return None


ERRORS = []
CHECKS = [0]


def requires(file_key, method, token, why, nth=0):
    CHECKS[0] += 1
    body = method_body(SRC[file_key], method, nth)
    if body is None:
        ERRORS.append('[缺方法] %s::%s 不存在（%s）' % (file_key, method, why))
        return
    if token not in body:
        ERRORS.append('[缺字段] %s::%s 里没有 %s —— %s' % (file_key, method, token, why))


def order(file_key, method, first, then, why, nth=0):
    CHECKS[0] += 1
    body = method_body(SRC[file_key], method, nth)
    if body is None:
        ERRORS.append('[缺方法] %s::%s（%s）' % (file_key, method, why))
        return
    a, b = body.find(first), body.find(then)
    if a < 0:
        ERRORS.append('[缺分支] %s::%s 里没有 %s —— %s' % (file_key, method, first, why))
    elif b >= 0 and a > b:
        ERRORS.append('[顺序错] %s::%s 里 %s 排在了 %s 之后，永远走不到 —— %s'
                      % (file_key, method, first, then, why))


# ---------------- A. 节点覆盖 ----------------
nodes = sorted(set(re.findall(r'public sealed class (\w+Node) : EffectNode',
                              io.open(ROOT + '/Ast/EffectNode.cs', encoding='utf-8').read())))

# 宏节点（MacroNode）由专门的 MacroExecutor 处理，四端 DIY 不支持它 ——
# 它不是「漏了」，而是「明确不在范围内」。排除要写明理由，
# 否则排除表会变成藏污纳垢的地方。
NOT_IN_DIY = {'MacroNode'}
nodes = [n for n in nodes if n not in NOT_IN_DIY]

NODE_DISPATCH = {
    'block': ('CompileStack', 'Stack'),
    'print': ('PrintNode',),
    'script': ('PrintNode',),
}

for n in nodes:
    for fk, methods in NODE_DISPATCH.items():
        for m in methods:
            requires(fk, m, n, '节点 %s 未在 %s::%s 处理' % (n, fk, m))

# ---------------- B. 字段往返 ----------------
requires('block', 'CompileStack', 'CardIdOf', 'SpawnCard 的 CardId 必须由卡名解析')
requires('block', 'CompileStack', 'CrestDef', '纹章节点必须内嵌子卡定义')
requires('block', 'CompileStack', 'ZoneRef.CrestArea', '纹章落点是纹章区，不是战场')
requires('block', 'Stack', 'sp.CardId', '积木反编译要存卡号兜底')

requires('block', 'CompileStack', 'IsExactCopy', '复制体必须区分 exact')
requires('block', 'Stack', 'CopyOfTarget', '积木反编译要识别复制体')
order('block', 'Stack', 'CopyOfTarget', 'act_summon',
      '复制体分支必须排在通用召唤之前，否则永远走不到')
requires('script', 'PrintNode', 'CopyOfTarget', '脚本打印要识别复制体')
requires('script', 'ParseCopyCard', 'IsExactCopy', '脚本解析要还原 exact')

requires('block', 'CompileStack', 'GrantAbilityNode', '积木编译要处理实体授予能力')
requires('block', 'Stack', 'GrantAbilityNode', '积木反编译要处理实体授予能力')
requires('script', 'PrintNode', 'GrantAbilityNode', '脚本打印要处理实体授予能力')
requires('script', 'ParseGrantEntityAbility', 'GrantAbilityNode', '脚本解析要产出实体授予能力')

requires('block', 'CompileStack', 'InvokeAbilityNode', '积木编译要处理复刻能力')
requires('block', 'Stack', 'InvokeAbilityNode', '积木反编译要处理复刻能力')
requires('script', 'PrintNode', 'InvokeAbilityNode', '脚本打印要处理复刻能力')
requires('script', 'PrintNode', 'ia.Target', 'Invoke 的 Target 必须打印')
requires('script', 'ParseInvoke', 'Target = tgt', 'Invoke 的 Target 必须解析')

requires('block', 'CompileStack', 'ReanimateHelper', '亡者召还必须用唯一标准构造')
requires('block', 'Stack', 'ReanimateHelper', '亡者召还反编译要取费用上限')
requires('script', 'PrintNode', 'ReanimateHelper', '脚本打印要用费用上限而不是 Count')
requires('script', 'ParseReanimate', 'ReanimateHelper', '脚本解析要用唯一标准构造')
requires('form', 'RowToNode', 'ReanimateHelper', '表单也必须用唯一标准构造')

requires('block', 'CompileStack', 'ParseDuration', '积木编译要还原 Duration')
requires('block', 'Stack', 'Duration', '积木反编译要保存 Duration')
requires('script', 'PrintNode', 'DurArg', '脚本打印要输出 Duration')
requires('script', 'ParseBuff', 'TryParseDur', 'buff 要读 Duration')
requires('script', 'ParseSetStats', 'TryParseDur', 'set_stats 要读 Duration')
requires('script', 'ParseGrantKw', 'TryParseDur', 'grant_kw 要读 Duration')

requires('block', 'CompileHat', 'SecondaryEvent', '并列事件是运行期判定依据')
requires('block', 'Hat', 'SecondaryEvent', '积木反编译要保存并列事件')
requires('script', 'ParseOnBlock', 'SecondaryEvent', '脚本解析要还原并列事件')
requires('script', 'PrintAbility', 'SecondaryEvent', '脚本打印要输出并列事件')
requires('block', 'CompileHat', 'MaxTriggersPerTurn', '每回合次数上限必须还原')
requires('block', 'Hat', 'MaxTriggersPerTurn', '积木反编译要保存次数上限')
requires('script', 'ParseOnBlock', 'MaxTriggersPerTurn', '脚本解析要还原次数上限')
requires('script', 'PrintAbility', 'MaxTriggersPerTurn', '脚本打印要输出次数上限')

requires('block', 'CompileHat', 'Label', '代价的种类必须写回 Label')
requires('block', 'Hat', 'Label', '积木反编译要读 Label')
requires('script', 'PrintCost', 'Label', '脚本打印要输出 Label')
requires('script', 'ParseCostSpec', 'Label', '脚本解析要写回 Label')
requires('form', 'Cost', 'Label', '表单代价必须填 Label')

requires('block', 'CompileCond', 'CompileTarget(b.Target)', '布尔条件的 Of 必须还原')
requires('block', 'Cond', 'hs.Of', '布尔条件反编译要保存 Of')
requires('script', 'PrintBoolExpr', 'has_state', '脚本要打印状态条件的 Of')

requires('block', 'CompileTarget', 'Sort', '目标筛选的排序必须还原')
requires('block', 'Target', 'Sort', '目标筛选反编译要保存排序')
requires('script', 'ParseTargetArgsCore', 'Sort', '脚本解析要还原排序')
requires('script', 'PrintTargetArgs', 'Sort', '脚本打印要输出排序')

requires('form', 'RowToNode', 'CardIdOf', '表单召唤/变身必须填卡号')
requires('form', 'RowToNode', 'ZoneRef.CrestArea', '表单纹章落点是纹章区')
requires('form', 'RowToNode', 'IntoCardId', '表单变身必须填目标卡号')
requires('form', 'FormAbilityToDef', 'CtrGe', '门槛必须生成同源条件')
requires('form', 'TryCardDefToForm', 'HasFuse', '含融合声明的卡必须显式拒绝')

# ---------------- D. 重复分派（同一个 opcode 在同一 switch 里出现两次） ----------------
# 两份实现并存比「漏改一端」更危险：后改的那份看起来对了，实际走不到，
# 而往返测试还会告诉你「等价」（因为正反两端都用了同一份错实现）。
from collections import Counter


def switch_blocks(src):
    """切出所有 switch 表达式的块体（{ 到配对 }）。

    同一个 switch 只会被扫到一次：记录已覆盖的起点，跳过落在已知块里的 match
    （例如 "switch (x)" 后面还有一层嵌套 switch 时，外层已把它包含进来）。
    """
    out = []
    covered_until = -1
    for m in re.finditer(r'switch\b', src):
        if m.start() < covered_until:
            continue
        i = src.find('{', m.start())
        if i < 0:
            continue
        d = 0
        k = i
        while k < len(src):
            if src[k] == '{':
                d += 1
            elif src[k] == '}':
                d -= 1
                if d == 0:
                    break
            k += 1
        out.append((i, src[i:k + 1]))
        covered_until = k + 1
    return out


SWITCH_FILES = ('block', 'script', 'form')
for fk in SWITCH_FILES:
    for pos, block in switch_blocks(SRC[fk]):
        ops = re.findall(r'"([a-z_0-9]{3,})"(?:\s+or\s+"([^"]+)")?\s*=>', block)
        flat = []
        for a, b in ops:
            flat.append(a)
            if b:
                flat.append(b)
        CHECKS[0] += 1
        dup = {x: n for x, n in Counter(flat).items() if n > 1}
        if dup:
            line = SRC[fk][:pos].count(chr(10)) + 1
            ERRORS.append('[重复分派] %s 第 %d 行的 switch 里同一 opcode 出现多次：%s '
                          '—— 后一份永远走不到' % (fk, line, dup))


# ---------------- 输出 ----------------
if ERRORS:
    report = '=== AST 支持矩阵：%d 项检查，%d 处不合规 ===' % (CHECKS[0], len(ERRORS))
    report += chr(10) + chr(10).join('  ' + e for e in ERRORS)
    io.open('.matrix_report.txt', 'w', encoding='utf-8', newline=chr(10)).write(report)
    print('FAILED: %d of %d checks' % (len(ERRORS), CHECKS[0]))
    sys.exit(1)

print('PASSED: %d checks' % CHECKS[0])
