#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
生成 64 号系列文档的总览页。

为什么单独做成工具而不是写在 shell 里：
  之前用 `grep -c '^### `' docs/64-*.md` 统计文件数，
  传多个文件时 grep 会输出「文件名:计数」多行，
  塞进 markdown 表格就变成了错乱的「0\n0」。
  统计与格式化交给 Python，一步到位。
"""
import io
import os
import re
import subprocess
import sys

sys.stdout.reconfigure(encoding='utf-8')
NL = chr(10)
BT = chr(96)
DOCS = 'E:/text/reshadow/docs'
REPO = 'E:/text/reshadow/game'

PAT = re.compile(r'^### ' + BT + r'([^' + BT + r']+)' + BT, re.M)

VOLS = [
    ('64-A-AST', 'AST（抽象语法与效果节点）', '想看效果节点数据结构的人'),
    ('64-B-英文编译器', '英文编译器（English → AST）', '想看 English → AST 编译流程的人'),
    ('64-C-游戏引擎', '游戏引擎（规则 / 对局 / VM / 卡包）', '想看规则实现、对局推进、VM 指令集的人'),
    ('64-D-前端UI', '前端 UI（Bridge / 主题 / 控件 / 卡组 / 战斗）', '想看 Godot 界面层、前后端分离做法的人'),
    ('64-E-DIY系统', 'DIY 系统（四端同构造卡）', '想看积木/表单/脚本/CNL 四端互通的人'),
    ('64-F-工具与数据', '工具与数据（构建脚本 / Python 工具 / 卡池配置）', '想复现构建、了解卡池配置格式的人'),
    ('64-G-测试与门禁', '测试与门禁（回归 / 文档验收 / 矩阵）', '想核查测试是否真在测东西的人'),
    ('64-H-附录', '附录（设计原文与历史资料）', '需要设计原文与历史审计记录的人'),
]


def fmt_size(n):
    return '%.0f KB' % (n / 1024) if n < 1048576 else '%.1f MB' % (n / 1048576)


def main():
    commit = subprocess.run(
        ['git', '-C', REPO, 'log', '-1', '--format=%h %s'],
        capture_output=True, text=True, encoding='utf-8').stdout.strip()

    out = ['# 64 · Reshadow 分册总览', '']
    out.append('> 提交：' + BT + commit + BT)
    out.append('')
    out.append('代码按模块拆成 8 册，**同一份代码只出现在一册里**，可以按需分发。')
    out.append('')
    out.append('| 分册 | 内容 | 文件 | 体积 |')
    out.append('|---|---|---|---|')

    total = 0
    for key, desc, _ in VOLS:
        p = os.path.join(DOCS, key + '.md')
        if not os.path.exists(p):
            continue
        s = io.open(p, encoding='utf-8').read()
        n = len(PAT.findall(s))
        total += n
        out.append('| ' + BT + key + BT + ' | ' + desc + ' | ' + str(n) + ' | '
                   + fmt_size(os.path.getsize(p)) + ' |')

    out.append('| **合计** | | **' + str(total) + '** | |')
    out.append('')
    out.append('## 适合发给谁')
    out.append('')
    out.append('| 分册 | 适合 |')
    out.append('|---|---|')
    for key, _, who in VOLS:
        out.append('| ' + BT + key + BT + ' | ' + who + ' |')

    io.open(os.path.join(DOCS, '64-总览.md'), 'w', encoding='utf-8',
            newline=NL).write(NL.join(out) + NL)
    print('总览已生成，合计 ' + str(total) + ' 个文件')


if __name__ == '__main__':
    main()