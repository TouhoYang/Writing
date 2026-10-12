# -*- coding: utf-8 -*-
"""构建辅助:解析版本号并写入 AppScope/app.json5。

为什么用 Python 而不是直接写在 ps1 里
------------------------------------
PowerShell 5.1 按 ANSI 读取 .ps1,脚本里带中文会乱码;
而 app.json5 里的 app_name 是中文。用 Python 处理文件读写可以
显式指定 UTF-8,不受宿主控制台编码影响。

子命令
------
  parse  <版本> <渠道>
      校验版本与渠道,算出 versionCode / versionName / 产物文件名。
      输出 KEY=VALUE 每行一个,供 ps1 读取。

  stamp  <版本> <渠道>
      把版本写进 AppScope/app.json5(保留原格式,只改两个值)。

  restore
      从 .app.json5.bak 还原(构建脚本结束时调用)。

版本规则
--------
  "0.1"   -> versionName "0.1.0", versionCode 100
  "1.2.3" -> versionName "1.2.3", versionCode 10203
  versionCode = major*10000 + minor*100 + patch   (每段 0..99)

渠道
----
  rel   -> versionName "0.1.0"        产物 写作-鸿蒙-0.1.0.hap
  trial -> versionName "0.1.0-trial"  产物 写作-鸿蒙-0.1.0-trial.hap
"""
import io
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
APP_JSON = os.path.join(HERE, 'harmony', 'AppScope', 'app.json5')
BACKUP = APP_JSON + '.bak'

APP_NAME = u'写作'          # 产物文件名里的应用名
CHANNELS = ('rel', 'trial')
CHANNEL_LABEL = {'rel': u'', 'trial': u'-trial'}


def die(msg):
    sys.stderr.write(u'错误: %s\n' % msg)
    sys.exit(2)


def parse_version(v):
    """把 '0.1' / '1.2.3' 规范化成 (versionName, versionCode)。"""
    s = (v or '').strip()
    if not s:
        die(u'版本号不能为空,例如 0.1 或 1.2.3')
    if not re.fullmatch(r'\d+(\.\d+){0,2}', s):
        die(u'版本号格式不对: %s\n  只支持 主.次.修订 三段数字,例如 0.1 或 1.2.3' % s)
    parts = [int(x) for x in s.split('.')]
    while len(parts) < 3:
        parts.append(0)
    major, minor, patch = parts
    for name, n in (('主', major), ('次', minor), ('修订', patch)):
        if n > 99:
            die(u'%s版本号段 %d 超过 99,请改用更大的主版本号' % (name, n))
    name = u'%d.%d.%d' % (major, minor, patch)
    code = major * 10000 + minor * 100 + patch
    return name, code


def parse_channel(c):
    s = (c or 'rel').strip().lower()
    if s in ('rel', 'release'):
        return 'rel'
    if s in ('trial', 'test', 'beta'):
        return 'trial'
    die(u'渠道只能是 rel 或 trial,收到: %s' % c)


def info(version, channel):
    name, code = parse_version(version)
    ch = parse_channel(channel)
    full = name + CHANNEL_LABEL[ch]
    return {
        'VERSION_NAME': full,
        'VERSION_CODE': str(code),
        'CHANNEL': ch,
        'HAP_NAME': u'%s-鸿蒙-%s.hap' % (APP_NAME, full),
    }


def read_app_json():
    if not os.path.isfile(APP_JSON):
        die(u'找不到 %s' % APP_JSON)
    return io.open(APP_JSON, encoding='utf-8').read()


def stamp(version, channel):
    """把版本写进 app.json5,先备份一次。"""
    d = info(version, channel)
    text = read_app_json()
    if not os.path.isfile(BACKUP):
        io.open(BACKUP, 'w', encoding='utf-8', newline='').write(text)

    new = re.sub(r'("versionCode"\s*:\s*)\d+', r'\g<1>' + d['VERSION_CODE'], text, count=1)
    new = re.sub(r'("versionName"\s*:\s*)"[^"]*"',
                 r'\g<1>"' + d['VERSION_NAME'] + '"', new, count=1)
    if new == text:
        # 值没变(可能重复打同一个版本),也算成功
        pass
    io.open(APP_JSON, 'w', encoding='utf-8', newline='').write(new)
    return d


def restore():
    if os.path.isfile(BACKUP):
        io.open(APP_JSON, 'w', encoding='utf-8', newline='').write(
            io.open(BACKUP, encoding='utf-8').read())
        os.remove(BACKUP)
        return True
    return False


def main():
    if len(sys.argv) < 2:
        die(u'用法: version_tool.py parse|stamp|restore [版本] [渠道]')
    cmd = sys.argv[1]

    if cmd == 'parse':
        if len(sys.argv) < 3:
            die(u'parse 需要版本号')
        d = info(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else 'rel')
        for k in ('VERSION_NAME', 'VERSION_CODE', 'CHANNEL', 'HAP_NAME'):
            print('%s=%s' % (k, d[k]))
        return 0

    if cmd == 'stamp':
        if len(sys.argv) < 3:
            die(u'stamp 需要版本号')
        d = stamp(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else 'rel')
        print('OK versionName=%s versionCode=%s' % (d['VERSION_NAME'], d['VERSION_CODE']))
        return 0

    if cmd == 'restore':
        print('OK restored' if restore() else 'OK nothing-to-restore')
        return 0

    die(u'未知子命令: %s' % cmd)


if __name__ == '__main__':
    sys.exit(main())
