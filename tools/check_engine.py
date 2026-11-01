#!/usr/bin/env python
"""引擎一致性静态检查。

这些检查针对的是同一类缺陷：
  写入端完整、读取端缺失，而且没有任何测试覆盖。
已经出现过四次：
  GrantCounterAbility 存了不扫、纹章容量漏分支、
  IsCrest 加了不用、cannot_destroy/cannot_attack 只写不读。

用法：python tools/check_engine.py
"""
import subprocess, sys, os

sys.stdout.reconfigure(encoding='utf-8')
HERE = os.path.dirname(os.path.abspath(__file__))

# check_ast_traps.py 针对**写法层面的防呆陷阱**：
#   == 对 TargetSpec 静态属性做引用判等、枚举新增值没有求值分支、
#   TargetSpec 克隆点漏拷字段、拼字符串做子串匹配、无边界吞词。
# 这几类问题的共同点是「编译通过、单测也绿，但运行时恒假或静默吞词」。
# check_matrix.py 针对**覆盖矩阵**：
#   EffectNode 的每个子类、反复漏过的每个字段，必须在四端（英文/脚本/积木/表单）
#   的三个方向（正向编译/反向反编译/打印）里都出现。
# 90/91/92 三轮文档审查都抓到同一类问题：加了 A 端忘了 B 端。
# 四端 × 三向 = 12 个落点，靠人脑记必然漏，所以写成表用脚本校验。
CHECKS = ['check_keywords.py', 'check_zones.py', 'check_ast_traps.py', 'check_matrix.py']

failed = []
for name in CHECKS:
    path = os.path.join(HERE, name)
    print('=' * 70)
    print('运行 ' + name)
    print('=' * 70)
    rc = subprocess.call([sys.executable, path])
    if rc != 0:
        failed.append(name)
    print()

if failed:
    print('FAILED: %s' % ', '.join(failed))
    sys.exit(1)
print('ALL ENGINE CHECKS PASSED')