# 写作 Writing · 移动版（安卓 / 鸿蒙）

把电脑版《写作》的核心体验搬到手机上：**一个 H5 应用做本体，两端各配一层薄原生外壳**。
`mobile/index.html` 是共享本体，安卓与鸿蒙各自把它装进 WebView / ArkWeb 里。

```
mobile/
├─ index.html                    ← 共享本体(H5,含内联的 isomorphic-git / fflate / Buffer polyfill)
├─ README.md                     ← 本文档
├─ check_mobile.py               ← 构建前静态检查(语法/漏定义/按钮绑定),务必先跑
├─ android/                      ← 安卓外壳(Java + WebView)
│  ├─ AndroidManifest.xml
│  ├─ src/com/touho/writing/
│  │  ├─ MainActivity.java        WebView 宿主 + JS 桥
│  │  └─ AndroidFs.java           SAF 文件系统 + Git 桥 + 配置存储
│  └─ build-apk.ps1              ← 一键打 APK
└─ harmony/                      ← 鸿蒙外壳(ArkTS + ArkWeb)
   └─ entry/src/main/
      ├─ ets/pages/Index.ets       ArkWeb 宿主 + 原生桥(全部逻辑都在这一个文件)
      ├─ module.json5              权限声明
      └─ resources/rawfile/
         ├─ index.html             ← 本体副本(构建前同步)
         └─ vendor/                isomorphic-git / fflate 的离线副本
```

## 三种用法

| 方式 | 说明 |
|---|---|
| **浏览器** | 直接打开 `index.html`。稿件存在浏览器 localStorage，可导入/导出 txt；文件夹与 Git 功能不可用（会给出提示） |
| **安卓** | 用 `build-apk.ps1` 打 APK，安装后与电脑版接近 |
| **鸿蒙** | DevEco 构建 HAP，`hdc install` 安装；功能最完整（Git 全套） |

## 功能对照（电脑版 → 移动版）

| 功能 | 移动版 | 说明 |
|---|---|---|
| 码字面板 / 字号 / 自动缩进 / 背景色 | ✅ | 与电脑版一致 |
| 码字速度 / 累计已码 / 净字数 | ✅ | 统计口径与电脑版相同 |
| 多稿件 | ✅ | 存在应用内，可随时导出 |
| 导入 / 导出 txt | ✅ | 导出走系统「保存」对话框 |
| 目录树浏览 | ✅ | **应用私有目录**，见下文 |
| 新建 txt / 新建文件夹 / 改名 / 删除 | ✅ | 删除有二次确认；同名自动加序号 |
| 展开状态记忆 | ✅ | 重开应用原样恢复 |
| Git：多仓库 / 克隆 / 提交 / 推送 / 拉取 / 历史 / 分支 | ✅ | 用 isomorphic-git，纯 JS 实现 |
| Git 令牌管理 | ✅ | 主机名自动从仓库链接推导 |
| **导出全部为 ZIP** | ✅ | `写作数据+时间戳.zip`，一次保存整个应用目录 |
| 导出仓库为 ZIP | ✅ | Git 面板里的「导出仓库文件…」 |
| 目录树 Git 状态着色 | ❌ | 移动版未做 |
| 键盘统计面板 | ❌ | 移动端无物理键盘 |

---

## 目录访问：为什么只能用「应用私有目录」

这是移动版**最反直觉**的一点，也是踩坑最多的部分。结论先说：

> **应用无法把自己的文件放到用户可见目录里常驻。**
> 系统授权是**按文件节点**给的，不是按目录给的。所以仓库和稿件只能放在**应用私有目录**，
> 想拿到「文件管理」里，必须走**导出**。

### 实测证据（鸿蒙）

| 尝试 | 结果 |
|---|---|
| 用系统选择器选一个用户目录，然后 `listFileSync(uri)` | ❌ `13900002 No such file or directory` |
| 在同一目录 `mkdirSync(uri + '/子目录')` | ❌ `13900012 Permission denied` |
| 用 `DocumentViewPicker.save()` 拿到的文件 URI 写入 | ✅ 成功 |
| 同一目录下新建**另一个**文件（不再弹窗） | ❌ `13900002` |
| 同一目录下 `mkdir` | ❌ `13900012` |
| 应用私有目录 `mkdir` / `write` | ✅ 全部成功 |

**结论**：`save()` 只对**用户选中的那一个文件**授权。相邻文件、子目录都不在授权范围内 ——
所以「选一次目录然后批量写」在手机端不可能实现。

### 平台权限层面的原因

- `ohos.permission.READ_WRITE_DOWNLOAD_DIRECTORY` 等目录级权限**仅支持 PC 侧**，手机侧不提供
  （[来源](https://ost.51cto.com/answer/40140)）
- 它们是**受限 ACL 权限**，自动签名申请不到；硬加会导致安装失败 `9568289 grant request permissions failed`
- 手机侧访问文件管理器**只能走 `DocumentViewPicker`**

### 现在的做法

```
应用私有目录（唯一可写位置）
  /data/storage/el2/base/haps/entry/files/       ← 鸿蒙
  （安卓对应 context.getFilesDir()）
  ├─ 稿件.txt
  ├─ 新章节/
  └─ <Git 仓库>/
```

- 工作目录**开机自动设定**，用户不需要（也无法）选择
- **导出**走系统「保存」对话框，把内容写到用户可见位置：
  - 「导出 txt」→ 当前稿件
  - 「导出全部」→ 整个应用目录打包成 `写作数据+<时间戳>.zip`
  - Git 面板「导出仓库文件…」→ 仓库打包成 `<仓库名>-<时间戳>.zip`

> ⚠️ **卸载风险**：私有目录里的文件会随应用卸载一起被系统删除。
> 因此首次启动会有一次**红色标题的提示弹窗**（`⚠ 重要注意事项 ⚠`），
> 提醒用户卸载前先导出或推送到 GitHub。这个提示只弹一次。

---

## 配置持久化（重要：鸿蒙的 localStorage 会丢）

**症状**：鸿蒙版关掉应用再进去，设置和令牌全没了（例如刚更新的 PAT 重启后消失）。

**原因**：`ArkWeb` 的 `localStorage` 在部分鸿蒙机器上「关闭应用再进入」会丢失。这是**已知问题**，
社区有大量相同反馈（[示例](http://bbs.itying.com/topic/676713a2ad669d01bf73491a)）。
反过来，原生 `preferences` 实测**可靠**。

**修法**：两端都把网页配置镜像到原生存储，原生优先。

| | 鸿蒙 | 安卓 |
|---|---|---|
| 原生存储 | `@ohos.data.preferences`（`mazi_config`） | `SharedPreferences`（`mazi_config`） |
| 桥接口 | `configLoad()` / `configSet(k,v)` / `configRemove(k)` | 同名同语义 |

网页侧（`index.html`）：

- `LS.setItem()` 包了一层，写入 localStorage 的同时**镜像到原生**
- ⚠️ **只有 `NATIVE_KEYS` 白名单里的键才会镜像**：
  ```js
  var NATIVE_KEYS = ["mazi_docs_v1", "mazi_cur_v1", "mazi_set_v1", "mazi_chars_v1",
                     "mazi_git_v1", "mazi_tree_v1", "mazi_firstrun_v1"];
  ```
  **新增需要持久化的键时必须加进这个列表**，否则它只存在不可靠的 localStorage 里
  （`mazi_firstrun_v1` 就因为这个漏加，导致首次提示反复弹出）
- 启动时 `syncFromNative()` 先**用原生的值覆盖 localStorage**，再跑原来的初始化
- ⚠️ 但 `docs` / `curId` 是在文件顶部就读 localStorage 的，**那时 `syncFromNative()` 还没跑** ——
  所以 `boot()` 里会**重新加载一次**稿件，否则「上次打开的稿件」会丢
- **老用户升级**：若原生还是空的，`seedNativeFromLocal()` 反向灌进原生，不丢数据

---

## Git（鸿蒙 / 安卓）

手机上没有 `git.exe`，鸿蒙也不允许普通应用执行外部二进制（`@ohos.childProcess` 仅系统应用可用），
所以**不能**像电脑版那样调 `git.exe`。这里改用 **[isomorphic-git](https://isomorphic-git.org/)** ——
纯 JS 的 git 实现，以 UMD 形式**内联进 `index.html`**（`window.git`，72 个 API），完全离线可用。

### 内联的三个库（顺序很重要）

```
Buffer polyfill → fflate(ZIP) → isomorphic-git → 应用代码
```

| 库 | 为什么需要 |
|---|---|
| **buffer**（feross/buffer UMD） | isomorphic-git 依赖 Node 的 `Buffer`，**ArkWeb 里没有**。不内联会报 `Buffer is not defined` |
| **fflate** | 导出 ZIP 用（纯 JS 压缩，无需原生） |
| **isomorphic-git** | git 本体 |

### ⚠️ 适配器必须遵守的约定（每一条都踩过坑）

**1. `readFile` / `read` 默认必须返回 `Buffer`，不是字符串**

isomorphic-git 内部 `t.read()` 实际调用的就是 `FileSystem` 构造时绑定的 `_readFile`
（即适配器的 `readFile`），而它读 `.git/index` 和 packfile 的 `.idx`/`.pack` 时**不传 encoding**。

如果默认返回字符串：
- `.idx` 的魔数校验 `"ff744f63"` 失败 → `Pt.fromIdx` **静默返回 `undefined`**
  → 调用方读 `.error` 崩掉：`Cannot read properties of undefined (reading 'error')`
- `.git/index` 变成字符串 → `invalid type passed to GitIndex.from`

只有显式传 `{encoding:'utf8'}` 时才该给字符串。

**2. `stat` / `lstat` 必须返回 Node 风格的 `fs.Stats`**

要带 `isDirectory()` / `isFile()` 这些**方法**，不能只返回 `{type, size, mtimeMs}` 对象，
否则克隆第一步就抛 `r.isDirectory is not a function`（打包版在 5 处以上调用）。

**3. 二进制读写要保证字节完全一致**

- 读：原生**一律**返回 `"b64:<base64>"`，由网页端按需解码。
  **不要**靠"内容里有没有 `0x00`"来猜文本/二进制 ——
  zlib 压缩过的 git 对象里几乎没有 0 字节，会被误判成文本而遭 UTF-8 破坏，
  报 `incorrect header check`
- 写：传字节体给 `@ohos.net.http` 要用 **`ArrayBuffer`**；
  手动加 `content-length` 反而会让它报 `2300999 Internal error`

**4. ⚠️ `decodeSync('')` 会抛异常**

`Base64Helper.decodeSync('')` 报 `Parameter error ... length greater than 0`。
**空文件到处都是**（占位文件、刚建的稿子），
所以所有 `b64:x` 的解码点都必须先判断 `x` 是否为空串。

这个陷阱在我们的代码里**反复出现 4 次**，务必注意：

| 位置 | 症状 |
|---|---|
| `gitWrite` 前缀判断用了 `data.length > 6` | 空文件里被写入字面量 `b64:` |
| `saveB64` 解码 | 导出空文件报「内容解码失败」 |
| `gitReadB64` 空文件返回空串 | 「导出全部」跳过所有空文件 |
| `gitWrite` 解码 | 克隆遇到空文件**整批失败**（`There are multiple errors...`） |

**约定**：`"b64:"` = 成功且内容为 0 字节；`"b64:xxx"` = 成功且有内容；`""` = 失败。

### ⚠️ `http` 适配器的两个坑（都会表现为 `Empty response from git server`）

isomorphic-git 的 `body` 在**两个方向上都是「Uint8Array 分块数组」**，不是单个 `Uint8Array`：

1. **响应 `body` 必须是分块数组**。若直接给一个 `Uint8Array`，`getIterator()` 会退化成**逐字节迭代**，
   `read(4).toString('utf8')` 拿到的是 `"113,48,48,49"` 而不是 `"001e"`，pkt-line 解析全错 →
   最终抛 `EmptyServerResponseError`。**必须**写成 `body: [bytes]`。
2. **请求 `o.body` 也是分块数组**，要先把各块拼起来再编码。若直接 `bytesToBase64(o.body)`，
   只会用到第一块（长度常为 1），POST 出去的 `git-upload-pack` 请求体只有 1 字节 → 服务端返回空。

这两个坑在 `push`/`fetch` 这类**带请求体**的操作上才暴露（`clone` 第一步 `info/refs` 是 GET，碰不到第 2 条）。

### 令牌规则（照搬电脑版的优先级）

按顺序匹配：
1. 该仓库上次使用的令牌 → 自动载入并提示
2. 只保存了一个令牌 → 任何仓库都用它
3. 按远端主机匹配（远端 `https://github.com/...` → 用 `github.com` 的令牌）
4. 都没有 → 清空用户/令牌，提示该仓库尚未绑定令牌

**主机名是服务器域名，不是邮箱** —— 例如 `github.com`。
为避免用户填错，实现了两层保护：
- **粘贴仓库链接后自动把主机名填进令牌栏**
- **保存令牌时智能纠正**：若填的是邮箱 / 带 `https://` / 带路径，自动改成从链接解析出的主机
- **启动时自动迁移**：把历史误填的键（如 `creds["xxx@gmail.com"]`）搬到正确主机名下

### 怎么用

**首次使用**
1. 打开「**Git**」→「**克隆仓库**」填链接，例如 `https://github.com/用户名/仓库名.git`
2. 私有仓库先在下方填 **用户名 + 令牌**（主机名会自动填好）→ 点「确认（添加/更新令牌）」
3. 点「**一键克隆仓库**」→ 自动克隆到应用私有目录下的 `<仓库名>/`
4. 克隆完自动登记为仓库并切换过去

> 浅克隆（`depth: 1`）只取最新一次提交，快且省流量。
> 若目标目录里已有 Git 仓库，会**跳过克隆改为 `pull`**（避免 checkout 冲突）。

**日常使用**
1. 改稿子 → 「**提交并推送**」（`add -A` → `commit` → `push`）
2. 「**拉取**」= `pull`；若当前编辑的文件在仓库内，拉取后会自动重新载入
3. 「**导出仓库文件…**」把整个仓库（不含 `.git`）打包成 ZIP 存到手机

`push` 失败会自动回退：先按 `main` 推，失败则改用当前分支名重推（兼容 `master`）。

> ⚠️ **安全性差异**：电脑版用 **Windows DPAPI** 加密令牌后才写盘；
> 移动端没有等价能力，令牌**明文**存在应用沙箱内（仅本机应用可读）。
> 若要更强保护，鸿蒙可改用 `@ohos.security.huks`（HUKS）加密。

---

## ⚠️ ArkWeb：系统对话框默认不显示

**症状**：「新建文件夹」「新建 txt」点了**完全没反应**。

**原因**：`prompt()` / `confirm()` / `alert()` 在 ArkWeb 里**默认不弹**（需要 Web 组件实现
`onAlert` / `onConfirm` / `onPrompt` 回调）。这些调用会**静默返回 `null`**，于是代码里的
`if (n === null) return;` 直接退出 —— 看起来就是"没反应"，连异常都没有。

**修法**：全部改用**页面内 HTML 弹窗**（`uiPrompt` / `uiConfirm` / `uiAlert`），
与平台无关、样式可控、还能显示多行说明。

---

## 原生桥：同步 vs 异步（很容易用错）

鸿蒙的 `javaScriptProxy` 和安卓的 `@JavascriptInterface` 方法是**同步返回**的，
但需要回调的操作（目录、网络、弹窗）通过 `window.maziOn(name, result)` 异步回传。

因此网页侧有两个调用封装：

| 函数 | 适用 | 说明 |
|---|---|---|
| `callNative(name, args)` | 原生**直接返回结果**的方法（`listDir` / `readText`） | 有返回值时立刻 resolve |
| `callNativeAsync(name, args)` | 原生返回 **`void`**、结果走 `maziOn` 的方法（`mkdir` / `createFile` / `writeText` / `rename` / `remove` / `deleteTree`） | 只等回调 |

> ⚠️ 用 `callNative` 调 `void` 方法会**永远不 resolve** —— 表现同样是"点了没反应"。
> 排查时如果发现某按钮无反应，先检查是不是用错了这两个函数。

---

## 安卓版

### 安装

```powershell
cd mobile
powershell -ExecutionPolicy Bypass -File .\build-apk.ps1
```

产物在 `mobile\android\build\写作.apk`。签名用的是固定路径的 keystore
（`mobile\android-sdk\debug.keystore`），这样**重装不会清数据**。

### 外壳做了什么

| 能力 | 实现 |
|---|---|
| 网页宿主 | `WebView` + `addJavascriptInterface` |
| 目录访问 | SAF：`ACTION_OPEN_DOCUMENT_TREE` + `takePersistableUriPermission` |
| 文件读写 | `DocumentsContract` + `ContentResolver` |
| 配置存储 | `SharedPreferences`（`mazi_config`） |
| Git 文件系统 | `AndroidFs.git*`（同步方法） |
| Git 网络 | `HttpURLConnection`（无同源限制） |
| 回传网页 | `evaluateJavascript("window.maziOn(...)")` |

### 已修的问题

| 问题 | 原因 |
|---|---|
| APK 装不上 | `targetSdkVersion` 34 → 安卓 15 拒绝，改成 35 |
| 每次重建都清数据 | 签名 key 每次变化，改用固定 keystore |
| `build-apk.ps1` 解析报错 | PowerShell 5.1 按 ANSI 读脚本，中文注释乱码 → 改为**纯 ASCII + UTF-8 BOM** |
| 目录重命名失败 | 原代码调 `unlink` 删目录（那只删文件）→ 改为递归搬移内容 |
| 空文件写入字面量 `b64:` | `gitWrite` 的长度判断陷阱 → 改为只判前缀 |

---

## 鸿蒙版

### 构建步骤

需要装有 DevEco Studio 的机器：

```powershell
$env:NODE_HOME='D:\DevEco Studio\tools\node'
$env:JAVA_HOME='D:\DevEco Studio\jbr'
$env:DEVECO_SDK_HOME='D:\DevEco Studio\sdk'      # hvigor 6.x 必需，指向 sdk 目录
$env:PATH="$env:NODE_HOME;$env:JAVA_HOME\bin;D:\DevEco Studio\tools\ohpm\bin;D:\DevEco Studio\tools\hvigor\bin;$env:PATH"

# 先同步网页本体(务必!)
Copy-Item mobile\index.html mobile\harmony\entry\src\main\resources\rawfile\index.html -Force

cd mobile\harmony
hvigorw assembleHap --mode module -p product=default -p buildMode=debug --no-daemon
hdc install -r entry\build\default\outputs\default\entry-default-signed.hap
```

签名材料由 DevEco 的「自动签名」生成（`File → Project Structure → Signing Configs`）。

### 构建踩过的坑

1. **缺 `harmony\hvigor\hvigor-config.json5`** → hvigor 直接拒绝启动
   （`Hvigor config file ... does not exist`）。已补上。
2. **`DEVECO_SDK_HOME` 必须设**（指向 `D:\DevEco Studio\sdk`，不是 `sdk\default`），
   否则报 `Invalid value of 'DEVECO_SDK_HOME'`。
3. **`preferences` 不在 `@kit.ArkTS` 里** → 要从 `@ohos.data.preferences` 导入。
4. **`hvigorw` 即使构建成功也返回退出码 1** → 不要用 `$LASTEXITCODE` 判定，
   要看日志里的 `BUILD SUCCESSFUL`。
5. **`fs.accessSync()` 在路径不存在时是抛异常，不是返回 `false`** →
   写成 `if (!fs.accessSync(p))` 会因为异常跳到 catch，导致本该创建的目录没被创建。
   要用 `statSync` + try/catch 判断存在性。
6. **`new util.TextEncoder().encodeInto(s)` 的返回值不可靠** ——
   同一个调用在 `log()` 里能用，在 `createFile()` 里却拿到 `undefined`
   （报 `Cannot read property buffer/length of undefined`）。
   现在改为**手写 UTF-8 编码**（`utf8BufferOf`），行为确定。

### ArkTS 限制（写原生桥时反复踩到）

比 TypeScript 严得多：

- 禁止 `any` / `unknown`
- 禁止匿名对象字面量 → 回传数据必须先声明 `interface`
- 禁止 `for...in`（`arkts-no-for-in`）
- 禁止对字段做下标访问（`arkts-no-props-by-index`），**`Record<string,string>` 也不行** →
  要读 `resp.header`（类型是 `Object`）时，直接 `JSON.stringify(resp.header)` 整体交给网页处理
- `util.Base64Helper` 的方法是**实例方法**（要 `new util.Base64Helper()`），不是静态方法
- `util.TextEncoder.encodeInto` 也是**实例方法**，不是静态方法
- `@ohos.net.http` 的 `HttpRequestOptions.maxLimit` **默认只有 5MB**（最大 100MB）
  → 拉 packfile 必须显式调大

### 无线调试（推荐）

插线调试容易掉，鸿蒙支持无线调试：

1. 开发者选项 → **无线调试** → 打开
2. 记下 IP 和端口（形如 `192.168.0.229:38299`）
3. `hdc tconn <IP>:<端口>`

之后 `hdc install -r <hap>` 就能直接装，不用插线。

### 设备上的诊断日志

原生桥把所有关键事件写进**应用私有目录下的 `mazi-git.log`**，比 hilog 可靠得多
（hilog 环形缓冲会被系统日志冲掉）。日志在每次启动时自动清空，所以拉出来的总是本次会话记录：

```powershell
hdc shell "cat /data/app/el2/100/base/com.touho.writing/haps/entry/files/mazi-git.log"
```

日志标签：

| 标签 | 内容 |
|---|---|
| `[BOOT]` | 启动、工作目录、恢复的仓库/稿件 |
| `[GIT]` | 仓库切换、面板刷新 |
| `[CLONE]` | 克隆流程每一步 |
| `[NATIVE]` | 原生侧收到的调用与返回 |
| `[HTTP→]` / `[HTTP←]` | 每次 git 网络请求的完整 header / body 长度 / 状态码 |
| `[HTTP-BODY n]` | 非 2xx 时的响应体前 400 字符 |
| `[FS-FAIL]` | 文件操作失败（带错误码） |
| `[NEW]` / `[WRITE]` | 新建文件 / 写入 |
| `[EXPORT]` | 导出 ZIP |
| `[导出全部]` | 打包全部文件 |
| `[JS-ERROR]` / `[JS-REJECT]` | 未捕获异常 / 未处理的 Promise 拒绝 |

> 加了全局 `error` / `unhandledrejection` 兜底后，**失败不会再被静默吞掉** ——
> 这是排查效率的关键。

---

## ⚠️ 改完代码务必先跑检查脚本

```powershell
python mobile\check_mobile.py
```

**这个脚本是必需的，不是可选的。** 原因：

「回滚功能」要成段删除代码，而**删除很容易连带切掉别的函数定义或事件绑定**。
本项目已经因此踩了 3 次，每次的症状都是「**点了没反应**」这种最难查的问题：

| # | 被误删的东西 | 症状 |
|---|---|---|
| 1 | `createTreeFile()` 的定义 | 「新建 txt」抛 `ReferenceError` |
| 2 | `btnNewFile` 的 `onclick` 绑定 | 按钮**彻底静默**（连异常都没有） |
| 3 | `notTextFile()` 的定义 | 点击文件抛 `ReferenceError`，**编辑区不加载** |

**致命之处：hvigor 构建时一律不报错** —— 打包照常成功，只有运行时点到那条路径才炸。
所以只能靠静态检查提前发现。

脚本检查三项：

| 项 | 内容 |
|---|---|
| **A. 语法** | 用 `node --check` 验证每个 `<script>` 块 |
| **B. 漏定义** | 被调用的名字是否有定义（排除内建/库/参数） |
| **C. 少绑定** | 每个 `<button id=...>` 是否有 `onclick` / `addEventListener` |

退出码 0 = 通过。**建议把它当作构建流程的第一步**：

```powershell
cd mobile
python check_mobile.py          # 必须全绿
Copy-Item index.html harmony\entry\src\main\resources\rawfile\index.html -Force
cd harmony
hvigorw assembleHap --mode module -p product=default -p buildMode=release --no-daemon
```

## 修改网页后要做什么

`index.html` 是共享本体，改完后：

- **鸿蒙**：复制到 `harmony\entry\src\main\resources\rawfile\index.html` 后重新构建
  （**必须**，否则改动不会进包）
- **安卓**：重新跑 `build-apk.ps1`（脚本会自动同步到 `assets\index.html`）
- **浏览器**：直接用，无需构建

建议改完后先做语法检查（本体有 4 个 `<script>` 块：buffer / fflate / isomorphic-git / 应用代码）。

---

## 已知限制

| 限制 | 原因 | 影响 |
|---|---|---|
| 文件只能在应用私有目录 | 系统按文件节点授权，不给目录写权限 | 卸载会丢文件；需导出才能放进「文件管理」 |
| 无法批量写用户目录 | 同上（`save()` 只授权单个文件） | 导出必须打包成一个 ZIP |
| 浅克隆 | `depth: 1` | 只有最新一次提交的历史 |
| 令牌明文存储 | 移动端无 DPAPI 等价物 | 设备被 root 后可读 |
| 目录树不显示 Git 状态 | 未实现 | 需到 Git 面板看变更 |
| 一次只能操作一个仓库目录 | 工作目录单一 | — |
