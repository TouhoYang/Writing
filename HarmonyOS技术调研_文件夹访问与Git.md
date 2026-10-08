# HarmonyOS NEXT 技术调研：用户文件夹访问 与 应用内 Git

> 目标工程：ArkTS Stage 模型 + ArkWeb（`$rawfile('index.html')`）+ `javaScriptProxy` 暴露 `MaziHarmony`
> 配置：`compatibleSdkVersion` 5.0.0(12) / 设备 HarmonyOS 7.0.0 手机(API 26) / DevEco 26.0.0.851 / hvigor 6.26.8
> 调研方式：直接下载 **OpenHarmony 官方源码文档仓库**（`gitee.com/openharmony/docs`，与华为 doccenter 同源）+ 官方权限定义 JSON + ohpm registry 实测 + 社区资料
> 结论标注方式：`【官方】`= 官方文档/官方源码确认；`【社区】`= 社区经验/推测；置信度 高/中/低

---

## 0. 先说结论（TL;DR）

| 问题 | 结论 | 可行性 |
|---|---|---|
| 用 `DocumentViewPicker` 选**文件夹** | 枚举确实存在（`DocumentSelectMode.FOLDER`），但**标准手机上不具备该系统能力**，会抛 801 | **手机不可行 / 2in1 可行** |
| 持久化访问授权 | `fileShare.persistPermission` + `activatePermission`，权限 `ohos.permission.FILE_ACCESS_PERSIST` = **normal + system_grant**，三方可直接申请 | **可行** |
| 遍历所选目录 | `fileIo.listFile(path, {recursion:true})`，path 通过 `new fileUri.FileUri(uri).path` 从 URI 转换 | **有条件可行** |
| 访问"下载"目录 | `Environment.getUserDownloadDir()` **仅 2in1**；手机走 `READ_WRITE_DOWNLOAD_DIRECTORY`（normal + user_grant）弹窗授权 | **有条件可行** |
| 应用私有目录 | `context.filesDir`，零权限 | **可行（最稳）** |
| 完整 Git 客户端 | libgit2 只有 **PC/2in1 命令行移植**，无手机 ohpm 包；`childProcessManager` 不能 exec 外部二进制；沙箱禁止 exec | **不可行** |
| `isomorphic-git` 跑在 ArkWeb | 依赖零 `require()`，可纯 Buffer + 自定义 fs/http 适配器运行，UMD 包 265 KB | **有条件可行（推荐）** |
| 原生桥代理 git | 只能代理**高层语义**（readFile/writeFile/list），不能代理"git 命令" | **不可行** |
| HTTPS + PAT | `@kit.NetworkKit` 的 `http.request` 支持任意 `header` | **可行** |
| SSH | 有 `@ohos/libssh`（libssh 0.11.1 + NAPI，ohpm 实测存在 v1.0.4） | **有条件可行** |
| **推荐路线** | ArkWeb + isomorphic-git 纯 JS，fs 适配器走异步 `javaScriptProxy`→`fileIo`，http 适配器直接 `fetch`；或退化为 **GitHub/Gitee REST API 只推单文件** | — |

---

# 问题一：让应用"访问用户的文件夹"

## 1.1 `DocumentViewPicker` 支持选择文件夹吗？

**答：枚举存在，但手机上不可用。**

**【官方 · 置信度高】** `@ohos.file.picker` 中确实有目录选择能力：

```ts
// 枚举 DocumentSelectMode，起始版本 API 11
// 系统能力：SystemCapability.FileManagement.UserFileService.FolderSelection
enum DocumentSelectMode {
  FILE   = 0,   // 文件类型（默认）
  FOLDER = 1,   // 文件夹类型
  MIXED  = 2    // 文件和文件夹混合类型
}
```

来源：[js-apis-file-picker.md#documentselectmode11](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-core-file-kit/js-apis-file-picker.md)（等价华为页：[@ohos.file.picker (选择器)](https://developer.huawei.com/consumer/cn/doc/doccenter-references/api/js-apis-file-picker?docScope=all)）

**关键卡点（这是本次调研最重要的发现）：**

**【官方 · 置信度高】** `selectMode` / `DocumentSelectMode` 的系统能力是 `SystemCapability.FileManagement.UserFileService.FolderSelection`。而在 OpenHarmony 官方 **手机 SysCap 清单**中，`SystemCapability.FileManagement.UserFileService` 在列，但 **`SystemCapability.FileManagement.UserFileService.FolderSelection` 不在列**：

```text
phone-syscap-list.md 第 163-168 行：
- SystemCapability.FileManagement.File.FileIO            ← 有
- SystemCapability.FileManagement.File.Environment       ← 有（但只是"环境"基础能力）
- SystemCapability.FileManagement.AppFileService         ← 有
- SystemCapability.FileManagement.UserFileService        ← 有
- SystemCapability.FileManagement.UserFileService.FolderSelection   ← 缺失！
- SystemCapability.FileManagement.File.Environment.FolderObtain     ← 缺失！
- SystemCapability.FileManagement.AppFileService.FolderAuthorization ← 缺失！
```

来源：[phone-syscap-list.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/phone-syscap-list.md)

同时 `Environment.getUserDownloadDir()` 的 doc 明确写 **"设备行为差异：该接口在 2in1 中可正常调用，在其他设备类型中返回 801 错误码"**。

社区侧完全吻合 —— 华为开发者问答上有开发者原话："**鸿蒙 FilePicker 选文件夹手机秒退：DocumentSelectMode.FOLDER 仅 2in1 设备支持**"，参见 [华为开发者问答 0203225049760963212](https://developer.huawei.com/consumer/cn/forum/topic/0203225049760963212?fid=0109140870620153026)（【社区 · 置信度中】，页面为 JS 渲染，未取到正文，仅取到标题）

**必须用 `canIUse` 做能力探测，不要硬调：**

```ts
// 没有这个 SysCap 就不要拉起 FOLDER 模式，否则应用秒退/抛 801
if (!canIUse('SystemCapability.FileManagement.UserFileService.FolderSelection')) {
  console.error('this device does not support folder selection');
  // → 走降级方案（见 1.6）
  return;
}
```

**⚠️ API 12 vs API 26 差异：**

| 参数 | 起始版本 | 设备差异 |
|---|---|---|
| `DocumentSelectMode`（含 FOLDER/MIXED） | API 11 | 系统能力受限于 FolderSelection SysCap |
| `authMode`（授权模式，需配 `defaultFilePathUri`） | API 12 | **文档明说"该参数在 2in1 设备中可正常使用，在其他设备中无效果"** |
| `multiAuthMode` + `multiUriArray`（批量授权） | API 15 | **文档明说"该参数在 Phone 设备中可正常使用，在其他设备中无效果"** ← 手机专属！ |
| `allowsMulFolderSelection`（多选文件夹） | **26.0.0** | 需配合 `selectMode = FOLDER/MIXED`，系统能力同样是 FolderSelection |

来源：同 [js-apis-file-picker.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-core-file-kit/js-apis-file-picker.md) 第 763-778 行

**【官方 · 置信度中】** 注意 `authMode`（授权一个目录）只在 2in1 有效，但 `multiAuthMode`（批量授权**文件**）**只在 Phone 有效**，且文档明确括号注明"仅支持文件，文件夹不生效"。这基本坐实了：**华为在手机上的目录级授权通路是关闭的，只给了"批量授权一批文件"作为替代。**

> 【社区 · 置信度低，需实测】华为开发者博客有一篇《FilePicker 文件夹选择：设备能力降级与 API 26 新能力》（[链接](https://developer.huawei.com/consumer/cn/blog/topic/03225221088684133)），标题暗示 API 26 有"新能力"。该页面 JS 渲染无法取到正文，**我无法确认 API 26 是否在手机上开放了 FolderSelection**。建议你在真机（HarmonyOS 7.0.0）上用 `canIUse` 实测一次，这是唯一可靠的判据。

---

## 1.2 `DocumentSelectOptions` 有哪些字段？

**【官方 · 置信度高】** 完整字段表（含目录相关标注）：

```ts
const documentSelectOptions = new picker.DocumentSelectOptions();

// 选择文件最大个数
//  · API 20 及之前：单次上限 500，默认 500；"目录选择功能仅对具备该系统能力的设备开放，且单次最多可选择 1 个目录"
//  · API 21 起取消文件数量限制（建议不超过 1 万）
//  · API 23 起取消目录选择数量限制
documentSelectOptions.maxSelectNumber = 5;

// 指定选择的文件或目录的 URI（默认为空 = 拉起最近打开页）
documentSelectOptions.defaultFilePathUri = 'file://docs/storage/Users/currentUser/test';

// ★ 目录选择核心字段（API 11+），系统能力 FolderSelection
documentSelectOptions.selectMode = picker.DocumentSelectMode.FILE; // FILE | FOLDER | MIXED

// 后缀过滤：'描述|.后缀1,.后缀2'，数组长度 ≤ 100
documentSelectOptions.fileSuffixFilters = ['图片(.png, .jpg)|.png,.jpg', '文档|.txt', '.pdf'];

// ★ 授权模式（API 12+），authMode=true 时 defaultFilePathUri 必填
//   设备行为差异：仅 2in1 有效，其他设备无效果
documentSelectOptions.authMode = false;

// ★ 批量授权模式（API 15+），true 时只有 multiUriArray 生效
//   设备行为差异：仅 Phone 有效
documentSelectOptions.multiAuthMode = false;

// ★ 批量授权的 URI 数组（API 15+，仅支持文件，文件夹不生效）
documentSelectOptions.multiUriArray = [
  'file://docs/storage/Users/currentUser/test',
  'file://docs/storage/Users/currentUser/2test'
];

// 聚合视图模式（API 15+，Phone 有效）
documentSelectOptions.mergeMode = picker.MergeTypeMode.DEFAULT;

// 是否支持加密（API 19+，仅文件）
documentSelectOptions.isEncryptionSupported = false;

// ★ 多选文件夹（API 26+，需 selectMode=FOLDER/MIXED），系统能力 FolderSelection
documentSelectOptions.allowsMulFolderSelection = false;
```

来源：[js-apis-file-picker.md#documentselectoptions](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-core-file-kit/js-apis-file-picker.md) 第 763-778 行；用法示例见官方指南 [select-user-file.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/file-management/select-user-file.md) 第 34-59 行

---

## 1.3 能否持久化访问授权？

**答：能，而且普通三方应用可以申请到（这是好消息）。**

**【官方 · 置信度高 · 已从官方权限定义源码核对】**

我在 OpenHarmony access token 仓库的权限定义文件中直接查到了这个权限：

```json
{
  "name": "ohos.permission.FILE_ACCESS_PERSIST",
  "grantMode": "system_grant",
  "availableLevel": "normal",
  "availableType": "NORMAL",
  "since": 11,
  "provisionEnable": true,
  "distributedSceneEnable": false
}
```

来源：[security_access_token / permission_definitions.json](https://gitee.com/openharmony/security_access_token/blob/master/services/accesstokenmanager/permission_definitions.json)

**含义（重要）：**

- `availableLevel: normal` → **不是 system_basic**，三方应用可直接申请，**不需要 ACL**（ACL 只在 system_basic 及以上才需要）
- `grantMode: system_grant` → **声明即授予，不需要弹窗**，只要在 `module.json5` 里写上就会自动生效
- **所以：这个权限是"免费"的，没有申请门槛。**

**module.json5 声明：**

```json5
{
  "module": {
    "requestPermissions": [
      { "name": "ohos.permission.FILE_ACCESS_PERSIST" }
    ]
  }
}
```

**四个 API（`@kit.CoreFileKit` 的 `fileShare`）：**

| API | 起始 | 作用 |
|---|---|---|
| `fileShare.persistPermission(policies)` | API 11 | 把**临时**授权持久化到系统数据库 |
| `fileShare.activatePermission(policies)` | API 11 | 每次应用启动后**重新使能**已持久化的授权（不调就用不了！） |
| `fileShare.deactivatePermission(policies)` | API 11 | 暂时收回使能 |
| `fileShare.revokePermission(policies)` | API 11 | 彻底取消持久化 |
| `fileShare.checkPersistentPermission(policies)` | — | 批量校验是否已持久化，返回 `Promise<Array<boolean>>` |
| `fileShare.getPathPolicyInfo` / `PathPolicyInfo` | API 15 | 按 path 查询策略 |

`policies` 数组大小上限 **500**。`operationMode` 可组合：`fileShare.OperationMode.READ_MODE | fileShare.OperationMode.WRITE_MODE`。

来源：[js-apis-fileShare.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-core-file-kit/js-apis-fileShare.md)；指南 [file-persistPermission.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/file-management/file-persistPermission.md)（等价华为页：[授权持久化](https://developer.huawei.com/consumer/cn/doc/doccenter-capabilities/file-persistpermission?docScope=all)）

**官方文档里的硬约束（务必遵守）：**

> 1. 持久化授权文件信息建议应用在本地存储数据，供后续按需激活持久化文件。
> 2. 持久化授权的数据存储在系统的数据库中，**应用或者设备重启后需要激活已持久化的授权才可以正常使用**。
> 3. 持久化权限接口（可以使用 canIUse 接口进行校验能力是否可用），且需要申请对应的权限。
> 4. **应用在卸载时会将之前的授权数据全部清除**，重新安装后需要重新授权。
> 5. **只能对已获取到的临时权限进行持久化授权操作，否则会报错。**
>
> —— [file-persistPermission.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/file-management/file-persistPermission.md) 第 74-80 行

**【官方 · 置信度中】** 还有一个前置能力校验，官方给的是 **`SystemCapability.FileManagement.AppFileService.FolderAuthorization`**，而它在**手机 SysCap 清单里也是缺失的**：

```ts
if (!canIUse('SystemCapability.FileManagement.AppFileService.FolderAuthorization')) {
    console.error('this api is not supported on this device');
    return;
}
```

来源：同上 file-persistPermission.md 第 21-28 行。
→ **我的判断：手机上的"文件夹级持久化授权"不被支持；但"文件级持久化授权"（一批 .txt 文件）是可以的。** 这一点对你的"码字"场景其实完全够用（见 1.6）。

**API 26 新增（对你有用）**：从 **API version 24** 起支持"持久化权限保留"，应用卸载后重装可恢复上次的持久化权限：

```json5
// src/main/module.json5 的 module.metadata 中
"metadata": [
  { "name": "ohos.fileshare.supportPreservePersistentPermission" }
]
```

来源：file-persistPermission.md 第 194-221 行

---

## 1.4 能否列出所选目录下的文件？picker 返回的 URI 能直接 `fs.listFile` / `fs.stat` 吗？

**答：能，但必须先把 URI 转成沙箱路径，不能自己拼字符串。**

**【官方 · 置信度高】** `fileIo.listFile` 的 `path` 参数文档写的是"**目录的应用沙箱路径**"，不是 URI：

```ts
listFile(path: string, options?: ListFileOptions): Promise<string[]>
```

支持递归：

```ts
import { fileIo, Filter, ListFileOptions } from '@kit.CoreFileKit';

const listFileOption: ListFileOptions = {
  recursion: true,      // 递归列出所有文件，相对路径以 "/" 开头
  listNum: 0,
  filter: {
    suffix: ['.png', '.jpg'],
    displayName: ['*abc', 'efg*'],
    fileSizeOver: 1024
  }
};
fileIo.listFile(pathDir, listFileOption).then((filenames: Array<string>) => { /* ... */ });
```

来源：[js-apis-file-fs.md#fileiolistfile](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-core-file-kit/js-apis-file-fs.md) 第 3035-3087 行

**【官方 · 置信度高】** URI → 沙箱路径用 `fileUri.FileUri#path`：

```ts
import { fileUri } from '@kit.CoreFileKit';

// FileUri 构造函数接受的 URI 类型（官方原文）：
//  - 应用沙箱URI：      file://<bundleName>/<sandboxPath>
//  - 公共目录文件类URI：file://docs/storage/Users/currentUser/<publicPath>
//  - 公共目录媒体类URI：file://media/<mediaType>/IMG_DATETIME_ID/<displayName>
const fu = new fileUri.FileUri('file://docs/storage/Users/currentUser/Download/a.txt');
const sandboxPath: string = fu.path;      // → 沙箱路径，可直接喂给 fileIo.*
const fileName: string  = fu.name;        // → 'a.txt'
const dirUri: string    = fu.getFullDirectoryUri();  // 所在目录 URI
```

来源：[js-apis-file-fileuri.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-core-file-kit/js-apis-file-fileuri.md)

**⚠️ 官方明确警告不要自己拆 URI 拼路径：**

> path 属性说明：将 uri 转换成对应的沙箱路径 path。1、uri 转 path 过程中会将 uri 中存在的 ASCII 码进行解码后拼接在原处……2、**转换处理为系统约定的字符串替换规则（规则随系统演进可能会发生变化），转换过程中不进行路径校验操作，无法保证转换结果的一定可以访问。**

> 用户文件 URI 是文件的唯一标识……**不建议开发者解析 URI 中的片段用于业务代码开发**。
> —— [user-file-uri-intro.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/file-management/user-file-uri-intro.md) 第 9 行

**前提条件（缺一不可）：**

1. 该 URI 必须**已经通过 Picker 获得**（或已 `persistPermission` + `activatePermission` 使能），否则 `13900012 Permission denied`
2. 目录遍历要求对**目录 URI 本身**持有权限
3. 更稳的做法是**直接对 URI 调用 `fileIo.openSync(uri, ...)`**，官方 `select-user-file.md` 和 `fileUri` 文档都推荐这条路：

```ts
import { fileIo } from '@kit.CoreFileKit';

// 官方推荐：拿到 URI 直接 open，不解析路径
let file = fileIo.openSync(uri, fileIo.OpenMode.READ_ONLY);
let buffer = new ArrayBuffer(4096);
let readLen = fileIo.readSync(file.fd, buffer);
fileIo.closeSync(file);
```

来源：[select-user-file.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/file-management/select-user-file.md) 第 86-105 行

**【社区 · 置信度中】** 有一篇 51CTO 文章《HarmonyOS 7 Core File Kit 多目录授权与文件夹选择机制》（[链接](https://ost.51cto.com/posts/57251)）总结：`FOLDER` 模式返回文件夹 URI 可后续遍历；`MIXED` 模式返回的可能是文件也可能是文件夹，须先判断；`allowsMulFolderSelection` 打开后返回的是 URI **数组**，很多人只取第一个导致漏文件夹。此文示例代码有一处笔误（写成 `options.mode` 而官方字段名是 `options.selectMode`），以官方文档为准。同文也指出**授权是会话绑定的临时凭证，不做持久化下次启动就失效** —— 这一点与官方文档一致。

---

## 1.5 限制边界：HarmonyOS 上做不到什么？

**结论：做不到类似 Android 的 `MANAGE_EXTERNAL_STORAGE`（全盘/任意目录遍历）。**

**【官方 + 推理 · 置信度中高】**

1. **没有"任意遍历用户磁盘"的权限。** OpenHarmony 权限定义里存在 `ohos.permission.READ_WRITE_USER_FILE`，但它是 **`system_basic` + `system_grant`**（我在权限定义 JSON 中核对过），三方应用拿不到。

   | 权限 | availableLevel | grantMode | 三方可得？ |
   |---|---|---|---|
   | `ohos.permission.READ_WRITE_USER_FILE` | **system_basic** | system_grant | ❌ |
   | `ohos.permission.READ_WRITE_DESKTOP_DIRECTORY` | **system_basic** | user_grant | ❌ |
   | `ohos.permission.READ_WRITE_DOWNLOAD_DIRECTORY` | **normal** | user_grant | ✅ 弹窗可得 |
   | `ohos.permission.READ_WRITE_DOCUMENTS_DIRECTORY` | **normal** | user_grant | ✅ 弹窗可得 |
   | `ohos.permission.READ_IMAGEVIDEO` | **system_basic** | user_grant | ❌（走 photoAccessHelper） |
   | `ohos.permission.FILE_ACCESS_PERSIST` | **normal** | system_grant | ✅ 免弹窗 |

   来源：[permission_definitions.json](https://gitee.com/openharmony/security_access_token/blob/master/services/accesstokenmanager/permission_definitions.json)

2. **公共目录根路径本身也拿不到（手机上）。** `Environment.getUserDownloadDir()` 需要 `SystemCapability.FileManagement.File.Environment.FolderObtain`，官方明确"**仅支持 2in1 设备**"，手机上返回 801。

   来源：[request-dir-permission.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/file-management/request-dir-permission.md) 第 14 行、[js-apis-file-environment.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-core-file-kit/js-apis-file-environment.md) 第 29 行

3. **所以实际的边界是：**
   - ✅ 你只能在 **Picker 显式授权过的 URI 集合** 内自由读写（文件级）
   - ✅ 你可以在 **自建 / 预授权公共目录**（Download / Documents）内自由读写（目录级，需弹窗授权）
   - ✅ 你可以在 **应用沙箱** 内任意遍历（零权限）
   - ❌ 你不能枚举用户未授权的任何目录
   - ❌ 手机上不能"选一个父目录 → 递归拿到整个子树"

---

## 1.6 实际可行的最佳方案（针对你的"码字"场景）

### 方案 A（最稳，推荐做基线）：应用沙箱 + 导入/导出

**【官方 · 置信度 高】零权限，零弹窗，随便遍历。**

```ts
import { fileIo, Filter, ListFileOptions } from '@kit.CoreFileKit';
import { common } from '@kit.AbilityKit';

// 目录路径（无需任何权限）：
//   context.filesDir       → /data/storage/el2/base/haps/entry/files       （持久化，推荐放这）
//   context.cacheDir       → .../cache                                      （系统可能清理）
//   context.tempDir        → .../temp
//   context.databaseDir    → .../database
// 说明：el2 = 加密等级 EL2（用户级），应用卸载即清除

function workspaceOps(context: common.UIAbilityContext) {
  const dir = context.filesDir + '/workspace';
  if (!fileIo.accessSync(dir)) {
    fileIo.mkdirSync(dir, true);   // 递归建目录
  }
  // 建/写文件
  const f = fileIo.openSync(dir + '/novel.txt',
      fileIo.OpenMode.READ_WRITE | fileIo.OpenMode.CREATE);
  fileIo.writeSync(f.fd, '第一章……');
  fileIo.closeSync(f);

  // 递归列目录
  const opt: ListFileOptions = { recursion: true, listNum: 0,
    filter: { suffix: ['.txt', '.md'] } };
  for (const name of fileIo.listFileSync(dir, opt)) {
    console.info('file:', name, 'size:', fileIo.statSync(dir + '/' + name).size);
  }
}
```

来源：[app-file-access.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/file-management/app-file-access.md)

**优点**：100% 可控；H5 侧的"文件列表/多文件"完全由原生 fs 支撑，不再受 Picker 限制；unzip/tar/遍历 subtree 全都免费。
**缺点**：用户看不到这些文件（除非你在设置里做导入/导出）。

### 方案 B（手机上的"文件夹"替代）：批量授权一批 .txt 文件

**【官方 · 置信度 高】这是华为在手机上给的正解。**

```ts
import { picker, fileShare } from '@kit.CoreFileKit';
import { common } from '@kit.AbilityKit';

// 1) 先扫描出候选 URI（例如从文管根目录 /Download/mybook 下）
const ALL_TXT_URIS: string[] = [
  'file://docs/storage/Users/currentUser/Download/mybook/a.txt',
  'file://docs/storage/Users/currentUser/Download/mybook/b.txt',
  // ... 上限 500 个
];

// 2) 批量授权（Phone 专属能力，API 15+）
async function batchAuthorize(ctx: common.UIAbilityContext) {
  const opts = new picker.DocumentSelectOptions();
  opts.multiAuthMode = true;              // 仅 Phone 有效
  opts.multiUriArray = ALL_TXT_URIS;      // 仅支持文件，文件夹不生效
  const dvp = new picker.DocumentViewPicker(ctx);
  const uris = await dvp.select(opts);    // 用户一次性点"允许"
  return uris;
}

// 3) 持久化 + 每次启动激活
async function persistAndActivate(uris: string[]) {
  const policies: fileShare.PolicyInfo[] = uris.map(u => ({
    uri: u,
    operationMode: fileShare.OperationMode.READ_MODE | fileShare.OperationMode.WRITE_MODE
  }));
  await fileShare.persistPermission(policies);          // 写系统库
  const activated = await fileShare.activatePermission(policies);  // 本次启动使能
  // 把 uris 存进 preferences，下次启动直接 activatePermission
}
```

来源：[select-user-file.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/file-management/select-user-file.md) 第 48-51 行、[file-persistPermission.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/file-management/file-persistPermission.md)

**优点**：用户授权一次，之后启动只需 `activatePermission`，不用再弹框；文件真实可见可备份。
**缺点**：**新增文件仍需重新授权**（新文件不在已持久化集合里）—— 对"码字"场景意味着：你要先在**应用沙箱里写**，再周期性"导出/同步"到已授权的 .txt 文件。

### 方案 C（如果实测 `FolderSelection` 在 HarmonyOS 7 手机上可用）

**【推理 + 官方 API · 置信度中】** 若 `canIUse('SystemCapability.FileManagement.UserFileService.FolderSelection')` 在真机返回 `true`，则最佳路径成立：

```ts
async function pickFolderAndPersist(ctx: common.UIAbilityContext) {
  if (!canIUse('SystemCapability.FileManagement.UserFileService.FolderSelection')) {
    throw new Error('no folder selection on this device');
  }
  const opts = new picker.DocumentSelectOptions();
  opts.selectMode = picker.DocumentSelectMode.FOLDER;
  opts.allowsMulFolderSelection = false;   // API 26+，按需

  const dvp = new picker.DocumentViewPicker(ctx);
  const folderUris = await dvp.select(opts);
  const folderUri = folderUris[0];

  // 持久化 + 激活
  const pol: fileShare.PolicyInfo[] = [{
    uri: folderUri,
    operationMode: fileShare.OperationMode.READ_MODE | fileShare.OperationMode.WRITE_MODE
  }];
  await fileShare.persistPermission(pol);
  await fileShare.activatePermission(pol);

  // 遍历目录：URI → 沙箱路径 → listFile(recursion)
  const sandboxPath = new fileUri.FileUri(folderUri).path;
  const files = await fileIo.listFile(sandboxPath, { recursion: true, listNum: 0 });
  return files;
}
```

> ⚠️ **【社区 · 置信度中】已知坑**：华为开发者问答有一条"对文件夹持久化权限以后，向文件夹中写入文件时，打不开文件，报错 No such file or directory"（[0208208108060612060](https://developer.huawei.com/consumer/cn/forum/topic/0208208108060612060?fid=0109140870620153026)，页面未取到正文）。**说明"持久化目录 URI + 在其中新建文件"这条链路存在已知问题，必须真机实测。** 相对地，**"持久化文件 URI + 写已知文件"是社区和官方样例都跑通的**。

### 方案 D：访问"下载"目录

**【官方 · 置信度 高】**

```json5
// module.json5
"requestPermissions": [
  { "name": "ohos.permission.READ_WRITE_DOWNLOAD_DIRECTORY" },   // normal + user_grant
  { "name": "ohos.permission.READ_WRITE_DOCUMENTS_DIRECTORY" }   // normal + user_grant
]
```

```ts
// 手机上 getUserDownloadDir() 会抛 801，所以必须走"用户自己选 Download 目录下的文件"这条路：
//   → DocumentViewPicker 里 defaultFilePathUri 指向 Download
//   → 或让用户在文管里进 Download 选文件，再持久化该文件 URI
const opts = new picker.DocumentSelectOptions();
opts.defaultFilePathUri = 'file://docs/storage/Users/currentUser/Download';
opts.fileSuffixFilters = ['文本(.txt)|.txt'];
opts.maxSelectNumber = 500;
```

**【官方 · 置信度 高】"不需要权限就写下载目录"的办法：不存在。** 官方明确"公共目录获取接口仅用于获取公共目录路径，**不对公共目录访问权限进行校验**。若需访问公共目录需申请对应的公共目录访问权限"；且 `getUserDownloadDir` 只在 2in1 可用。
唯一"免权限"写公共区的官方通路是 **`DocumentViewPicker.save()`（保存框）**，但那是"用户点一次存一个文件"，不是目录访问。

来源：[request-dir-permission.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/file-management/request-dir-permission.md) 第 21 行

---

## 1.7 问题一 可行性总表

| 子问题 | 判定 | 依据 |
|---|---|---|
| `DocumentViewPicker` 选文件夹 | ⚠️ **API 存在，手机不可行** | FolderSelection SysCap 不在 phone 清单 |
| `DocumentSelectOptions` 目录字段 | ✅ 可行 | `selectMode` / `allowsMulFolderSelection` / `multiAuthMode` |
| 持久化授权（文件级） | ✅ **可行**，权限 normal+system_grant 免弹窗 | 官方权限定义 + file-persistPermission |
| 持久化授权（目录级） | ⚠️ **有条件可行**，需 FolderAuthorization SysCap（手机缺失），且有已知写入坑 | 官方 canIUse 片段 + 社区问答 |
| 遍历所选目录 | ⚠️ **有条件可行**，需 `FileUri#path` + `fileIo.listFile(recursion)` + 已授权 | 官方 fs / fileuri 文档 |
| 任意遍历用户磁盘 | ❌ **不可行** | 无 normal 级全盘权限 |
| 私目录 `context.filesDir` | ✅ **可行**，零权限 | app-file-access.md |
| 公共"下载"目录（手机） | ⚠️ **有条件可行**，`READ_WRITE_DOWNLOAD_DIRECTORY`（normal+user_grant）弹窗，或 Picker | request-dir-permission.md |
| 免权限写"下载" | ❌ 不存在（仅 `save()` 单文件保存框） | request-dir-permission.md |

---
---

# 问题二：能否在 HarmonyOS 应用里实现 Git？

## 2.1 有没有可用的 Git 实现？

### 2.1.1 libgit2 的 OHOS/HarmonyOS 移植

**【社区+官方仓库 · 置信度 中高】**

- ✅ **有 libgit2 的 OpenHarmony 适配项目，但定位是 PC/2in1 命令行工具链，不是手机 App 的 NAPI 库。**
  - `ohos-libgit2`：Gitee/AtomGit 上"基于 OpenHarmony PC 生态的 Git 核心库适配项目" —— [gitcode.com/oh-tpc/ohos-libgit2](https://gitcode.com/oh-tpc/ohos-libgit2)（页面 JS 渲染，README 正文未取到；仓库标题/描述可确认存在）
  - `build_in_harmonyos`：OpenHarmony PC 生态的"开源软件编译兼容性框架"，**archives 里有 `libgit2/1.9.4`** —— [gitcode 目录](https://gitcode.com/dIT8Zv/build_in_harmonyos/tree/feat/unicorn-2.1.4/archives/l/libgit2/1.9.4/)，并有 "feat(libgit2-tools): 1.9.6 适配 HarmonyOS PC" 的 MR
- ❌ **ohpm registry 上没有任何 libgit2 的 ohpm 包。** 我实测了 registry 的 detail 接口：

  ```
  EXISTS  @ohos/axios
  absent  @ohos/libgit2
  absent  @ohos/git
  absent  @ohos/nodegit
  absent  @ohos/simple-git
  absent  @ohos/isomorphic-git
  absent  ohos-git
  ```
  （接口：`https://ohpm.openharmony.cn/ohpmweb/registry/oh-package/openapi/v1/detail/<pkg>`）

- **【社区 · 置信度 中高】移植到手机 App 的实战难点已被公开记录**：GitUI 的 HarmonyOS PC 移植文章（[jishuzhan 镜像](https://jishuzhan.net/article/2099532071795085314)，原始 [CSDN](https://blog.csdn.net/m0_38139250/article/details/165180926)）总结了三类坑，**全部对手机同样适用**：
  1. **vendored OpenSSL 交叉编译失败**：`cc` crate 不读 `.cargo/config.toml` 的 linker，必须设 `CC_aarch64_unknown_linux_ohos` 环境变量，否则回落到宿主 x86_64 gcc → `#error "unsupported ARM architecture"`
  2. **libgit2 的目录所有权校验误报**：libgit2 打开仓库时做 owner validation（同 git 的 `safe.directory`，防 CVE-2022-24765），比较文件系统 UID 与进程 EUID。**在鸿蒙沙箱/单用户文件模型下直接报 `Owner (-36)`**
  3. **gitoxide（gix）索引槽位溢出崩溃**：大仓库在 `gix-odb` 的固定槽位 slotmap 上触发 `InsufficientSlots` panic

  该文章还给出可复用的工程模式：用 `cfg(target_env)` 按目标平台切换后端，而不是逐个依赖打补丁。目标平台 `aarch64-unknown-linux-ohos`，编译器 OHOS SDK clang 15.0.4，链接 musl libc，产物约 15 MB。

- **【推理 · 置信度 中】** 结论：**把 libgit2 编到手机 App 里在技术上不是不可能（OHOS NDK + CMake + NAPI 是官方支持的），但工作量巨大（OpenSSL 交叉编译 + libgit2 owner 校验绕过 + 无现成 ohpm 包），对"码字 App"性价比极低。**

**NAPI 集成方式（官方支持，供参考）：**

```json5
// entry/build-profile.json5
"buildOption": {
  "externalNativeOptions": {
    "path": "./src/main/cpp/CMakeLists.txt",
    "arguments": "",
    "cppFlags": "",
    "abiFilters": ["arm64-v8a"]
  }
}
```

```cmake
# CMakeLists.txt 要点（libgit2 需自行交叉编译）
add_library(entry SHARED napi_init.cpp)
target_link_libraries(entry PUBLIC libace_napi.z.so libhilog_ndk.z.so ./thirdparty/libgit2.a)
```
（`externalNativeOptions` / `libace_napi.z.so` 为官方 NDK 工程标准写法；libgit2 静态库需你自己用 OHOS SDK clang 交叉编译产出）

### 2.1.2 `isomorphic-git` 能否在 ArkWeb 里跑？

**答：可以，而且这是最现实的路线。我把它的 npm 包拉下来做了静态分析。**

**【实测 · 置信度高】** `isomorphic-git@1.43.1` 的依赖（registry.npmmirror.com 实测）：

```json
"dependencies": {
  "pako": "^1.0.10",           // 纯 JS zlib
  "pify": "^4.0.1",
  "diff3": "0.0.3",
  "crc-32": "^1.2.0",
  "ignore": "^5.1.4",
  "sha.js": "^2.4.12",         // 纯 JS SHA-1/SHA-256 ← 不需要 node:crypto
  "async-lock": "^1.4.1",
  "minimisted": "^2.0.0",
  "simple-get": "^4.0.1",      // 仅 Node 客户端用
  "clean-git-ref": "^2.0.1",
  "readable-stream": "^4.0.0"  // 仅 Node 客户端用
}
```

**我在 `index.umd.min.js`（265 KB，浏览器 UMD 构建）上统计的结果：**

| 统计项 | 计数 | 含义 |
|---|---|---|
| `require(` | **0** | ✅ 完全自包含，**没有 Node 模块运行时依赖** |
| `crypto` | 1 | 仅一处 `crypto.subtle.digest("SHA-1", ...)` |
| `subtle` | 1 | 同上，**有纯 JS `sha.js` 兜底**（bundle 里先 try WebCrypto，失败回落 `new SHA1().update().digest("hex")`） |
| `Buffer` | **157** | ⚠️ **需要 `Buffer` polyfill** |
| `process.` | 11 | ⚠️ 只用于 `process.domain`（async-lock 的 domainReentrant，默认关闭）和 `process.platform`（`core.filemode` 判断），`typeof process` 已做守卫，**提供 `process = { platform: 'linux' }` 空壳即可** |
| `TextEncoder` / `TextDecoder` | 各 1 | Chromium 原生支持 |
| `fetch` | 15 | 浏览器 HTTP 客户端用 |
| `window.` | **0** | ✅ 不依赖 window，可跑在 Worker 里 |

**关键结论：isomorphic-git 的浏览器构建只需要 3 样东西：`Buffer` polyfill、`process` 空壳、以及一个 `fs` 适配器。`crypto.subtle` 是可选的（有 `sha.js` 兜底）；`http` 走 `fetch`。**

**【官方文档 · 置信度高】** 自定义 `fs` 适配器需要实现的完整接口（推荐 `promises` 形式）：

```
fs.promises.readFile(path[, options])
fs.promises.writeFile(file, data[, options])
fs.promises.unlink(path)
fs.promises.readdir(path[, options])
fs.promises.mkdir(path[, mode])
fs.promises.rmdir(path)
fs.promises.stat(path[, options])
fs.promises.lstat(path[, options])
fs.promises.readlink(path[, options])   // optional，仅含 symlink 的仓库需要
fs.promises.symlink(target, path[, type]) // optional
fs.promises.chmod(path, mode)            // optional
```

> 官方原话：*"If your `fs` object provides an enumerable `promises` property, `isomorphic-git` will use the 'promise' API exclusively."*

来源：[isomorphic-git.org/docs/en/fs](https://isomorphic-git.org/docs/en/fs)

**【官方文档 · 置信度高】** 自定义 `http` 适配器只需一个 `request` 方法：

```js
const http = {
  async request({ url, method, headers, body, onProgress /*, signal */ }) {
    // body: AsyncIterableIterator<Uint8Array>
    return {
      url, method, headers, body, statusCode, statusMessage
    };
  }
};
```

> 官方原话：*"You don't have to support streaming… you can simply fake it by returning an array with a single Uint8Array inside it. This works because the async iteration protocol (`for await ... of`) will fallback to the sync iteration protocol, which is supported by plain Arrays."*

来源：[isomorphic-git.org/docs/en/http](https://isomorphic-git.org/docs/en/http)

**【实测 · 置信度 中】** `Buffer` polyfill：`<script src="https://unpkg.com/buffer/index.js">` 或打包时用 `buffer` npm 包（`buffer@6` 约 40 KB）。也可以避免 —— 但 157 处引用意味着**必须提供**。

**【判断 · 置信度 中】`crypto.subtle` 是否在 ArkWeb 可用？** ArkWeb 是 Chromium 内核的 Web 组件（HarmonyOS 从 4.1 起内核版本持续升级），`crypto.subtle` 是 Chromium 标准能力。**但我没有找到华为官方文档明确列出 ArkWeb 支持的 Web API 白名单**，因此标注为**需真机实测**。好在 isomorphic-git 有纯 JS `sha.js` 兜底，**即使 `crypto.subtle` 不可用也能工作**（只慢一点）。

### 2.1.3 `simple-git` / `nodegit` 是否需要 Node 运行时？

**【推理 · 置信度 高】需要，且**在 HarmonyOS 上都没有落地路径**：**

| 库 | 依赖 | HarmonyOS 可行性 |
|---|---|---|
| `simple-git` | 纯 JS，但内部 `child_process.spawn('git', ...)` | ❌ **不可行** —— 要求系统里有 `git` 可执行文件 |
| `nodegit` | libgit2 的 **Node.js 原生插件**（N-API/`node-gyp`，依赖 Node ABI + V8 头文件） | ❌ **不可行** —— ArkTS 运行时不是 Node，V8 头文件不存在 |
| `dugite` | 打包 git 二进制 + `child_process.execFile` | ❌ **不可行** —— 同上 |

HarmonyOS 没有 Node.js 运行时（ArkTS 是独立语言/运行时，虽然语法接近 TS，但没有 `require('fs')`、没有 `child_process`、没有 N-API 的 Node 侧 ABI）。

### 2.1.4 有没有 NAPI/C++ 方式集成 libgit2 的先例？

**【社区 · 置信度 中高】**

- **PC 侧有**：GitUI（Rust + git2 crate + gitoxide）已 100% 构建成功并跑通，产物 ~15 MB，目标 `aarch64-unknown-linux-ohos`。见上文 [jishuzhan 文章](https://jishuzhan.net/article/2099532071795085314)。
- **手机 App 侧没有找到公开先例**（`isomorphic-git`、`nodegit`、`simple-git`、`libgit2` 在 ohpm 上均不存在）。
- **反例（说明"原生能力可以做 Git 相关事"确实可行）**：AtomGit 官方有 HarmonyOS 手机版 App —— [hust-open-atom-club/AtomGit-mobile4Harmony](https://github.com/hust-open-atom-club/AtomGit-mobile4Harmony)。**但我无法从其 README 确认它用的是 libgit2 还是 REST API**（GitHub 页面内容需进一步抓取），标为**未确认**。
- **相关可信先例**：`@ohos/libssh`（libssh 0.11.1 + NAPI，见 2.4）证明**"C/C++ 库 + NAPI + ohpm 包"这条链路在手机上是通的**，只是 libgit2 还没人做。

---

## 2.2 网页层用 isomorphic-git：适配器与 `javaScriptProxy` 的能力边界

### 2.2.1 `javaScriptProxy` 是同步还是异步？

**【官方 · 置信度高】** 两个列表，语义明确不同：

```ts
// JavaScriptProxy 接口定义（API 12+）
interface JavaScriptProxy {
  object: object;              // 只能声明方法，不能声明属性
  name: string;                // 与 window 中的对象名一致
  methodList: Array<string>;   // ★ 同步方法
  controller: WebController | WebviewController;
  asyncMethodList?: Array<string>;  // ★ API 12+，异步方法 —— "异步方法无法获取返回值"
  permission?: string;         // API 12+，JSBridge 权限管控 JSON
}
```

- `methodList` 中的方法是**同步方法**，**可以有返回值**
- `asyncMethodList` 中的方法是**异步方法，无法获取返回值**（单向通知）
- **注册时"同步与异步列表请至少选择一项不为空，可同时注册两类方法"**
- 一个 `javaScriptProxy` **只能注册一个对象**；多对象用 `controller.registerJavaScriptProxy(obj, name, methodList, asyncMethodList?, permission?)`

来源：[arkts-basic-components-web-attributes.md#javascriptproxy](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-arkweb/arkts-basic-components-web-attributes.md) 第 122-197 行、[arkts-basic-components-web-i.md#javascriptproxy12](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-arkweb/arkts-basic-components-web-i.md) 第 558-571 行

**【官方 · 置信度高】⚠️ 同步调用 = 阻塞渲染线程。** 官方示例中 `test()` 是同步的，H5 里直接 `testObjName.test()` 拿返回值。**在 `methodList` 里做文件 IO 会卡住 Web 渲染线程**（社区也有多篇"ArkWeb JSBridge 主线程卡顿"问答，如 [0208218974124493081](https://developer.huawei.com/consumer/cn/forum/topic/0208218974124493081?fid=0109140870620153026)）。

### 2.2.2 能否返回 Promise？能否传 ArrayBuffer？

**【官方 · 置信度高】能返回 Promise，官方有专门章节"Promise 场景"**：

```ts
// 应用侧 new Promise 作为返回值 → H5 侧可以 .then()
class TestClass {
  test(): Promise<string> {
    return new Promise((resolve, reject) => { /* ... */ });
  }
}
```
```html
<script>
function callArkTS() {
  testObjName.test().then(p => testObjName.toString(p)).catch(p => testObjName.toString(p));
}
</script>
```
来源：[web-in-page-app-function-invoking.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/web/web-in-page-app-function-invoking.md) 第 712-791 行

**⚠️ 但注意：能返回 Promise 的 `test` 必须注册在 `methodList`（同步列表）里 —— 因为它"有返回值"。`asyncMethodList` 里的方法返回值会被丢弃。**

**【官方 · 置信度高】支持的数据类型（官方"复杂类型使用方法"章节实测列出）：**

| 类型 | 支持 | 官方章节 |
|---|---|---|
| `string` / `number` / `boolean` | ✅ | 基础类型示例 |
| `Array`（如 `Array<number>`） | ✅ | "应用侧和前端页面之间传递 Array" |
| 自定义对象（如 `class Student {name; age}`） | ✅ | "非 Function 等复杂类型使用" |
| `Function` / 回调 | ✅ | "应用侧调用前端页面的 Callback" |
| 前端 Object 里的 Function | ✅ | "前端页面调用应用侧 Object 里的 Function" |
| `Promise` | ✅ | "Promise 场景" |
| **`ArrayBuffer`** | ⚠️ **`javaScriptProxy` 没有官方示例**；但 `runJavaScriptExt` 明确支持 | 见下 |

**`ArrayBuffer` 的准确结论：**

- ❌ `javaScriptProxy` 的文档**没有**任何 ArrayBuffer 参数/返回值的示例或类型表 → **【查不到官方确认】**
- ✅ **`WebMessagePort`（`postMessage` 通道）官方明确支持 ArrayBuffer 双向传输**：

```ts
// ArkTS → H5
const message = new webview.WebMessageExt();
message.setArrayBuffer(new ArrayBuffer(8));   // setArrayBuffer API 10+
this.nativePort.postMessageEventExt(message);
this.nativePort.onMessageEventExt((result) => {
  const buf = result.getArrayBuffer();        // getArrayBuffer API 10+
  console.info('byteLength:', buf.byteLength);
});
```
来源：[arkts-apis-webview-WebMessageExt.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-arkweb/arkts-apis-webview-WebMessageExt.md) 第 99-111、257-269 行；[arkts-apis-webview-WebMessagePort.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-arkweb/arkts-apis-webview-WebMessagePort.md) 第 285-373 行

- ✅ **`runJavaScriptExt` 支持 ArrayBuffer 双向**（ArkTS → H5 传脚本 ArrayBuffer，H5 → ArkTS 通过 `JsMessageExt.getArrayBuffer()`）：

```ts
runJavaScriptExt(script: string | ArrayBuffer): Promise<JsMessageExt>          // API 10+，ArrayBuffer 入参 API 12+
runJavaScriptExt(script: string | ArrayBuffer, callback: AsyncCallback<JsMessageExt>): void
```
```ts
const result = await this.controller.runJavaScriptExt('test()');
console.info('result type: ' + typeof result.getArrayBuffer());
console.info('byteLength: ' + result.getArrayBuffer().byteLength);
```
来源：[arkts-apis-webview-WebviewController.md#runjavascriptext10](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-arkweb/arkts-apis-webview-WebviewController.md) 第 1508-1525、1589-1590 行

**⚠️ 关于大小限制：【查不到】官方没有为 `javaScriptProxy` 或 `WebMessagePort` 给出明确的单次传输字节上限。** 官方在 `http` 模块给过 `expectDataType: Object` 时"最大长度为 65536 字符数"（[js-apis-http.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-network-kit/js-apis-http.md) 第 1268 行），但那是 HTTP 的约束，不能外推到 JSBridge。**必须真机压测。**

**⚠️ 关于"二进制传 ArrayBuffer"的工程建议（【推理 · 置信度 中】）：**

1. 优先用 **`runJavaScriptExt` + `ArrayBuffer`** 传二进制（官方明确支持），而不是 `javaScriptProxy` 返回值
2. 兜底方案：**`javaScriptProxy` 只传 base64 字符串**（H5 侧 `atob` 还原）—— 100% 安全，代价是 +33% 体积
3. **绝对不要在 `methodList`（同步）里做大文件读写**

### 2.2.3 ArkWeb + isomorphic-git 的推荐接线方式

```ts
import { webview } from '@kit.ArkWeb';
import { fileIo, picker, fileShare } from '@kit.CoreFileKit';
import { common } from '@kit.AbilityKit';

/** 原生文件系统门面：方法名不带 Async，但全部返回 Promise →
 *  注册到 methodList（因为要返回值），H5 侧 await 即可。 */
class MaziHarmonyFs {
  private baseDir: string = '';
  constructor(ctx: common.UIAbilityContext) {
    this.baseDir = ctx.filesDir + '/repo';   // git 仓库落在应用沙箱，零权限
    if (!fileIo.accessSync(this.baseDir)) { fileIo.mkdirSync(this.baseDir, true); }
  }

  /** H5 侧统一传相对路径，避免 H5 直接接触沙箱绝对路径 */
  private abs(p: string): string {
    return this.baseDir + (p.startsWith('/') ? p : '/' + p);
  }

  // ---- isomorphic-git fs.promises 适配器所需的最小集合 ----

  readFile(path: string): Promise<ArrayBuffer | string> {
    const f = fileIo.openSync(this.abs(path), fileIo.OpenMode.READ_ONLY);
    try {
      const st = fileIo.statSync(this.abs(path));
      const buf = new ArrayBuffer(st.size);
      const n = fileIo.readSync(f.fd, buf);
      return Promise.resolve(buf.slice(0, n));
    } finally { fileIo.closeSync(f); }
  }

  writeFile(path: string, data: ArrayBuffer | string): Promise<void> {
    const f = fileIo.openSync(this.abs(path),
        fileIo.OpenMode.READ_WRITE | fileIo.OpenMode.CREATE | fileIo.OpenMode.TRUNC);
    try { fileIo.writeSync(f.fd, data as ArrayBuffer); } finally { fileIo.closeSync(f); }
    return Promise.resolve();
  }

  readdir(path: string): Promise<string[]> {
    return fileIo.listFile(this.abs(path));                 // 非递归
  }

  mkdir(path: string): Promise<void> {
    if (!fileIo.accessSync(this.abs(path))) { fileIo.mkdirSync(this.abs(path), true); }
    return Promise.resolve();
  }

  rmdir(path: string): Promise<void> {
    fileIo.rmdirSync(this.abs(path)); return Promise.resolve();
  }

  unlink(path: string): Promise<void> {
    fileIo.unlinkSync(this.abs(path)); return Promise.resolve();
  }

  stat(path: string): Promise<FsStats> {
    const s = fileIo.statSync(this.abs(path));
    return Promise.resolve({
      type: s.isDirectory() ? 'dir' : 'file',
      mode: s.mode, size: s.size,
      ino: s.ino, mtimeMs: s.mtime * 1000,
      uid: s.uid, gid: s.gid, dev: s.id
    } as FsStats);
  }

  lstat(path: string): Promise<FsStats> { return this.stat(path); }  // 沙箱无 symlink 语义
  chmod(_p: string, _m: number): Promise<void> { return Promise.resolve(); }
}

interface FsStats {
  type: 'file' | 'dir'; mode: number; size: number; ino: number;
  mtimeMs: number; uid: number; gid: number; dev: number;
}

class MaziHarmony {
  /** 把 fs 适配器一次性交给 H5 */
  getFs(): ESObject { return this.fsImpl as ESObject; }

  /** H5 侧 git.clone/push 时用到的 onAuth 回调，Token 由原生侧保管，不下发到页面 */
  getToken(): string { return '<从 preferences / 用户输入 取>'; }

  platform: string = 'harmonyos';
  toast(msg: string): void { /* ... */ }
  setSheetOpen(_b: boolean): void { /* ... */ }
  importFile(): Promise<string[]> { /* DocumentViewPicker */ return Promise.resolve([]); }
  saveText(_n: string, _t: string): Promise<void> { return Promise.resolve(); }
}
```

```html
<!-- index.html -->
<script src="./vendor/buffer.min.js"></script>       <!-- Buffer polyfill -->
<script>window.process = { platform: 'linux', env: {} };</script>
<script src="./vendor/isomorphic-git/index.umd.min.js"></script>
<script>
const git = window.git;
const http = window.GitHttp;   // isomorphic-git/http/web 的 UMD 全局名就叫 GitHttp

// 关键：把原生返回的 Promise 包成 fs.promises 形状
const native = window.MaziHarmony;
const fs = { promises: {
  readFile:  (p) => native.getFs().readFile(p),
  writeFile: (p, d) => native.getFs().writeFile(p, d),
  readdir:   (p) => native.getFs().readdir(p),
  mkdir:     (p) => native.getFs().mkdir(p),
  rmdir:     (p) => native.getFs().rmdir(p),
  unlink:    (p) => native.getFs().unlink(p),
  stat:      (p) => native.getFs().stat(p),
  lstat:     (p) => native.getFs().lstat(p),
}};

async function commitAndPush() {
  await git.add({ fs, dir: '/', filepath: 'novel.txt' });
  await git.commit({ fs, dir: '/', author: { name: 'Mazi', email: 'a@b.c' }, message: 'update' });
  await git.push({
    fs, http, dir: '/',
    remote: 'origin',
    onAuth: () => ({ username: native.getToken(), password: '' }),  // GitHub/Gitee PAT
  });
}
</script>
```

**【推理 · 置信度 中】性能提醒**：每个 `fs` 调用都是一次 JS→ArkTS 跨语言往返 + 一次同步文件 IO。isomorphic-git 做一次 `status`/`commit` 会产生**成百上千次** fs 调用。**这在手机上会很慢**（毫秒级 × 上千次 = 秒级），但**功能上可行**。缓解手段：
- 让仓库尽量小（只 track 少量 .txt）
- 把 `getFs()` 返回的对象**缓存**在 JS 侧（不要每次调用都跨桥取）
- 必要时给 `readFile`/`writeFile` 加原生侧内存缓存层

---

## 2.3 能否通过原生桥代理 git 命令 / 执行外部可执行程序？

**答：不能。三条路全部堵死。**

### 2.3.1 `@ohos.childProcess` / `childProcessManager`

**【官方 · 置信度高】它不是"跑外部程序"，而是"跑你自己 App 的第二个 ArkTS 实例"，而且手机不支持。**

```ts
import { childProcessManager } from '@kit.AbilityKit';

childProcessManager.startChildProcess(
  './ets/process/DemoProcess.ets',                     // ★ 只能是你自己 entry 模块里的 .ets 源文件
  childProcessManager.StartMode.SELF_FORK              // SELF_FORK=0 | APP_SPAWN_FORK=1
).then((pid: number) => { /* ... */ });
```

官方"约束限制"原文：

> - 通过本模块中接口创建的子进程有如下限制：
>   - 创建的子进程**不支持创建 UI 界面**。
>   - 创建的子进程**不支持依赖 Context 的 API 调用**（包括 Context 模块自身 API 及将 Context 实例作为入参的 API）。
>   - 创建的子进程内**不支持再次创建子进程**。
> - 通过本模块中定义的创建子进程的接口和 native_child_process.h 中定义的创建子进程的接口启动的子进程总数最大为 512 个

以及最关键的一行：

> **设备行为差异**：该接口在 **Tablet、PC/2in1** 中可正常调用，**在其他设备类型中返回 16000061 错误码**。

`srcEntry` 参数说明也明确："子进程源文件路径，**只支持源文件放在 entry 类型的模块中**，以 src/main 为根目录。例如 ... `srcEntry` 为 `./ets/process/DemoProcess.ets`"。

来源：[@ohos.app.ability.childProcessManager](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-ability-kit/js-apis-app-ability-childProcessManager.md) 第 19-26、47-67 行
（华为页：[@ohos.app.ability.childProcessManager](https://developer.huawei.com/consumer/cn/doc/doccenter-references/api/js-apis-app-ability-childProcessManager)）

**结论：**
- ❌ **没有 `exec()` 语义** —— 参数是"ArkTS 源文件路径"，不是"可执行文件路径"
- ❌ **手机（Phone）不支持** —— 直接返回 `16000061 Operation not supported`
- ❌ 子进程里连 `Context` 都不能用 → 拿不到 `filesDir`，等于什么都干不了

### 2.3.2 应用沙箱能否 exec 自带二进制？

**【官方 FAQ 存在，但正文未能取到 · 置信度 中】**

华为开发者 FAQ 里有这个条目：**"HarmonyOS 是否限制 App 进程 fork 子进程，是否允许 app 里自带的可执行文件运行（fork+exec）执行，并通过 ptrace 方式读取自身进程？"**
- 华为页：https://developer.huawei.com/consumer/cn/doc/harmonyOS-faqs/faqs-ability-112 （JS 渲染，**正文未取到**）
- 开发者问答同题：https://developer.huawei.com/consumer/cn/forum/topic/0207212147070103301

**【社区 · 置信度 中】** 从社区转述与问答标题可推断官方立场是**限制且趋严**（"这种方式以后是否会限制并禁止"）。社区文章《应用执行 Shell 指令及其基本原理》等讨论了绕行手段（如通过 `childProcessManager` 在支持的设备上跑自有代码，或 NAPI 内直接调用系统接口），但**没有任何一条支持"把 git 二进制打进 HAP 然后 exec"**。

**【推理 · 置信度 中高】为什么不可行（工程性理由，比政策理由更硬）：**

1. **HAP 里天然没有"可执行文件"的存放语义。** `context.filesDir` 是数据目录，`/data/storage/el2/...` 挂载参数通常带 `noexec`；`libs/` 下的 `.so` 是给 `dlopen` 用的，不是给 `execve` 用的。
2. **git 是动态链接 ELF**，依赖 glibc/musl、`libpcre2`、`libz`、`libcurl`/OpenSSL、`/usr/libexec/git-core/*` 一大票子命令和 shell 脚本。**即使能 exec 主二进制，`git push` 会去调 `git-remote-https` 子进程和 `sh`，链条立刻断。**
3. 因此业界（含 GitUI 的鸿蒙 PC 移植）都是走 **libgit2/gitoxide 库级链接**，而不是 exec 二进制。

**✅ 判定：把 git 二进制打进应用并 exec —— ❌ 不可行。**

### 2.3.3 能否交叉编译 git/libgit2 到 OHOS？

**【社区+官方仓库 · 置信度 中高】**

- ✅ **交叉编译 libgit2 到 `aarch64-unknown-linux-ohos` 已被验证可行**（`build_in_harmonyos` archives 里有 `libgit2/1.9.4`、`1.9.6`；GitUI 完整跑通）
- ⚠️ 必须解决：OpenSSL vendored 交叉编译（设 `CC_aarch64_unknown_linux_ohos`）、libgit2 的 owner 校验误报（沙箱 UID 模型）、musl 与 Rust std 的符号重复
- ⚠️ **产出是 PC/2in1 的 CLI 工具链；没有现成的"手机 App 用 NAPI 封装"**

**✅ 判定：交叉编译本身——可行；封装成手机 App 可用的 NAPI 库——技术上可行但需自研，无现成包。**

---

## 2.4 认证方式

### 2.4.1 HTTPS + Personal Access Token

**【官方 · 置信度高】`@kit.NetworkKit` 的 `http` 支持任意自定义 header，含 `Authorization`。**

```ts
import { http } from '@kit.NetworkKit';

const httpRequest = http.createHttp();
const resp = await httpRequest.request('https://api.github.com/repos/u/r/contents/a.txt', {
  method: http.RequestMethod.PUT,
  header: {
    'Authorization': 'Bearer ghp_xxxxxxxxxxxxxxxx',   // 或 'Basic ' + base64(user:token)
    'Accept': 'application/vnd.github+json',
    'Content-Type': 'application/json',
    'User-Agent': 'MaziHarmony'
  },
  extraData: JSON.stringify({ message: 'update a.txt', content: '<base64>' }),
  expectDataType: http.HttpDataType.STRING,
  connectTimeout: 30000,
  readTimeout: 60000
});
console.info('code:', resp.responseCode, 'body:', resp.result);
httpRequest.destroy();
```

官方 `HttpRequestOptions.header` 字段说明原话：

> HTTP 请求头字段。当请求方式为 "POST" "PUT" "DELETE" 或者 "" 时，默认 `{'content-Type': 'application/json'}`，否则默认 `{'content-Type': 'application/x-www-form-urlencoded'}`。**如果 head 中包含 number 类型的字段，最大支持 int64 的整数。header 字段支持 JSON 格式和 `Record<string, string>` 格式输入。**

`extraData` 支持 `string | Object | ArrayBuffer`。响应 `result` 支持 `string | Object | ArrayBuffer`。

来源：[js-apis-http.md#httprequestoptions](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-network-kit/js-apis-http.md) 第 1257-1317、1372-1375 行

**⚠️ API 12 vs API 26 差异（都很关键）：**

| 项 | API 12 | API 26 |
|---|---|---|
| 响应体默认上限 | **5 MB** | **50 MB**（自 API 23 起）；`maxLimit` 可设到 **100 MB** |
| `maxLimit` | API 11+，默认 5 MB，最大 100 MB | 同 |
| `customMethod`（WebDAV 扩展方法） | ❌ | ✅ **API 23+**，最大 128 字符 |
| `maxRedirects` | ❌ | ✅ API 23+，默认 30 |
| `sniHostName` | ❌ | ✅ API 23+ |
| `reuseConnections` / `inactivityMs` | ❌ | ✅ **26.0.0** |
| `certificatePinning` | ✅ API 12+ | ✅ |
| `PATCH` 方法 | ❌ | ✅ **26.0.0** |
| `caData` | ❌ | ✅ API 20+ |

**"`customMethod` 支持 WebDAV 扩展协议"这一条（API 23+）直接给了你一个折中方案：可以把作品同步到坚果云/Nextcloud 之类的 WebDAV 网盘。**（你的设备是 API 26，可用。）

**⚠️ 关于 `Authorization` header 的限制：【查不到】官方文档没有对 `Authorization` 做任何特殊限制说明。** 社区一般反馈可直接使用。**GitHub 的 PAT 认证推荐 `Authorization: Bearer <token>`；Gitee 支持 `Authorization: token <token>` 或 `Basic base64(user:token)`。**

**⚠️ 安全提示（【推理 · 置信度 高】）**：Token **不要下发到 H5 页面**。若用 isomorphic-git 的 `onAuth`，让 `onAuth` 向原生桥请求 Token（原生侧同步返回即可），而不是把 Token 写进 HTML/JS。

### 2.4.2 SSH 是否可行？

**【社区（OpenHarmony TPC 官方孵化项目）· 置信度 高】有可用的 SSH NAPI 库，且我已在 ohpm registry 实测到包存在。**

```bash
ohpm install @ohos/libssh
```
```json
// ohpm registry 实测返回
{ "name": "@ohos/libssh", "version": "1.0.4", "org": "ohos",
  "authorName": "ohos_tpc", "license": "LGPL-2.1", "downloads": 483, "fileSize": 3422944 }
```

- **底层**：libssh-0.11.1（C++），通过 NAPI 封装
- **定位**：同时支持 **SFTP 服务端** 和 **SSH 客户端**
- **验证环境**：官方 README 写 "DevEco Studio 5.0.3 Beta2 - 5.0.9.200, SDK: API12"（**即 API 12 可用**）
- **源码**：[gitee.com/openharmony-tpc-incubate/ohos_ssh](https://gitee.com/openharmony-tpc-incubate/ohos_ssh)

关键接口：

```ts
import { libssh, SSH_KEYTYPES } from '@ohos/libssh';

const ssh = new libssh();
ssh.keygen(privPath, pubPath, SSH_KEYTYPES.SSH_KEYTYPE_ECDSA);  // 生成密钥对
ssh.setUser(username, password);                                 // 设置用户名/密码
ssh.startSSHClient(sshServerIP, port, privPath, (type: number) => {
  // type === 0 表示连接成功
});
await ssh.executeSSHComm('ls -al');                               // Promise<string>，执行远端命令
ssh.stopSSHClient();
```

来源：[ohos_ssh README](https://gitee.com/openharmony-tpc-incubate/ohos_ssh)（镜像：[gitcode](https://gitcode.com/openharmony-tpc/openharmony_tpc_samples/tree/master/ohos_ssh)）

**【推理 · 置信度 中高】判定：**

- ✅ **"建立一个 SSH 连接并执行远端命令"——可行**（`@ohos/libssh` 直接给）
- ❌ **"用 SSH 做 git 传输"——不现实**。Git 的 `ssh://` 传输需要在同一条 SSH 会话里双向流式跑 `git-upload-pack` / `git-receive-pack` 的 pkt-line 协议（含 side-band 多路复用）。`@ohos/libssh` 暴露的是 `executeSSHComm(): Promise<string>`（一次性取回全部输出）和 `createShell()`（伪终端），**没有暴露"双工管道流"API**。用 `createShell()` 手搓 pkt-line 理论上可能，但**要自己实现整个 git 传输协议**，工作量超过直接用 isomorphic-git。
- ✅ **更实用的 SSH 用途**：把 SSH 当作**文件同步通道**（SFTP），而不是 Git 通道。`@ohos/libssh` 支持 SFTP 服务端，客户端方向可通过 `executeSSHComm` 调远端 `sftp`。

- ❌ **`@ohos.net.socket` 的 `TCPSocket` 能否自己实现 SSH？** 技术上"能"（`tcp.connect(options)` / `tcp.send(string | ArrayBuffer)` / `tcp.on('message', ...)` 都齐全，见 [js-apis-socket.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-network-kit/js-apis-socket.md) 第 1830-2906 行），**但要在 ArkTS 里从零实现 SSH-2 协议（密钥交换、curve25519、AES-CTR、HMAC-SHA2-256、公钥认证）属于不切实际的工作量**。有现成的 `@ohos/libssh` 就没必要。

---

## 2.5 推荐方案（"码字 + 同步到 Git 仓库"）

### 结论排序

| 排名 | 方案 | 可行性 | 工作量 | 说明 |
|---|---|---|---|---|
| 🥇 | **A. ArkWeb + isomorphic-git + 原生 fs 适配器** | ✅ **可行** | 中 | 唯一能真正"提交到 git"的路线 |
| 🥈 | **B. GitHub / Gitee REST API 单文件推送** | ✅ **可行** | 极小 | 折中方案，覆盖 90% 需求 |
| 🥉 | **C. WebDAV 网盘中转（API 23+ `customMethod`）** | ✅ **可行** | 小 | 非 git，但可靠 |
| 4 | D. NAPI 封装 libgit2 | ⚠️ 有条件可行 | **极大** | 无现成包，需自研 + 踩 OpenSSL/owner 坑 |
| 5 | E. SSH + git 传输 | ❌ 不现实 | 极大 | libssh 无流式双工 API |
| 6 | F. exec git 二进制 | ❌ **不可行** | — | 沙箱禁止；childProcessManager 手机不支持 |

### 🥇 方案 A：ArkWeb + isomorphic-git（推荐）

**架构：**

```
┌─────────────────── ArkWeb (Chromium) ───────────────────┐
│  index.html                                              │
│   ├─ buffer.min.js            (Buffer polyfill, ~40 KB)  │
│   ├─ isomorphic-git/index.umd.min.js       (265 KB)      │
│   ├─ isomorphic-git/http/web/index.umd.js  (GitHttp)     │
│   │                                                      │
│   ├─ fs   = { promises: { ... } }  ──┐                   │
│   └─ http = GitHttp (原生 fetch)      │                   │
└───────────────────────────────────────┼──────────────────┘
                                        │ javaScriptProxy
                                        │ (methodList, 返回 Promise)
┌───────────────────────────────────────▼──────────────────┐
│  ArkTS  MaziHarmony                                       │
│   ├─ getFs()   → 文件门面（fileIo.*，根目录 context.filesDir/repo）│
│   ├─ getToken()→ PAT（从 preferences 取，不下发到页面）    │
│   ├─ saveText / importFile / toast / setSheetOpen / platform │
│   └─ 目录/文件授权：fileShare.persistPermission + activatePermission│
└──────────────────────────────────────────────────────────┘
```

**落地步骤：**

1. **H5 侧引入 UMD**（下载 3 个文件放 `src/main/resources/rawfile/vendor/`，随 `$rawfile()` 离线加载）：
   - `https://unpkg.com/buffer/index.js`
   - `https://unpkg.com/isomorphic-git/index.umd.min.js` → 全局 `git`
   - `https://unpkg.com/isomorphic-git/http/web/index.umd.js` → 全局 **`GitHttp`**（官方特别说明：*"the global var is called `GitHttp` not `http`"*）
2. **注入 `process` 空壳**：`window.process = { platform: 'linux', env: {} }`
3. **`javaScriptProxy` 注册**（`methodList` 放有返回值的方法）：

```ts
Web({ src: $rawfile('index.html'), controller: this.controller })
  .javaScriptAccess(true)
  .javaScriptProxy({
    object: this.maziHarmony,
    name: 'MaziHarmony',
    methodList: ['getFs', 'getToken', 'saveText', 'importFile', 'getRepoDir'],
    asyncMethodList: ['toast', 'setSheetOpen'],
    controller: this.controller,
    permission: '{"javascriptProxyPermission":{"urlPermissionList":' +
      '[{"scheme":"resource","host":"rawfile","port":"","path":""}]}}'
  })
```

> ⚠️ `permission` 的官方说明：**"JavaScriptProxy 的 permission 参数支持 resource/http/https 协议，不支持 file 协议。"** 你用 `$rawfile('index.html')`，所以必须放行 `scheme: "resource", host: "rawfile"`，否则桥调不通。（[web-i.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-arkweb/arkts-basic-components-web-i.md) 第 571 行）

4. **仓库必须落在应用沙箱**（`context.filesDir + '/repo'`），**不要试图放在公共目录** —— 沙箱零权限且 `mkdir/unlink/rmdir/rename` 全支持，公共目录受授权限制且已有已知写入 bug。
5. **"同步到公共目录"作为独立功能**：写完后用 `saveText`（DocumentViewPicker 保存框）导出到用户可见位置；或用方案 B 推 GitHub。

**风险与缓解：**

| 风险 | 缓解 |
|---|---|
| `crypto.subtle` 不可用 | isomorphic-git 内置 `sha.js` 纯 JS 兜底，自动回落；无需处理 |
| `Buffer` 未定义 | 引入 `buffer` polyfill（必须） |
| JSBridge 调用次数过多导致慢 | 仓库保持小；在原生侧加一层内存/文件缓存 |
| `ArrayBuffer` 过桥大小未知 | 先小数据验证；大文件用 base64 兜底 |
| 同步方法阻塞渲染线程 | 全部走返回 Promise 的方法；绝不在 `methodList` 里做重 IO |

### 🥈 方案 B（折中，强烈建议同时做）：GitHub / Gitee REST API

**只做"推送单个/多个文本文件"，不碰 git 对象模型。工作量 1~2 天。**

```ts
import { http } from '@kit.NetworkKit';

const API = 'https://api.github.com';

class GitHubSync {
  constructor(private token: string, private owner: string, private repo: string,
              private branch = 'main') {}

  /** 读文件（同时拿到 sha，更新时必须回传） */
  async getFile(path: string): Promise<{ content: string, sha: string } | null> {
    const req = http.createHttp();
    const r = await req.request(`${API}/repos/${this.owner}/${this.repo}/contents/${path}?ref=${this.branch}`, {
      method: http.RequestMethod.GET,
      header: { 'Authorization': `Bearer ${this.token}`,
                'Accept': 'application/vnd.github+json',
                'User-Agent': 'MaziHarmony' }
    });
    req.destroy();
    if (r.responseCode === 404) return null;
    if (r.responseCode !== 200) throw new Error(`GET ${r.responseCode}: ${r.result}`);
    const j = JSON.parse(r.result as string);
    // GitHub 返回的 base64 带换行，需去掉
    const b64 = (j.content as string).replace(/\n/g, '');
    return { content: buffer.from(b64, 'base64').toString('utf8'), sha: j.sha };
  }

  /** 新建或更新文件（PUT /contents） */
  async putFile(path: string, text: string, message: string): Promise<void> {
    const existing = await this.getFile(path);
    const body: Record<string, string> = {
      message,
      content: buffer.from(text, 'utf8').toString('base64'),
      branch: this.branch
    };
    if (existing) body.sha = existing.sha;   // 更新必须带 sha

    const req = http.createHttp();
    const r = await req.request(`${API}/repos/${this.owner}/${this.repo}/contents/${path}`, {
      method: http.RequestMethod.PUT,
      header: { 'Authorization': `Bearer ${this.token}`,
                'Accept': 'application/vnd.github+json',
                'Content-Type': 'application/json',
                'User-Agent': 'MaziHarmony' },
      extraData: JSON.stringify(body),
      connectTimeout: 30000, readTimeout: 60000
    });
    req.destroy();
    if (r.responseCode !== 200 && r.responseCode !== 201) {
      throw new Error(`PUT ${r.responseCode}: ${r.result}`);
    }
  }

  /** 批量：先取 tree，再逐个 diff 上传（最省流量的增量同步） */
  async syncAll(files: Map<string, string>): Promise<string[]> {
    const changed: string[] = [];
    for (const [path, text] of files) {
      const remote = await this.getFile(path);
      if (!remote || remote.content !== text) {
        await this.putFile(path, text, `sync ${path}`);
        changed.push(path);
      }
    }
    return changed;
  }
}
```

**Gitee 差异**：API base 换成 `https://gitee.com/api/v5`，认证用 `Authorization: token <PAT>` 或 query/body 里的 `access_token`，路径为 `/repos/{owner}/{repo}/contents/{path}`。**具体字段请以 Gitee 官方文档为准（我未逐一核对 Gitee 的响应结构）。**

**优点**：零依赖、体积为 0、每篇文章一次 PUT 就"提交"了（GitHub 侧会生成真实的 commit，带 author/message）；
**缺点**：不是真正的本地 git（没有本地历史/diff/branch/merge），需要网络才能"提交"。

### 🥉 方案 C：WebDAV 网盘中转（API 23+）

```ts
const req = http.createHttp();
await req.request('https://dav.jianguoyun.com/dav/mazi/novel.txt', {
  method: http.RequestMethod.PUT,
  customMethod: 'PROPFIND',   // ★ API 23+，支持 WebDAV 扩展方法
  header: {
    'Authorization': 'Basic ' + buffer.from(`${user}:${appPassword}`).toString('base64'),
    'Depth': '1'
  },
  extraData: text
});
```
官方对 `customMethod` 的原话：

> 支持自定义请求方法，例如实现 WebDAV 扩展协议……当 customMethod 符合 WebDAV 扩展协议请求方式，但服务器不支持时，本次请求的服务器响应码通常为 405 或 501。

来源：[js-apis-http.md](https://gitee.com/openharmony/docs/blob/master/zh-cn/application-dev/reference/apis-network-kit/js-apis-http.md) 第 1292 行

**适合**：不想暴露 GitHub/Gitee Token 给最终用户、或用户已有网盘。**不适合**：需要真正的 git 历史。

---

## 2.6 问题二 可行性总表

| 子问题 | 判定 | 依据 |
|---|---|---|
| libgit2 有 OHOS 移植？ | ⚠️ **PC/2in1 命令行工具链有；手机 App 无现成包** | `build_in_harmonyos/archives/l/libgit2`、GitUI 移植文章；ohpm 实测无包 |
| libgit2 有 ohpm 包？ | ❌ **没有**（实测 registry） | ohpm openapi detail 接口 |
| `isomorphic-git` 能在 ArkWeb 跑？ | ✅ **有条件可行**（需 Buffer polyfill + process 空壳 + fs 适配器） | bundle 静态分析：`require(` 出现 0 次；官方 fs/http 适配器文档 |
| `isomorphic-git` 依赖哪些 Node 模块？ | `Buffer`（157 处，必需）、`process`（11 处，空壳即可）、`crypto`（1 处，有 `sha.js` 兜底）、`fetch`（15 处，Chromium 原生） | 同上（实测） |
| 只靠内存 + 自定义 fs/http 适配器？ | ✅ **可以**（但必须持久化，否则重启丢仓库） | 官方 "Implementing your own fs" |
| `simple-git` / `nodegit` | ❌ 需要 Node 运行时 / git 二进制 | 推理（高置信） |
| NAPI/C++ 集成 libgit2 先例（手机） | ❌ **未找到公开先例** | 检索结果 |
| `javaScriptProxy` 同步 or 异步 | **两者都支持**：`methodList`=同步（有返回值）、`asyncMethodList`=异步（无返回值） | web-attributes.md / web-i.md |
| `javaScriptProxy` 能否返回 Promise | ✅ **能**（官方"Promise 场景"章节） | web-in-page-app-function-invoking.md |
| `javaScriptProxy` 能否传 ArrayBuffer | ⚠️ **查不到官方确认**；`runJavaScriptExt` 和 `WebMessagePort` 确认支持 | WebMessageExt/WebviewController 文档 |
| 桥调用大小限制 | ❌ **查不到官方数值**（需真机压测） | — |
| exec 外部可执行程序 | ❌ **不可行** | childProcessManager 文档 + 沙箱推理 |
| `@ohos.childProcess` 是否仅系统应用 | ❌ **不是"仅系统应用"，而是"只能跑自有 ArkTS 代码 + 手机不支持"** | childProcessManager 文档 |
| 交叉编译 git/libgit2 到 OHOS | ⚠️ **libgit2 可行**（已验证）；git 本体 + exec 不可行 | GitUI 移植文章 |
| HTTPS + PAT（自定义 header） | ✅ **可行** | HttpRequestOptions.header |
| SSH 可行？ | ✅ **连接可行**（`@ohos/libssh`）；❌ **做 git 传输不现实** | ohos_ssh README + ohpm 实测 |
| **完整 git 客户端?** | ⚠️ **有条件可行**：isomorphic-git 路线 |
| **折中方案** | ✅ GitHub/Gitee REST API 单文件推送 / WebDAV（API 23+ `customMethod`） | http 文档 |

---

## 附录 A：关键 import 路径速查

```ts
// 文件与 picker
import { picker, fileIo, fileUri, fileShare, hash, Environment } from '@kit.CoreFileKit';
// picker.DocumentViewPicker / DocumentSelectOptions / DocumentSelectMode{FILE,FOLDER,MIXED}
// picker.DocumentSaveOptions / DocumentPickerMode{DEFAULT,DOWNLOAD} / MergeTypeMode
// fileIo.OpenMode / ListFileOptions / Filter / ReadOptions / WriteOptions / File / Stream
// fileUri.FileUri (path / name / getFullDirectoryUri / isRemoteUri) / getUriFromPath
// fileShare.persistPermission / activatePermission / deactivatePermission / revokePermission
//            / checkPersistentPermission / OperationMode{READ_MODE,WRITE_MODE}
//            / PolicyInfo{uri, operationMode} / PolicyErrorCode / PathPolicyInfo
// Environment.getUserDownloadDir / getUserDocumentDir / getUserDesktopDir  （仅 2in1）

// Web / JSBridge
import { webview } from '@kit.ArkWeb';
// webview.WebviewController  (javaScriptProxy / registerJavaScriptProxy /
//                             deleteJavaScriptRegister / runJavaScript /
//                             runJavaScriptExt / postMessage / createWebMessagePorts)
// webview.WebMessageExt     (setArrayBuffer / getArrayBuffer / setString / getString ...)
// webview.WebMessagePort    (postMessageEvent / onMessageEvent / postMessageEventExt / onMessageEventExt)

// 网络
import { http } from '@kit.NetworkKit';
// http.createHttp() / HttpRequestOptions{method, header, extraData, expectDataType,
//   connectTimeout, readTimeout, maxLimit, usingProxy, caPath, certificatePinning,
//   customMethod(23+), maxRedirects(23+), sniHostName(23+)}
// http.RequestMethod{GET,POST,PUT,DELETE,PATCH(26+),...} / HttpDataType / HttpResponse

import { socket } from '@kit.NetworkKit';
// socket.constructTCPSocketInstance() / constructTLSSocketInstance() / TCPSocketServer / LocalSocket

// 子进程（手机不可用）
import { childProcessManager, ChildProcess } from '@kit.AbilityKit';

// SSH（三方 ohpm 包）
import { libssh, SSH_KEYTYPES } from '@ohos/libssh';

// 通用
import { common } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { buffer } from '@kit.ArkTS';   // buffer.from / toString('base64')
```

## 附录 B：`module.json5` 权限清单（针对你的场景）

```json5
{
  "module": {
    "requestPermissions": [
      // 免弹窗（normal + system_grant），声明即生效 —— 持久化访问授权用
      { "name": "ohos.permission.FILE_ACCESS_PERSIST" },
      // 需要弹窗（normal + user_grant）—— 只在真的要读写公共 Download/Documents 时加
      { "name": "ohos.permission.READ_WRITE_DOWNLOAD_DIRECTORY" },
      { "name": "ohos.permission.READ_WRITE_DOCUMENTS_DIRECTORY" },
      // 网络
      { "name": "ohos.permission.INTERNET" }
    ],
    "metadata": [
      // API 24+：卸载重装后保留持久化授权
      { "name": "ohos.fileshare.supportPreservePersistentPermission" }
    ]
  }
}
```

## 附录 C：本次调研确认"查不到"的事项（请勿采信任何相反说法）

1. **华为官方从未公开 ArkWeb 支持的完整 Web API 白名单** → `crypto.subtle` 是否可用**需真机实测**（isomorphic-git 有兜底，不影响可用性）
2. **`javaScriptProxy` / `WebMessagePort` 的单次传输字节上限，官方无文档** → 需真机压测
3. **`javaScriptProxy` 传 `ArrayBuffer` 的官方确认** → 文档只覆盖 `string/number/boolean/Array/自定义对象/Function/Promise`；二进制请走 `runJavaScriptExt` 或 `WebMessagePort`
4. **`ohos.permission.FILE_ACCESS_PERSIST` 在每个具体设备上的实际可授性** → 我核对的是 OpenHarmony 权限定义源码；HarmonyOS NEXT 商业版可能另有裁剪，**建议真机实测一次 `persistPermission`**
5. **华为 FAQ "是否允许 app 里自带可执行文件运行" 的正文** → 页面 JS 渲染，未取到；仅"官方存在该 FAQ"可确认
6. **Gitee REST API 的精确字段结构** → 未逐一核对，请查 Gitee 官方文档
7. **AtomGit HarmonyOS 手机版 App 的技术实现（libgit2 vs REST）** → 未确认
8. **HarmonyOS 7 手机上 `SystemCapability.FileManagement.UserFileService.FolderSelection` 是否已开放** → 官方 phone-syscap-list 显示缺失，但社区博客暗示 API 26 有变化。**唯一可靠判据是真机上 `canIUse` 实测**

---

## 附录 D：给你的两条最务实建议

1. **"访问用户文件夹"这件事，在手机上不要和系统对抗。** 把仓库/工作区放在 `context.filesDir`（零权限、能递归、能建能删），把"用户可见"降级为 **导入/导出**（`DocumentViewPicker.save` + 批量授权一批 .txt）。这样你的 H5 侧永远面对一个"像本地磁盘一样自由"的目录，代码简单 10 倍。真要试目录授权，**第一件事是在真机上把 `canIUse('SystemCapability.FileManagement.UserFileService.FolderSelection')` 打出来**，别先写代码。

2. **"Git"这件事，分两层做。** 先做 **REST API 单文件推送**（1~2 天，立即可用，用户拿到的是真实 commit）；同时用 `isomorphic-git` + 原生 fs 适配器做**完整的本地仓库**（能 commit/diff/log，离线可用），推送仍走 `fetch`。**不要碰 libgit2 NAPI 和 exec 二进制** —— 前者是几周的坑（OpenSSL 交叉编译 + owner 校验），后者在手机上物理上不成立。
