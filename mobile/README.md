# 写作 Writing · 移动版（安卓 / 鸿蒙）

电脑版是 Windows 专用的 WPF 程序（`Release\写作.exe`），手机上没有 .NET/WPF 运行环境，
所以移动端不是"把 exe 换个壳"，而是**同一套功能逻辑的移动端重写**：一份共享的 H5 应用，
外面套安卓 / 鸿蒙的原生外壳（WebView + 原生文件能力）。

```
mobile\
├─ index.html              共享应用本体(单文件,离线可用,浏览器直接打开)
│                          —— 内置 isomorphic-git(UMD 内联),无需联网加载
├─ Release\写作.apk        安卓安装包(已构建、已签名,可直接装)
├─ android\                安卓外壳工程(Java + WebView)
├─ harmony\                鸿蒙外壳工程(ArkTS + ArkWeb)
│  └─ entry\src\main\resources\rawfile\
│     ├─ index.html            内置网页(与上面的 index.html 同一份,需手动同步)
│     └─ vendor\isomorphic-git.umd.min.js   离线保底的库副本
├─ build-apk.ps1           安卓一键打包脚本(无需 Gradle)
└─ 获取安卓SDK.ps1         按需下载构建用 SDK(约 347 MB,用完可删,不入库)
└─ build\                  构建中间产物(可删)
```

## 三种用法

| 用法 | 怎么用 | 是否需要装东西 |
|------|--------|----------------|
| **① 浏览器直接打开** | 把 `index.html` 传到手机，用浏览器打开（安卓 Chrome/华为浏览器都行）；或放到任意静态服务器/网盘直链 | 不需要，立即可用 |
| **② 安卓 APK** | 安装 `Release\写作.apk` | 需要允许"安装未知来源应用" |
| **③ 鸿蒙 HAP** | 用 DevEco Studio 打开 `harmony\` 工程 → 配置签名 → Build HAP | 需要 DevEco Studio 5.0+ 与华为开发者账号（见下文） |

## 功能对照（电脑版 → 移动版）

| 功能 | 移动版 | 说明 |
|------|--------|------|
| 码字编辑区、字号调节 | ✅ | 12~48px，按钮调，适配手机 |
| 自动缩进（回车补两个全角空格） | ✅ | 与电脑版同款"检测换行"实现，对输入法零干扰 |
| 码字速度（3 秒采样 + 指数平滑，停 9 秒归零） | ✅ | 算法与电脑版一致 |
| 累计已码 / 文档字数 / 净字数（去标点） | ✅ | 标点判定与电脑版一致（含全角字母数字不算标点） |
| 纸张背景切换（白 / 奶绿 / 天蓝） | ✅ | |
| 多文档管理 | ✅（改为稿件列表） | 手机不能像电脑那样自由遍历磁盘，稿件存在本机应用存储里 |
| **文件夹访问（目录树）** | ✅ **新增** | 选一个目录 + **持久化授权**，之后可长期在该目录内浏览子目录、打开/新建/重命名/删除 txt，改动**直接写回原文件**（不必再导入导出） |
| 导入 / 导出 txt | ✅ | 导出：安卓落到系统「下载」目录；鸿蒙走系统保存框；浏览器走下载 |
| 编码识别（UTF-8 → GB18030 回退） | ✅ | 与电脑版一致 |
| 自动保存 | ✅ | 停止输入 1 秒后落盘，切后台立即保存 |
| 键盘统计面板 | ⚠️ 改为「输入统计」 | **手机系统不允许第三方统计物理按键**（安卓需无障碍/输入法权限，鸿蒙同理），因此改为统计输入字符频次 |
| 全局目录树 | ⚠️ 改为「选定一个文件夹」 | 安卓分区存储（SAF）与鸿蒙沙箱都**不允许任意遍历整块磁盘**；两端统一改为：用户选定**一个**目录并做**持久化授权**（鸿蒙 `persistPermission` / 安卓 `takePersistableUriPermission`），之后该目录内可自由浏览与读写（详见下文「文件夹访问」） |
| Git 管理 / PAT 令牌 | ✅ **新增** | 手机端**没有 `git.exe`**，改用 **isomorphic-git**（纯 JS git，内联在页面里，离线可用）；文件读写走原生桥，网络走原生 HTTP。对齐电脑版：**克隆仓库**、多仓库、令牌、令牌↔仓库记忆、拉取、提交、推送（详见下文「Git」） |
| 直播用置顶 / 时钟 | ❌ | 手机无桌面悬浮窗语义；码字时已自动保持屏幕常亮 |

## 安卓版

### 安装
1. 把 `Release\写作.apk` 传到手机（微信/QQ/USB/网盘均可）
2. 手机上点击安装，若提示则允许「安装未知来源应用」
3. 首次打开即用，无需任何权限（导出走系统「下载」目录，不需要存储权限）

> APK 用调试证书签名（`CN=Android Debug`），仅供自己安装使用；要上架应用商店需要换成你自己的正式签名。

### 外壳做了什么
- WebView 承载 `assets/index.html`，开启 DOM Storage（稿件与设置持久化）
- `MaziAndroid.saveText()`：导出 txt 到系统「下载」目录（Android 10+ 走 MediaStore，无需权限；旧版本写应用目录）
- `onShowFileChooser`：支持网页的文件选择（导入 txt）
- **文件夹访问（SAF）**：`AndroidFs.java` 用 `ACTION_OPEN_DOCUMENT_TREE` 让用户选一个目录，
  `takePersistableUriPermission` 持久化授权后即可长期读写该目录；`DocumentsContract` 提供
  列目录 / 建文件 / 建目录 / 删文件 / 读写，**`rename` 用「复制 + 删旧」实现**（SAF 没有 rename）
- **Git 桥**：`MaziHarmony` 别名对象与鸿蒙同名同语义（`listDir/readText/writeText/...`、
  `gitRead/gitWrite/gitMkdir/gitUnlink/gitRmdir/gitReaddir/gitStat/gitExists/gitHttp`），
  于是网页端 `gitFs()` / `gitHttp()` 适配器**两端共用一套代码**；
  网络走 `HttpURLConnection`（原生无同源限制）
- 返回键：优先关闭应用内抽屉，再退出
- 状态栏白色浅色图标、码字时屏幕常亮

> **两端桥的同步性差异**：鸿蒙 `javaScriptProxy` 的 `methodList` 是**同步返回**的，
> 安卓 `@JavascriptInterface` 是**异步**的。因此网页端把原生调用统一包成 Promise
> （`gitFs()` 用 `Promise.resolve().then(...)`），并且在「原生返回 void、结果靠 `maziOn` 回传」的
> 场景（两端的 `pickFolder`、`gitHttp`）改用 `callNativeAsync` —— 否则 `callNative` 会立刻用
> `undefined` 结算，把真正的响应丢掉。

### 重新打包
```powershell
cd mobile
powershell -ExecutionPolicy Bypass -File .\获取安卓SDK.ps1   # 首次,约 116MB 下载 / 347MB 解压
powershell -ExecutionPolicy Bypass -File .\build-apk.ps1
```
脚本会：同步最新 `index.html` → aapt2 编译资源 → aapt2 link → javac → d8 → 把 `classes.dex` 打进 APK
→ zipalign → apksigner 签名，输出 `Release\写作.apk`。

四个已踩过的坑（脚本里已处理）：
1. **aapt2 / d8 不支持中文路径** → 先复制到 `C:\mazi_apk_build` 再构建
2. **javac 21 编译出的匿名内部类会让 build-tools 34 的 R8/d8 崩** → 固定用 JDK 11~18（本机用 jdk-17）
3. 原生工具往 stderr 写警告，PowerShell 的 `$ErrorActionPreference='Stop'` 会误判为失败 → 用 `Continue` + 判 `$LASTEXITCODE`
4. **PowerShell 5.1 会把「无 BOM 的 UTF-8 脚本」里的中文读乱**，导致语法解析失败
   （报 `Unexpected token` / `Missing closing '}'`，且报错行号指向文件末尾、与真实原因无关）。
   → 本脚本**改成纯 ASCII 注释**，输出文件名用码点拼（`[char]0x5199`），并**存为带 BOM 的 UTF-8**。

> **`targetSdkVersion` 已升到 35**：Android 15（API 35）起系统**拒绝安装** `targetSdk < 35` 的 APK，
> 原来的 34 会导致「装不上」。脚本仍用 API 34 的 `android.jar` 编译（`minSdk 24`），
> 只把 `targetSdkVersion` 声明为 35。

> **签名密钥已改为常驻**（存在 `mobile\android-sdk\debug.keystore`，已加入 .gitignore）。
> 以前每次打包都新生成一把密钥，密钥一变安卓就拒绝原地覆盖安装
> （`INSTALL_FAILED_UPDATE_INCOMPATIBLE`），只能卸载重装 —— 而**卸载会清掉应用数据**。
> 现在密钥固定，后续升级可直接覆盖安装、数据保留。
> 如果你装过签名密钥不同的旧包，需要**先卸载一次**；之后就不会再发生了。


SDK 组件**不在项目里**（解压后约 347 MB）。首次打包前先运行一次下载脚本（约 116 MB 压缩包）：
`https://dl.google.com/android/repository/build-tools_r34-windows.zip`、
`https://dl.google.com/android/repository/platform-34-ext7_r03.zip`

## 鸿蒙版

`harmony\` 是标准的 DevEco Studio 5.0（API 12 / HarmonyOS NEXT）Stage 模型工程：

```
harmony\
├─ AppScope\app.json5                     应用信息(bundleName、版本、图标)
├─ build-profile.json5 / hvigorfile.ts    工程级构建配置
└─ entry\
   ├─ src\main\module.json5               模块与 Ability 声明
   ├─ src\main\ets\entryability\EntryAbility.ets
   ├─ src\main\ets\pages\Index.ets        ArkWeb 页面 + 原生桥(MaziHarmony)
   └─ src\main\resources\rawfile\index.html   内置网页(与 index.html 同一份)
```

### 构建步骤（需要在有 DevEco 的机器上）
1. 安装 **DevEco Studio 5.0 及以上**（自带 HarmonyOS SDK 与 hvigor/ohpm）
2. `File → Open` 打开 `mobile\harmony` 目录，等待 `Sync Now` 完成
3. 配置签名：`File → Project Structure → Signing Configs`
   - 勾选 **Automatically generate signature**（需登录华为开发者账号，DevEco 会自动生成调试证书与 Profile）
   - 或手工导入你自己的 `.p12` + `.cer` + `.p7b`
4. `Build → Build Hap(s)/APP(s) → Build Hap(s)`，产物在 `entry\build\default\outputs\default\`
5. 连接手机（开发者模式 + USB 调试）后点运行，或用命令行安装：
   ```
   hdc install entry\build\default\outputs\default\entry-default-signed.hap
   ```

> ⚠️ **本机没有 DevEco/鸿蒙 SDK，也没有华为开发者账号**，所以这个工程只完成了源码与配置，
> **尚未编译验证过**。若你希望我在本机尝试下载鸿蒙命令行工具链并构建（签名仍需你的华为账号），
> 告诉我即可。
>
> 另外：鸿蒙手机上现在就可以**用浏览器直接打开 `index.html`**（方式 ①），功能与装了 HAP 基本一致，
> 差别只是导出/导入会走浏览器下载而不是系统文件框。

### 外壳做了什么
- `Web({ src: $rawfile('index.html') })` 承载同一份网页，开启 `domStorageAccess`（持久化）
- `window.MaziHarmony` 原生桥（`Index.ets` 里的 `MaziBridge`）：
  - 文件夹：`pickFolder()` / `folderInfo()` / `clearFolder()` / `listDir(rel)` / `readText(rel)` /
    `writeText(rel,text,enc)` / `createFile(rel,text)` / `mkdir(rel)` / `remove(rel,isDir)` / `rename(rel,newName)`
  - 导入导出：`saveText(name, text)`、`importFile()`
  - 界面：`setSheetOpen()`（让返回键先关抽屉再退出）、`toast()`、`platform()`
- 原生结果**统一异步回传** `window.maziOn(name, res)`，`res` 形如 `{ok:true,data:…}` 或 `{ok:false,error:"…"}`
- 状态栏白底浅色图标、启动窗口背景 `#F6F7F9`

## 文件夹访问（目录树）

手机不允许应用任意遍历磁盘，但允许**用户主动授权一个目录**。两端的「文件夹」都是这条路，
差别只在系统 API：

| | 鸿蒙 | 安卓 |
|---|---|---|
| 选目录 | `DocumentSelectMode.FOLDER` | `ACTION_OPEN_DOCUMENT_TREE` |
| 持久化授权 | `fileShare.persistPermission`（`ohos.permission.FILE_ACCESS_PERSIST`，normal 级、系统自动授予） | `takePersistableUriPermission` |
| 重启后恢复 | `activatePermission` | 持久授权本身即长期有效 |
| 读写实现 | `@ohos.file.fs`（`fileIo`） | `DocumentsContract` + `ContentResolver` |

操作流程（两端一致）：

1. 顶栏 **「文件夹」→ 右上角「选择文件夹」**，在系统选择器里选一个目录（例如你放小说的目录，或一个 Git 仓库目录）
2. 之后可以：逐层展开子目录、点开 txt 编辑、**改动自动写回原文件**（1.2 秒防抖 + 切后台立即落盘）、
   新建 txt / 新建文件夹 / 长按条目重命名或删除、点「刷新」重新扫描
3. 重启应用后自动恢复该目录，**不用再选一次**
4. 隐藏项（`.git` 等以 `.` 开头的）在目录树里自动跳过——与电脑版一致

编码处理与电脑版一致：UTF-8 优先，解码失败回退 GB18030，保存时按打开时的编码写回
（鸿蒙 `util.TextDecoder` 支持 utf-8 / gbk / gb18030 / big5 / utf-16le 等）。

> 注意：**一次只能授权一个目录**（系统限制），且只能浏览该目录**内部**。想换目录就再点一次「选择文件夹」。
> 安卓的 `rename` 是「读出内容 → 写新名 → 删旧名」实现的，因为 SAF 没有 rename 原语。

## 配置持久化（重要：鸿蒙的 localStorage 会丢）

**症状**：鸿蒙版关掉应用再进去，设置和令牌全没了（例如刚更新的 PAT 重启后消失）。

**原因**：`ArkWeb` 的 `localStorage` 在部分鸿蒙机器上「关闭应用再进入」会丢失。这是**已知问题**，
社区有大量相同反馈（[示例](http://bbs.itying.com/topic/676713a2ad669d01bf73491a)）。
反过来，原生 `preferences` 实测**可靠** —— 应用里「上次选定的文件夹」就是存在原生 preferences 里的，
它活过了我十几次重建和重装。

**修法**：两端都把网页的配置镜像到原生存储，原生优先。

| | 鸿蒙 | 安卓 |
|---|---|---|
| 原生存储 | `@ohos.data.preferences`（`mazi_config`） | `SharedPreferences`（`mazi_config`） |
| 桥接口 | `configLoad()` / `configSet(k,v)` / `configRemove(k)` | 同名同语义 |

网页侧（`index.html`）：

- `LS.setItem()/removeItem()` 包了一层，写入 **localStorage 的同时镜像到原生**（只镜像白名单里的键：
  稿件、当前稿件、设置、字符统计、Git 配置）
- 启动时 `syncFromNative()` 先**用原生的值覆盖 localStorage**，再跑原来的初始化 —— 读取路径保持同步，原代码不用动
- **老用户升级**：若原生还是空的（第一次装这个版本），`seedNativeFromLocal()` 会把已有 localStorage
  反向灌进原生，不会丢数据
- **浏览器里打开** `index.html`（没有原生桥）时自动降级为纯 localStorage

> 安卓的 WebView localStorage 本来是可靠的，也照样镜像了一份，这样两端行为一致、配置可备份可检查。

## Git（鸿蒙）

手机上没有 `git.exe`，鸿蒙也不允许普通应用执行外部二进制（`@ohos.childProcess` 仅系统应用可用），
所以**不能**像电脑版那样调 `git.exe`。这里改用 **[isomorphic-git](https://isomorphic-git.org/)** —— 纯 JS 的 git 实现，
以 UMD 形式**内联进 `index.html`**（`window.git`，72 个 API），完全离线可用。

两条适配器把 isomorphic-git 接到鸿蒙上：

| 适配器 | 接到哪 | 说明 |
|---|---|---|
| `fs` | `window.MaziHarmony.git*`（原生**同步**方法） | `gitRead/gitWrite/gitMkdir/gitUnlink/gitRmdir/gitReaddir/gitStat/gitExists`；文本走 UTF-8，二进制加 `b64:` 前缀走 base64（`javaScriptProxy` 不保证支持 ArrayBuffer） |
| `http` | `window.MaziHarmony.gitHttp`（`@ohos.net.http`） | 绕开浏览器同源限制（GitHub 的 git 端点不发 CORS 头） |

### ⚠️ `http` 适配器有两个极易踩的坑（都会表现为 `Empty response from git server`）

isomorphic-git 的 `body` 在**两个方向上都是「Uint8Array 分块数组」**，不是单个 `Uint8Array`：

1. **响应 `body` 必须是分块数组**。若直接给一个 `Uint8Array`，`getIterator()` 会退化成**逐字节迭代**，
   `read(4).toString('utf8')` 拿到的是 `"113,48,48,49"` 而不是 `"001e"`，pkt-line 解析全错 →
   最终抛 `EmptyServerResponseError: Empty response from git server.`。**必须**写成 `body: [bytes]`。
2. **请求 `o.body` 也是分块数组**，要先把各块拼起来再编码。若直接 `bytesToB64(o.body)`，
   只会用到第一块（长度常为 1），POST 出去的 `git-upload-pack` 请求体只有 1 字节 → 服务端返回空。

这两个坑在 `push`/`fetch` 这类**带请求体**的操作上才暴露（`clone` 的第一步 `info/refs` 是 GET，碰不到第 2 条）。

与电脑版**一致**的功能：多仓库、`init`、切换分支、提交、拉取、提交并推送、提交历史（最近 40 条）、
`M/A/U/D` 状态字母与配色、执行输出。

**令牌规则（照搬电脑版的优先级）**，按顺序：
1. 该仓库上次使用的令牌 → 自动载入并提示
2. 只保存了一个令牌 → 任何仓库都用它
3. 按远端主机匹配（远端 `https://github.com/...` → 用 `github.com` 的令牌）
4. 都没有 → 清空用户/令牌，提示该仓库尚未绑定令牌

令牌↔仓库绑定会被记住；删除令牌时同步清理相关绑定。
认证沿用 `username + PAT`，由 isomorphic-git 的 `onAuth` 回调提供。

> ⚠️ **与电脑版的差异（安全性）**：电脑版用 **Windows DPAPI** 加密令牌后才写盘；
> 鸿蒙侧没有等价的 DPAPI，令牌目前明文存在页面 localStorage 里（仅本机应用沙箱内可读）。
> 若要更强保护，可改用 `@ohos.security.huks`（HUKS）做密钥加密后再存。

### 怎么用

**方式一：从仓库链接克隆（推荐首次使用）**
1. 打开「**Git**」→ 在「**克隆仓库**」里填仓库链接，例如 `https://github.com/用户名/仓库名.git`
2. 点「**选择文件夹并克隆**」→ 在弹出的系统选择器里选一个**空文件夹**（或新建一个）
3. 应用会自动在该文件夹下克隆出仓库内容（`depth: 1` 浅克隆，快且省流量），
   克隆完**自动登记为仓库并切换过去**；私有仓库请先按下面第 3 步保存令牌
4. 若该链接之前克隆过，会直接复用已有仓库并改为 `pull`，不会重复拉一份

> 浅克隆（`depth: 1`）只取最新一次提交。需要完整历史时用电脑版克隆，或在电脑上 `git fetch --unshallow`。

**方式二：打开已有仓库**
1. 先在「**文件夹**」里把目录选到一个 **Git 仓库**（或一个空目录）
2. 打开「**Git**」→「**用当前文件夹作仓库**」；空目录先点「**init 初始化**」
3. 私有仓库在下方填 **主机名 / 用户名 / 令牌** → 「确认（添加/更新令牌）」
4. 「**提交**」只提交到本地；「**提交并推送**」会 `add -A` → `commit` → `push`
5. 「**拉取**」= `pull`；若当前编辑的文件在仓库内，拉取后会自动重新载入

`push` 失败会自动回退：先按 `main` 推，失败则改用当前分支名重推（兼容 `master` 等）。

## 电脑版（Windows）克隆仓库

电脑版有真正的 `git.exe`，走的是系统 git：

1. 打开工具栏「**Git**」
2. 在左侧「**克隆仓库（填链接 → 选文件夹 → 拉取内容）**」输入框里填仓库链接
3. 点「**选择文件夹并克隆**」→ 选一个**空文件夹作为仓库根目录**
   （它不是空的会明确拒绝，避免把已有文件搞乱）
4. 克隆完成后自动加入仓库列表并**在新线程里执行 `git clone`**，日志区实时显示

实现细节（`GitPanel.cs` 的 `Clone_Click`）：
- 在**父目录**里执行 `git clone <url> <文件夹名>`，这样 git 能自己创建目标目录，
  同时兼容不同 git 版本对「目录已存在」的处理差异
- 目标若已存在且为空，先删掉再让 git 克隆
- 支持 `https://` / `git@` / `ssh://`；`https` 私有仓库自动套用已保存的该主机 PAT
  （`http.extraheader: Authorization: Basic ...`），`ssh` 走系统密钥
- 克隆前先按远端地址查重（`FindRepoByUrl`，比较时忽略大小写与结尾 `.git`），
  已克隆过就直接切过去并 `pull`，不会重复克隆

## 命令行构建（鸿蒙）

本机已装 DevEco Studio 时，可以不打开 IDE 直接出包：

```powershell
$env:NODE_HOME='D:\DevEco Studio\tools\node'
$env:JAVA_HOME='D:\DevEco Studio\jbr'
$env:DEVECO_SDK_HOME='D:\DevEco Studio\sdk'      # hvigor 6.x 必需，指向 sdk 目录
$env:PATH="$env:NODE_HOME;$env:JAVA_HOME\bin;D:\DevEco Studio\tools\ohpm\bin;D:\DevEco Studio\tools\hvigor\bin;$env:PATH"
cd mobile\harmony
ohpm install
hvigorw assembleHap --mode module -p product=default -p buildMode=debug --no-daemon
```

产物：`entry\build\default\outputs\default\entry-default-signed.hap`；安装：
`hdc install -r <hap路径>`。签名材料由 DevEco 的「自动签名」生成（`File → Project Structure → Signing Configs`）。

四个已踩过的坑：
1. **缺 `harmony\hvigor\hvigor-config.json5`** → hvigor 直接拒绝启动（`Hvigor config file ... does not exist`）。已补上。
2. **`DEVECO_SDK_HOME` 必须设**（指向 `D:\DevEco Studio\sdk`，不是 `sdk\default`），否则报 `Invalid value of 'DEVECO_SDK_HOME'`。
3. **`preferences` 不在 `@kit.ArkTS` 里** → 要从 `@ohos.data.preferences` 导入。
4. **ArkTS 限制比 TypeScript 严得多**，写原生桥时反复踩到：
   - 禁止 `any` / `unknown`
   - 禁止匿名对象字面量 → 回传数据必须先声明 `interface`，用带类型的变量传
   - 禁止 `for...in`（`arkts-no-for-in`）
   - 禁止对字段做下标访问（`arkts-no-props-by-index`），**`Record<string,string>` 也不行** →
     要读 `resp.header`（类型是 `Object`）时，直接 `JSON.stringify(resp.header)` 整体交给网页处理
   - `util.Base64Helper` 的方法是**实例方法**（要 `new util.Base64Helper()`），不是静态方法
   - `@ohos.net.http` 的 `HttpRequestOptions.maxLimit` **默认只有 5MB**（最大 100MB）→ 拉 packfile 必须显式调大
5. **`hvigorw` 即使构建成功也返回退出码 1** → 不要用 `$LASTEXITCODE` 判定，要看日志里的 `BUILD SUCCESSFUL`。

## 修改网页后要做什么

`index.html` 是共享本体，改完后：

- 安卓：重新跑 `build-apk.ps1`（脚本会自动同步到 `assets\index.html`）
- 鸿蒙：把 `index.html` 复制到 `harmony\entry\src\main\resources\rawfile\index.html` 后重新构建
- 浏览器：直接用，无需构建（「文件夹」按钮会提示需要在 App 内使用）
