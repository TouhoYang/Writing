# -*- coding: utf-8 -*-
"""构建前静态检查 mobile/index.html。

为什么需要这个脚本
------------------
每次「回滚功能」都要大段删除代码,而**删除很容易连带切掉别的函数定义或事件绑定**。
这个项目已经因此踩了 3 次,而且每次的后果都是「点了没反应」这种最难查的症状:

  1. createTreeFile() 定义被删 → 「新建 txt」抛 ReferenceError
  2. btnNewFile 的 onclick 绑定被删 → 按钮彻底静默(连异常都没有)
  3. notTextFile() 定义被删 → 点击文件抛 ReferenceError,编辑区不加载

**这些错误 hvigor 构建时一律不报** —— 打包照常成功,只有运行时点到那条路径才炸。
所以必须在构建前静态扫一遍。

检查项
------
  A. 语法:每个 <script> 块是否能用 JS 引擎解析
  B. 漏定义:被调用的名字是否有定义(排除内建/库/参数)
  C. 少绑定:每个 <button id=...> 是否有 onclick 绑定

用法
----
  python check_mobile.py [index.html 路径]

退出码 0 = 通过;非 0 = 有问题(输出会指出具体位置)。
"""
import io
import os
import re
import subprocess
import sys
import tempfile

DEFAULT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'index.html')

# 语言内建 + 第三方库 + 语法关键字(正则区分不了,白名单掉)
WHITELIST = {
    # 关键字
    'if', 'for', 'while', 'switch', 'catch', 'return', 'typeof', 'new', 'delete', 'do',
    'else', 'in', 'of', 'case', 'throw', 'void', 'instanceof', 'await', 'yield',
    'function', 'this', 'super',
    # 语言内建
    'Array', 'Object', 'String', 'Number', 'Boolean', 'Math', 'JSON', 'Date', 'RegExp',
    'Error', 'TypeError', 'RangeError', 'Promise', 'Map', 'Set', 'WeakMap', 'WeakSet',
    'Symbol', 'Proxy', 'Reflect', 'Function', 'parseInt', 'parseFloat', 'isNaN',
    'isFinite', 'encodeURIComponent', 'decodeURIComponent', 'encodeURI', 'decodeURI',
    'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'requestAnimationFrame',
    'cancelAnimationFrame', 'alert', 'confirm', 'prompt', 'atob', 'btoa', 'escape',
    'unescape', 'structuredClone', 'queueMicrotask', 'fetch', 'getComputedStyle',
    'matchMedia', 'Uint8Array', 'Int8Array', 'Uint16Array', 'Uint32Array', 'Float32Array',
    'Float64Array', 'ArrayBuffer', 'DataView', 'TextEncoder', 'TextDecoder', 'Blob',
    'File', 'FileReader', 'URL', 'URLSearchParams', 'AbortController', 'CustomEvent',
    'Event', 'DOMParser', 'Intl', 'BigInt', 'Image', 'Audio', 'Notification',
    # 内联的第三方库
    'Buffer', 'git', 'fflate',
    # 值被当函数调用 / 正则边界情况(人工确认过,不是漏定义)
    'GB18030', 'Git', 'objects', 'localStorage', 'promise', 'resolve', 'reject',
    'cb', 'fn', 'onOk', 'validate', 'onFail', 'request', 'response', 'entry', 'entries',
    'host', 'path', 'stal', 'change', 'commit', 'ensure', 'chain', 'b', 'ar', 'Strin',
    'Promis', 'JSO', '_readFile', 'isDirectory', 'isFile', 'stat', 'lstat', 'readdir',
    'readFile', 'writeFile',
}

failures = []


def check_syntax(path, blocks):
    """A. 每个 script 块用 node 做语法检查(有 node 就查)"""
    node = None
    for cand in [
        r'D:\DevEco Studio\tools\node\node.exe',
        'node',
    ]:
        try:
            subprocess.run([cand, '--version'], capture_output=True, timeout=10)
            node = cand
            break
        except Exception:
            continue
    if node is None:
        print('  [跳过] 没找到 node,不检查语法')
        return

    for i, blk in enumerate(blocks):
        fd, tmp = tempfile.mkstemp(suffix='.js')
        os.close(fd)
        try:
            io.open(tmp, 'w', encoding='utf-8', newline='').write(blk)
            r = subprocess.run([node, '--check', tmp], capture_output=True, timeout=60)
            if r.returncode != 0:
                err = r.stderr.decode('utf-8', 'replace').strip().splitlines()
                failures.append('script 块 #%d 语法错误:\n      %s'
                                % (i, '\n      '.join(err[:6])))
                print('  [失败] script 块 #%d (%d 字符)' % (i, len(blk)))
            else:
                print('  [通过] script 块 #%d (%d 字符)' % (i, len(blk)))
        finally:
            try:
                os.remove(tmp)
            except OSError:
                pass


def check_undefined(app):
    """B. 被调用但没定义的名字"""
    calls = {}
    for m in re.finditer(r'(?<![.\w$?])([A-Za-z_$][\w$]*)\s*\(', app):
        calls.setdefault(m.group(1), []).append(app[:m.start()].count('\n') + 1)

    known = set()
    known |= set(re.findall(r'\bfunction\s+([A-Za-z_$][\w$]*)\s*\(', app))
    known |= set(re.findall(r'\b(?:var|let|const)\s+([A-Za-z_$][\w$]*)\s*=', app))
    known |= set(re.findall(r'(?<![.\w$])([A-Za-z_$][\w$]*)\s*=\s*(?:function|\(|[^=])', app))
    known |= set(re.findall(r'window\.([A-Za-z_$][\w$]*)\s*=', app))
    for params in re.findall(r'\bfunction\s*[A-Za-z_$\w]*\s*\(([^)]*)\)', app):
        for p in params.split(','):
            p = p.strip()
            if re.fullmatch(r'[A-Za-z_$][\w$]*', p):
                known.add(p)
    known |= set(re.findall(r'\bcatch\s*\(\s*([A-Za-z_$][\w$]*)', app))

    missing = sorted(n for n in calls if n not in known and n not in WHITELIST)
    if missing:
        for n in missing:
            failures.append('调用了但没定义: %s()  第 %s 行' % (n, calls[n][:8]))
        print('  [失败] %d 个可疑的未定义调用' % len(missing))
    else:
        print('  [通过] 没有未定义的函数调用')


def check_buttons(html):
    """C. 每个 <button id=...> 是否有 onclick / addEventListener 绑定"""
    btn_ids = re.findall(r'<button[^>]*\bid="([A-Za-z0-9_]+)"', html)
    unbound = []
    for b in btn_ids:
        # getElementById("x").onclick / getElementById('x').onclick
        pat_onclick = re.search(
            r'getElementById\(\s*[\'"]%s[\'"]\s*\)\s*\.\s*onclick' % re.escape(b), html)
        # addEventListener 形式
        pat_listen = re.search(
            r'getElementById\(\s*[\'"]%s[\'"]\s*\)\s*\.\s*addEventListener' % re.escape(b), html)
        # 先取到变量再绑定:var x = document.getElementById("b") ... x.onclick
        pat_var = re.search(
            r'(?:var|let|const)\s+([A-Za-z_$][\w$]*)\s*=\s*document\.getElementById\(\s*[\'"]%s[\'"]\s*\)' % re.escape(b),
            html)
        if pat_var:
            v = pat_var.group(1)
            if re.search(r'(?<![.\w$])%s\s*\.\s*(?:onclick|addEventListener)' % re.escape(v), html):
                continue
        if pat_onclick or pat_listen:
            continue
        # 弹窗内部按钮(umOk/umCancel 等)是在 uiPrompt 里动态取并绑定的
        if b in ('umOk', 'umCancel'):
            continue
        unbound.append(b)

    if unbound:
        failures.append('按钮有 id 但没有绑定点击事件: %s' % ', '.join(unbound))
        print('  [失败] %d 个按钮没有绑定' % len(unbound))
    else:
        print('  [通过] %d 个按钮都有绑定' % len(btn_ids))


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT
    if not os.path.isfile(path):
        print('找不到文件: %s' % path)
        return 2

    html = io.open(path, encoding='utf-8').read()
    blocks = re.findall(r'<script[^>]*>(.*?)</script>', html, re.S)
    if not blocks:
        print('没有找到 <script> 块')
        return 2

    print('检查 %s' % path)
    print('  script 块 %d 个, 最后一块(应用代码) %d 字符\n' % (len(blocks), len(blocks[-1])))

    print('A. 语法')
    check_syntax(path, blocks)
    print('\nB. 漏定义的函数')
    check_undefined(blocks[-1])
    print('\nC. 按钮绑定')
    check_buttons(html)

    print()
    if failures:
        print('=' * 60)
        print('发现 %d 类问题:' % len(failures))
        for f in failures:
            print('  · %s' % f)
        print('=' * 60)
        print('\n这些错误 hvigor 构建时不会报,但运行时会炸「点了没反应」。')
        return 1
    print('=' * 60)
    print('全部通过')
    print('=' * 60)
    return 0


if __name__ == '__main__':
    sys.exit(main())
