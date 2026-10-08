# 写作 Writing · 移动版（安卓 / 鸿蒙）

电脑版是 Windows 专用的 WPF 程序（`Release\写作.exe`），手机上没有 .NET/WPF 运行环境，
所以移动端不是"把 exe 换个壳"，而是**同一套功能逻辑的移动端重写**：一份共享的 H5 应用，
外面套安卓 / 鸿蒙的原生外壳（WebView + 原生文件能力）。

```
mobile\
├─ index.html              共享应用本体(单文件,离线可用,浏览器直接打开)
├─ Release\写作.apk        安卓安装包(已构建、已签名,可直接装)
├─ android\                安卓外壳工程(Java + WebView)
├─ harmony\                鸿蒙外壳工程(ArkTS + ArkWeb)
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
| 导入 / 导出 txt | ✅ | 导出：安卓落到系统「下载」目录；鸿蒙走系统保存框；浏览器走下载 |
| 编码识别（UTF-8 → GB18030 回退） | ✅ | 与电脑版一致 |
| 自动保存 | ✅ | 停止输入 1 秒后落盘，切后台立即保存 |
| 键盘统计面板 | ⚠️ 改为「输入统计」 | **手机系统不允许第三方统计物理按键**（安卓需无障碍/输入法权限，鸿蒙同理），因此改为统计输入字符频次 |
| 全局目录树 | ❌ | 安卓分区存储（SAF）与鸿蒙沙箱都不允许任意遍历磁盘，改为应用内稿件列表 + 导入导出 |
| Git 管理 / PAT 令牌 | ❌ | 手机端没有 `git.exe`，令牌加密依赖 Windows DPAPI。移动端不做 Git；建议"手机码字 → 导出 txt → 电脑上提交" |
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
- 返回键：优先关闭应用内抽屉，再退出
- 状态栏白色浅色图标、码字时屏幕常亮

### 重新打包
```powershell
cd mobile
powershell -ExecutionPolicy Bypass -File .\build-apk.ps1
```
脚本会：同步最新 `index.html` → aapt2 编译资源 → aapt2 link → javac → d8 → 把 `classes.dex` 打进 APK
→ zipalign → apksigner 签名，输出 `Release\写作.apk`。

三个已踩过的坑（脚本里已处理）：
1. **aapt2 / d8 不支持中文路径** → 先复制到 `C:\mazi_apk_build` 再构建
2. **javac 21 编译出的匿名内部类会让 build-tools 34 的 R8/d8 崩** → 固定用 JDK 11~18（本机用 jdk-18）
3. 原生工具往 stderr 写警告，PowerShell 的 `$ErrorActionPreference='Stop'` 会误判为失败 → 用 `Continue` + 判 `$LASTEXITCODE`

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
  - `saveText(name, text)` → `picker.DocumentViewPicker().save()` 弹系统保存框；取消则回退写入应用目录
  - `importFile()` → `DocumentViewPicker().select()` 选 txt，读入后回调 `window.maziImportFile()`
  - `setSheetOpen()` → 让返回键先关抽屉再退出
  - `toast()` / `platform()`
- 状态栏白底浅色图标、启动窗口背景 `#F6F7F9`

## 修改网页后要做什么

`index.html` 是共享本体，改完后：

- 安卓：重新跑 `build-apk.ps1`（脚本会自动同步到 `assets\index.html`）
- 鸿蒙：把 `index.html` 复制到 `harmony\entry\src\main\resources\rawfile\index.html` 后重新构建
- 浏览器：直接用，无需构建
