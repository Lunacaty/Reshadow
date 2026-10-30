#!/usr/bin/env python3
"""
pack_repo.py —— 把仓库里「被 git 追踪的文件」打包成单个 Markdown 文档。

用途：把代码与数据塞进一份文档，交给看不到仓库的 AI 阅读。

设计要点：
  · 只用 git ls-files 枚举，因此 .gitignore 已排除的脏数据不会混进来
  · 逐个文件检测二进制（含 NUL 字节即跳过），不会把图片糊进文档
  · 按扩展名给代码块加语言标记，渲染更准
  · --json-sample 让大型 JSON 只保留前 N 条记录，结构完整但体积可控
  · 自动统计体积并估算 token，超阈值时警告并给出更窄筛选的建议

用法：
  python tools/pack_repo.py                             # 全量
  python tools/pack_repo.py --exclude "data/*"          # 只要代码
  python tools/pack_repo.py --json-sample 3             # 大数据只留 3 条样例
  python tools/pack_repo.py --skip-large 200000         # 跳过大于 200KB 的文件
"""

import argparse
import fnmatch
import io
import json as _json
import os
import subprocess
import sys

LANG = {
    '.cs': 'csharp', '.py': 'python', '.js': 'javascript', '.ts': 'typescript',
    '.json': 'json', '.md': 'markdown', '.xml': 'xml', '.yml': 'yaml',
    '.yaml': 'yaml', '.toml': 'toml', '.sh': 'bash', '.ps1': 'powershell',
    '.glsl': 'glsl', '.gd': 'gdscript', '.tscn': 'ini', '.tres': 'ini',
    '.csproj': 'xml', '.sln': 'ini', '.cfg': 'ini', '.txt': 'text',
    '.gitignore': 'text', '.editorconfig': 'ini',
}

CODE_EXT = {'.cs', '.py', '.js', '.ts', '.gd', '.glsl', '.sh', '.ps1'}
DATA_EXT = {'.json', '.csv', '.txt'}


def run_git(root, *args):
    r = subprocess.run(['git', '-C', root, *args], capture_output=True, text=True,
                       encoding='utf-8', errors='replace')
    if r.returncode != 0:
        raise RuntimeError('git ' + ' '.join(args) + ' 失败: ' + r.stderr.strip())
    return r.stdout


def list_tracked(root):
    return [p for p in run_git(root, "ls-files").splitlines() if p.strip()]


def is_binary(path):
    try:
        with open(path, 'rb') as f:
            return b"\x00" in f.read(8192)
    except OSError:
        return True


def matched(rel, includes, excludes):
    if includes and not any(fnmatch.fnmatch(rel, p) for p in includes):
        return False
    if excludes and any(fnmatch.fnmatch(rel, p) for p in excludes):
        return False
    return True


def fmt_size(n):
    for unit in ('B', 'KB', 'MB'):
        if n < 1024:
            return ("%d B" % n) if unit == "B" else ("%.1f %s" % (n, unit))
        n /= 1024.0
    return "%.1f GB" % n


def sample_json(text, limit, indent):
    """只保留数组的前 limit 条，其余结构不动。返回 (新文本, 提示或None)。"""
    try:
        d = _json.loads(text)
    except Exception:
        return text, None

    note = None
    if isinstance(d, list) and len(d) > limit:
        note = "数组共 %d 条，此处仅保留前 %d 条" % (len(d), limit)
        d = d[:limit]
    elif isinstance(d, dict) and isinstance(d.get('cards'), list) and len(d['cards']) > limit:
        note = "cards 共 %d 条，此处仅保留前 %d 条" % (len(d["cards"]), limit)
        d = dict(d)
        d['cards'] = d['cards'][:limit]

    if note is None:
        return text, None
    return _json.dumps(d, ensure_ascii=False, indent=indent), note


def build_tree(paths):
    tree = {}
    for p in paths:
        node = tree
        parts = p.split("/")
        for d in parts[:-1]:
            node = node.setdefault(d + "/", {})
        node[parts[-1]] = None

    lines = []

    def walk(node, prefix=""):
        dirs = sorted(k for k in node if k.endswith("/"))
        files = sorted(k for k in node if not k.endswith("/"))
        for d in dirs:
            lines.append(prefix + d)
            walk(node[d], prefix + "  ")
        for f in files:
            lines.append(prefix + f)

    walk(tree)
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser(description='把 git 追踪的文件打包成单个 Markdown 文档')
    ap.add_argument('--root', default='.', help='仓库根目录')
    ap.add_argument('--out', default='repo_pack.md', help='输出文件路径')
    ap.add_argument('--include', action='append', default=[], help='只打包匹配的文件（glob，可重复）')
    ap.add_argument('--exclude', action='append', default=[], help='排除匹配的文件（glob，可重复）')
    ap.add_argument('--skip-large', type=int, default=0, metavar='BYTES',
                    help='跳过大于该字节数的文件（0 表示不跳过）')
    ap.add_argument('--json-sample', type=int, default=0, metavar='N',
                    help='JSON 只保留前 N 条记录（结构不变），0 表示完整保留')
    ap.add_argument('--json-indent', type=int, default=1, help='JSON 采样后的缩进')
    ap.add_argument('--max-bytes', type=int, default=600000, help='警告阈值')
    ap.add_argument('--title', default=None, help='文档标题')
    ap.add_argument('--no-tree', action='store_true', help='不输出目录树')
    ap.add_argument('--quiet', action='store_true')
    args = ap.parse_args()

    root = os.path.abspath(args.root)
    if not os.path.isdir(os.path.join(root, '.git')):
        sys.exit('错误：不是 git 仓库 -> ' + root)

    tracked = list_tracked(root)
    if not tracked:
        sys.exit('错误：仓库没有任何被追踪的文件')

    kept, skipped = [], []
    for rel in tracked:
        if not matched(rel, args.include, args.exclude):
            continue
        full = os.path.join(root, rel)
        if not os.path.isfile(full):
            skipped.append((rel, '文件不存在'))
            continue
        if is_binary(full):
            skipped.append((rel, '二进制'))
            continue
        size = os.path.getsize(full)
        if args.skip_large and size > args.skip_large:
            skipped.append((rel, fmt_size(size) + ' 超过阈值'))
            continue
        kept.append((rel, size))

    if not kept:
        sys.exit('错误：筛选后没有可打包的文件')

    total_bytes = sum(s for _, s in kept)
    code_bytes = sum(s for r, s in kept if os.path.splitext(r)[1] in CODE_EXT)
    data_bytes = sum(s for r, s in kept if os.path.splitext(r)[1] in DATA_EXT)

    title = args.title or (os.path.basename(root) + ' 仓库完整代码与数据')
    head = run_git(root, 'log', '-1', '--format=%h %s').strip()

    out = io.StringIO()
    out.write("# " + title + "\n\n")
    out.write("> 提交：`" + head + "`\n")
    out.write("> 文件数：" + str(len(kept)) + "　总计：" + fmt_size(total_bytes) + "\n")
    if skipped:
        out.write("> 已跳过：" + str(len(skipped)) + " 个文件（见文末）\n")
    out.write("\n")

    if not args.no_tree:
        out.write("## 文件树\n\n```\n")
        out.write(build_tree([r for r, _ in kept]))
        out.write("\n```\n\n")

    out.write("## 文件内容\n\n")
    for rel, size in kept:
        ext = os.path.splitext(rel)[1].lower()
        lang = LANG.get(ext, LANG.get(os.path.basename(rel).lower(), 'text'))
        body = io.open(os.path.join(root, rel), encoding='utf-8', errors='replace').read()
        note = None
        if args.json_sample and ext == '.json':
            body, note = sample_json(body, args.json_sample, args.json_indent)
        if body.endswith('\n'):
            body = body[:-1]
        out.write("### `" + rel + "`\n\n")
        sub = fmt_size(size)
        if note:
            sub += "　·　" + note
        out.write("<sub>" + sub + "</sub>\n\n")
        out.write("```" + lang + "\n" + body + "\n```\n\n")

    if skipped:
        out.write("## 已跳过的文件\n\n| 文件 | 原因 |\n|---|---|\n")
        for rel, why in skipped:
            out.write("| `" + rel + "` | " + why + " |\n")
        out.write("\n")

    text = out.getvalue()
    with io.open(args.out, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)

    nbytes = len(text.encode('utf-8'))
    est_tokens = nbytes // 4

    if not args.quiet:
        print("输出：" + args.out)
        print("  文件   " + str(len(kept)))
        print("  体积   " + fmt_size(nbytes) + "（代码 " + fmt_size(code_bytes)
              + " / 数据 " + fmt_size(data_bytes) + "）")
        print("  约     " + format(est_tokens, ",") + " tokens")
        if skipped:
            print("  跳过   " + str(len(skipped)))
            for rel, why in skipped[:6]:
                print("         " + rel + " —— " + why)
            if len(skipped) > 6:
                print("         …还有 " + str(len(skipped) - 6) + " 个")
        if nbytes > args.max_bytes:
            print("  警告   超过 " + fmt_size(args.max_bytes) + "，AI 可能读不完")
            print("         建议：--json-sample 3  或  --exclude \"data/*\"")


if __name__ == "__main__":
    main()
