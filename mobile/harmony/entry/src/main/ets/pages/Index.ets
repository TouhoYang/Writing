import { webview } from '@kit.ArkWeb';
import { fileIo as fs, picker, fileShare, fileUri } from '@kit.CoreFileKit';
import { common } from '@kit.AbilityKit';
import { promptAction } from '@kit.ArkUI';
import { util } from '@kit.ArkTS';
import preferences from '@ohos.data.preferences';
import http from '@ohos.net.http';
import hilog from '@ohos.hilog';
import { BusinessError } from '@kit.BasicServicesKit';

/** 日志域/标签:用 hdc shell hilog -T MaziGit 就能只抓这一路 */
const LOG_DOMAIN: number = 0x0A21;
const LOG_TAG: string = 'MaziGit';

/** 目录树里的一个条目 */
interface MaziEntry {
  name: string;
  path: string;      // 相对根目录的路径,如 "ch1/开头.txt"
  isDir: boolean;
  size: number;
  mtime: number;
}

/** 当前稿件文件夹信息 */
interface MaziFolderInfo {
  uri: string;
  name: string;
  restored: boolean;
}

/** pickFolder 的回传内容 */
interface MaziPickResult {
  uri: string;
  name: string;
  persisted: boolean;
  /** 所选文件夹是否为空(克隆需要一个空目录或新目录作为仓库根) */
  empty: boolean;
}

/** 目录列表回传内容 */
interface MaziDirData {
  path: string;
  entries: MaziEntry[];
}

/** 文件读取回传内容 */
interface MaziReadResult {
  path: string;
  text: string;
  encoding: string;
}

/** 文件写入回传内容 */
interface MaziWriteResult {
  path: string;
  bytes: number;
}

/** 重命名回传内容 */
interface MaziRenameResult {
  from: string;
  to: string;
}

/** 通用 ok/error 外壳 */
interface MaziOk<T> {
  ok: boolean;
  data: T;
}

/** 带 ok 的载荷(用于 folder 事件) */
interface MaziFolderEvent {
  ok: boolean;
  data: MaziFolderInfo | null;
}

/** gitHttp 的入参(网页用 JSON 字符串传进来) */
interface MaziHttpReq {
  url: string;
  method: string;
  headers: Record<string, string>;
  body?: string;        // base64,无 body 时省略
}

/** gitHttp 的出参 */
interface MaziHttpRes {
  status: number;
  statusText: string;
  headers: Record<string, string>;
  body: string;         // base64
}

/** 响应头暂存(ArkTS 不允许对 Record 做下标写入) */
interface HeaderPair {
  key: string;
  value: string;
}

/** 记住上次选定的文件夹,下次启动自动恢复授权 */
const PREF_STORE = 'mazi_folder';
const PREF_KEY_URI = 'rootUri';
const PREF_KEY_NAME = 'rootName';

/** 网页配置的持久化仓库。
 *  ArkWeb 的 localStorage 在部分机器上「关闭应用再进入」会丢失,
 *  而原生 preferences 实测可靠,所以网页的配置(设置/令牌/仓库/稿件)统一镜像到这里。 */
const PREF_CFG = 'mazi_config';

/**
 * 提供给网页的原生能力(网页通过 window.MaziHarmony 调用)
 * 结果统一通过 window.maziOn(名字, JSON) 异步回传
 */
class MaziBridge {
  private controller: webview.WebviewController;
  private context: common.UIAbilityContext;
  private sheetOpen: boolean = false;
  /** 用户选定的「稿件文件夹」URI;为空表示还没选 */
  private rootUri: string = '';
  private rootName: string = '';
  /** Base64 编解码实例(方法是实例方法,不是静态方法) */
  private b64: util.Base64Helper = new util.Base64Helper();
  /** 最近一次原生文件操作失败的细节(诊断用:克隆失败到底卡在哪个路径/哪个 errno) */
  private lastErr: string = '';
  private fsOps: number = 0;

  constructor(controller: webview.WebviewController, context: common.UIAbilityContext) {
    this.controller = controller;
    this.context = context;
  }

  // ==================== 基础 ====================

  platform(): string {
    return 'harmony';
  }

  /**
   * 写日志。两路输出:
   *   1) hilog(便于 hilog -T MaziGit)
   *   2) 应用私有目录下的 mazi-git.log 文件 —— hilog 缓冲会被系统日志冲掉、
   *      而且实测有时抓不到,写文件最可靠,可以直接 pull 出来看。
   */
  log(msg: string): void {
    const text: string = (msg === undefined || msg === null) ? '' : msg;
    try {
      hilog.info(LOG_DOMAIN, LOG_TAG, '%{public}s', text);
    } catch (e) {
      // 忽略
    }
    try {
      const p: string = this.context.filesDir + '/mazi-git.log';
      const f = fs.openSync(p, fs.OpenMode.READ_WRITE | fs.OpenMode.CREATE | fs.OpenMode.APPEND);
      let stamp: string = '';
      try {
        stamp = new Date().toISOString();
      } catch (e2) {
        stamp = '';
      }
      const line: string = '[' + stamp + '] ' + text + '\n';
      fs.writeSync(f.fd, this.utf8BufferOf(line));
      fs.closeSync(f);
    } catch (e3) {
      // 文件日志失败也不影响功能
    }
  }

  /** 清空日志文件 */
  clearLog(): void {
    try {
      const p: string = this.context.filesDir + '/mazi-git.log';
      if (this.exists(p)) {
        fs.unlinkSync(p);
      }
    } catch (e) {
      // 忽略
    }
  }

  /**
   * 读当前工作目录下的文件为 base64(二进制安全,供网页端打包 ZIP)。
   * 网页端只有文本读写桥,压缩需要原始字节,所以这里单独提供。
   *
   * 返回约定(必须让调用方区分「空文件」和「读取失败」):
   *   "b64:<base64>"  读取成功(空文件是 "b64:",解出来 0 字节)
   *   ""              读取失败 / 目标是目录
   * 之前空文件返回空串,网页端把它当成失败而跳过,
   * 导致「导出全部」在有 0 字节文件时导出为空。
   */
  gitReadB64(rel: string): string {
    try {
      const p: string = this.abs(rel);
      const st = fs.statSync(p);
      if (st.isDirectory()) {
        return '';
      }
      const len: number = st.size;
      if (len <= 0) {
        return 'b64:';                 // 空文件:成功,内容为 0 字节
      }
      const buf: ArrayBuffer = new ArrayBuffer(len);
      const f = fs.openSync(p, fs.OpenMode.READ_ONLY);
      const read: number = fs.readSync(f.fd, buf);
      fs.closeSync(f);
      const bytes: Uint8Array = new Uint8Array(buf.slice(0, read));
      this.fsOps++;
      return 'b64:' + this.b64.encodeToStringSync(bytes);
    } catch (e) {
      this.noteErr('gitReadB64', rel, e);
      return '';
    }
  }

  /**
   * 弹保存对话框,把 base64 内容写进去。
   * 这是平台允许的唯一写入用户可见目录的方式 —— save() 会给返回的那个 file URI
   * 授予写权限(实测:选文件夹拿到的目录 URI 连 listFile 都失败 13900002,
   *  同目录下新建其它文件也是 13900002,建子目录 13900012)。
   * 所以「批量导出」必须先打包成一个文件,再走这里保存。
   */
  saveB64(fileName: string, b64: string): void {
    const b64s: string = (b64 === undefined || b64 === null) ? '' : b64;
    this.log('[EXPORT] 保存 ' + fileName + ' , base64 长度 ' + b64s.length);
    let data: Uint8Array;
    if (b64s.length === 0) {
      /* 空内容必须单独处理:decodeSync('') 会抛异常,
         表现为「新建空 txt 保存到手机」时报错「内容解码失败」。 */
      data = new Uint8Array(0);
    } else {
      try {
        data = this.b64.decodeSync(b64s);
      } catch (e) {
        const be = e as BusinessError;
        this.log('[EXPORT] 解码失败 code=' + be.code + ' msg=' + be.message);
        this.fail('saveB64', '内容解码失败');
        return;
      }
    }
    const opts = new picker.DocumentSaveOptions();
    opts.newFileNames = [fileName];
    const saver = new picker.DocumentViewPicker(this.context);
    saver.save(opts).then((uris: Array<string>) => {
      if (uris === undefined || uris === null || uris.length === 0) {
        this.log('[EXPORT] 用户取消');
        this.fail('saveB64', '已取消');
        return;
      }
      const u: string = uris[0];
      try {
        const out = fs.openSync(u, fs.OpenMode.READ_WRITE | fs.OpenMode.TRUNC);
        if (data.length > 0) {
          fs.writeSync(out.fd, data.buffer);
        }
        fs.closeSync(out);
        this.log('[EXPORT] 写入成功 → ' + u + ' (' + data.length + ' 字节)');
        this.okData('saveB64', u);
      } catch (e) {
        const be = e as BusinessError;
        this.log('[EXPORT] 写入失败 code=' + be.code + ' ' + be.message);
        this.fail('saveB64', '写入失败: ' + be.message);
      }
    }).catch((err: BusinessError) => {
      const code: number = (err !== undefined) ? err.code : -1;
      this.log('[EXPORT] 选择器异常 code=' + code);
      this.fail('saveB64', code === 13900042 ? '已取消' : ('保存失败 code=' + code));
    });
  }

  /** 记录一次原生文件操作失败(供网页诊断) */
  private noteErr(op: string, path: string, e: Object): void {
    let msg: string = '';
    try {
      const be = e as BusinessError;
      msg = 'code=' + be.code + ' msg=' + be.message;
    } catch (x) {
      msg = String(e);
    }
    this.fsOps++;
    this.lastErr = op + ' 失败 @ ' + path + '  (' + msg + ')';
    this.log('[FS-FAIL] ' + this.lastErr);
  }

  /** 诊断:最近一次失败 + 调用次数 */
  lastNativeErr(): string {
    const s: string = this.lastErr;
    this.lastErr = '';
    return 'root=' + this.rootUri + ' ops=' + this.fsOps + '  ' + (s.length > 0 ? s : '(无失败)');
  }

  /**
   * 用同一个 POST 请求体,依次尝试几种「把字节体交给 @ohos.net.http」的方式,
   * 每种都把状态码或错误码写进日志 —— 一次就能看出哪种可行,不用来回试。
   */
  testHttpPost(url: string, bodyB64: string): void {
    const bridge = this;
    if (url === undefined || url === null || url.length === 0) {
      this.log('[POST-TEST] 需要仓库地址');
      return;
    }
    const body: Uint8Array = (bodyB64 === undefined || bodyB64 === null || bodyB64.length === 0)
      ? new Uint8Array(0) : this.b64.decodeSync(bodyB64);
    const ab: ArrayBuffer = new ArrayBuffer(body.length);
    const view: Uint8Array = new Uint8Array(ab);
    for (let i = 0; i < body.length; i++) {
      view[i] = body[i];
    }
    const cnt: number = 3;
    for (let v = 0; v < cnt; v++) {
      const h: Record<string, string> = {
        'content-type': 'application/x-git-upload-pack-request',
        'accept': 'application/x-git-upload-pack-result'
      };
      if (v === 1) {
        h['content-length'] = String(body.length);
      }
      let payload: string | ArrayBuffer = ab;
      let label: string = 'ArrayBuffer';
      if (v === 0) {
        payload = ab;
        label = 'ArrayBuffer 无 content-length';
      } else if (v === 1) {
        payload = ab;
        label = 'ArrayBuffer + 手动 content-length';
      } else {
        let s: string = '';
        for (let i = 0; i < body.length; i++) {
          s += String.fromCharCode(body[i]);
        }
        payload = s;
        label = '字符串(每字节一个字符)';
      }
      const opts: http.HttpRequestOptions = {
        method: http.RequestMethod.POST,
        header: h,
        extraData: payload,
        connectTimeout: 20000,
        readTimeout: 30000,
        maxLimit: 10 * 1024 * 1024,
        expectDataType: http.HttpDataType.ARRAY_BUFFER
      };
      const req = http.createHttp();
      req.request(url, opts, (err: BusinessError, resp: http.HttpResponse) => {
        try {
          if (err !== undefined && err !== null && err.code !== 0) {
            bridge.log('[POST-TEST] v' + v + ' ' + label + ' -> 错误 code=' + err.code + ' ' + err.message);
          } else {
            const n: number = (resp.result instanceof ArrayBuffer)
              ? (resp.result as ArrayBuffer).byteLength : -1;
            bridge.log('[POST-TEST] v' + v + ' ' + label + ' -> HTTP ' + resp.responseCode + ' len=' + n);
          }
        } catch (e) {
          bridge.log('[POST-TEST] v' + v + ' 回调异常');
        } finally {
          req.destroy();
        }
      });
    }
  }

  /**
   * 把工作根目录设为应用私有目录 (context.filesDir)。
   *
   * 注意:必须同时清掉 preferences 里存的「上次选定的文件夹」。
   * 否则 onPageEnd 的 restoreFolder() 会把那个旧 URI 恢复回来并覆盖这里的设置
   * (它有 await,所以会晚于 useAppDir 写入),导致工作目录变成
   * file://docs/storage/Users/currentUser/... ,于是 git 找不到 .git,
   * 表现为「状态和提交历史出不来」。
   */
  useAppDir(): void {
    try {
      const dir: string = this.context.filesDir;
      this.rootUri = dir;
      this.rootName = '应用目录';
      // 清掉可能残留的用户文件夹记录,避免被 restoreFolder 覆盖
      try {
        const pref = preferences.getPreferencesSync(this.context, { name: PREF_STORE });
        pref.deleteSync(PREF_KEY_URI);
        pref.deleteSync(PREF_KEY_NAME);
        pref.flush();
      } catch (e) {
        // 清不掉也不影响本次会话
      }
      let probe: string = 'filesDir=' + dir;
      try {
        const names: string[] = fs.listFileSync(dir);
        probe += ' | listFile OK 条目数=' + names.length;
      } catch (e) {
        const be = e as BusinessError;
        probe += ' | listFile 失败 code=' + be.code;
      }
      this.lastErr = 'useAppDir 取证: ' + probe;
      this.okData('useAppDir', JSON.stringify({ uri: dir, name: this.rootName, probe: probe }));
    } catch (e) {
      const be2 = e as BusinessError;
      this.fail('useAppDir', '取应用目录失败 code=' + be2.code);
    }
  }

  /**
   * 逐个测试 fileIo 对当前选中 URI 的能力,定位究竟哪一步被系统拒绝。
   * 背景:同一 URI 上 statSync 成功但 listFileSync/accessSync 报 13900002,
   * 自相矛盾,需要逐项取证才能确定是「URI 格式」还是「权限」问题。
   */
  probeUri(): string {
    const L: string[] = [];
    const uri: string = this.rootUri;
    if (uri.length === 0) {
      return '(未选定文件夹)';
    }
    L.push(' URI=' + uri);

    // 1) 目录自身能否 stat
    try {
      const st = fs.statSync(uri);
      L.push(' stat(目录) = OK  isDir=' + st.isDirectory());
    } catch (e) {
      const be = e as BusinessError;
      L.push(' stat(目录) = 失败 code=' + be.code);
    }

    // 2) 目录自身能否 open(只读)
    try {
      const f = fs.openSync(uri, fs.OpenMode.READ_ONLY);
      fs.closeSync(f);
      L.push(' open(目录,READ_ONLY) = OK');
    } catch (e) {
      const be = e as BusinessError;
      L.push(' open(目录,READ_ONLY) = 失败 code=' + be.code + ' ' + be.message);
    }

    // 3) 新建文件:带 '/' 的完整 URI + CREATE
    try {
      const p = uri + '/__p1__.txt';
      const f = fs.openSync(p, fs.OpenMode.READ_WRITE | fs.OpenMode.CREATE | fs.OpenMode.TRUNC);
      fs.writeSync(f.fd, 'ok');
      fs.closeSync(f);
      L.push(' open(uri/子文件, CREATE) = OK   ← 说明这种写法本来可行');
      try { fs.unlinkSync(p); } catch (e) {}
    } catch (e) {
      const be2 = e as BusinessError;
      L.push(' open(uri/子文件, CREATE) = 失败 code=' + be2.code + ' ' + be2.message);
    }

    // 4) 不带 TRUNC 的 CREATE
    try {
      const p = uri + '/__p2__.txt';
      const f = fs.openSync(p, fs.OpenMode.READ_WRITE | fs.OpenMode.CREATE);
      fs.closeSync(f);
      L.push(' open(uri/子文件, CREATE 无 TRUNC) = OK');
      try { fs.unlinkSync(p); } catch (e) {}
    } catch (e) {
      const be3 = e as BusinessError;
      L.push(' open(uri/子文件, CREATE 无 TRUNC) = 失败 code=' + be3.code);
    }

    // 5) 创建子目录
    try {
      const d = uri + '/__pdir__';
      fs.mkdirSync(d, true);
      L.push(' mkdir(uri/子目录) = OK');
      try { fs.rmdirSync(d); } catch (e) {}
    } catch (e) {
      const be4 = e as BusinessError;
      L.push(' mkdir(uri/子目录) = 失败 code=' + be4.code + ' ' + be4.message);
    }

    // 6) listFileExt 是否比 listFile 更宽
    try {
      const n: string[] = fs.listFileExtSync(uri);
      L.push(' listFileExt(目录) = OK 条目数=' + n.length);
    } catch (e) {
      const be5 = e as BusinessError;
      L.push(' listFileExt(目录) = 失败 code=' + be5.code);
    }

    // 7) 用 fileUri 重新构造一次 URI,看是否等价
    try {
      const again: string = fileUri.getUriFromPath(uri);
      L.push(' fileUri.getUriFromPath(uri) = ' + again);
    } catch (e) {
      const be6 = e as BusinessError;
      L.push(' fileUri.getUriFromPath 失败 code=' + be6.code);
    }

    return L.join('\n');
  }

  toast(msg: string): void {
    promptAction.showToast({ message: msg, duration: 2000 });
  }

  setSheetOpen(open: boolean): void {
    this.sheetOpen = open;
  }

  isSheetOpen(): boolean {
    return this.sheetOpen;
  }

  /** 回调网页:window.maziOn(name, data) */
  private emit(name: string, payload: string): void {
    try {
      this.controller.runJavaScript('window.maziOn && window.maziOn(' + JSON.stringify(name) + ',' + payload + ')');
    } catch (e) {
      // 网页已销毁,忽略
    }
  }

  /** 成功:{"ok":true,"data":{...}} */
  private okData<T>(name: string, data: T): void {
    let text: string = 'null';
    try {
      text = JSON.stringify(data);
    } catch (e) {
      text = 'null';
    }
    this.emit(name, '{"ok":true,"data":' + text + '}');
  }

  /** 成功但无数据 */
  private okEmpty(name: string): void {
    this.emit(name, '{"ok":true,"data":null}');
  }

  /** 失败:{"ok":false,"error":"..."} */
  private fail(name: string, msg: string): void {
    this.emit(name, '{"ok":false,"error":' + JSON.stringify(msg) + '}');
  }

  /** 把相对路径拼成 fs 能用的绝对 URI */
  private abs(rel: string): string {
    let base: string = this.rootUri;
    while (base.length > 1 && base.charAt(base.length - 1) === '/') {
      base = base.substring(0, base.length - 1);
    }
    let p: string = (rel === undefined || rel === null) ? '' : rel;
    while (p.length > 0 && p.charAt(0) === '/') {
      p = p.substring(1);
    }
    if (p.length === 0) {
      return base;
    }
    return base + '/' + p;
  }

  /** 从 URI 里取最后一段作为显示名 */
  private baseName(uri: string): string {
    let s: string = uri;
    while (s.length > 0 && s.charAt(s.length - 1) === '/') {
      s = s.substring(0, s.length - 1);
    }
    const i: number = s.lastIndexOf('/');
    let n: string = (i >= 0) ? s.substring(i + 1) : s;
    try {
      n = decodeURIComponent(n);
    } catch (e) {
      // 解码失败就用原样
    }
    return n.length > 0 ? n : '稿件文件夹';
  }

  // ==================== 文件夹选择与授权 ====================

  /**
   * 恢复上次选定的文件夹。
   *
   * 现在**不再使用**:应用的工作目录固定为私有目录(用户目录拿不到建目录权限)。
   * 保留此方法但直接跳过恢复 —— 否则它会把持久化里的旧用户文件夹 URI 恢复回来,
   * 覆盖 useAppDir() 设好的私有目录,导致 git 找不到 .git。
   */
  async restoreFolder(): Promise<void> {
    this.log('[FOLDER] restoreFolder 已停用(工作目录固定为应用私有目录)');
    return;
  }

  /** 旧的恢复逻辑,保留备查 */
  private async restoreFolderLegacy(): Promise<void> {
    try {
      const pref = preferences.getPreferencesSync(this.context, { name: PREF_STORE });
      const uri: string = pref.getSync(PREF_KEY_URI, '') as string;
      const name: string = pref.getSync(PREF_KEY_NAME, '') as string;
      if (uri !== undefined && uri !== null && uri.length > 0) {
        this.rootUri = uri;
        this.rootName = (name !== undefined && name !== null && name.length > 0) ? name : this.baseName(uri);
        // 重新激活授权,否则重启后读写会失败
        try {
          const policy: fileShare.PolicyInfo = {
            uri: uri,
            operationMode: fileShare.OperationMode.READ_MODE | fileShare.OperationMode.WRITE_MODE
          };
          await fileShare.activatePermission([policy]);
        } catch (e) {
          // 授权可能已被系统回收,后面读取会失败并提示重选
        }
        const info: MaziFolderInfo = { uri: this.rootUri, name: this.rootName, restored: true };
        this.okData('folder', info);
      } else {
        this.okEmpty('folder');
      }
    } catch (e) {
      this.okEmpty('folder');
    }
  }

  /** 让用户选一个文件夹作为「稿件文件夹」 */
  pickFolder(): void {
    this.log('[NATIVE] pickFolder 被调用');
    const options = new picker.DocumentSelectOptions();
    options.selectMode = picker.DocumentSelectMode.FOLDER;
    options.maxSelectNumber = 1;
    const documentPicker = new picker.DocumentViewPicker(this.context);
    documentPicker.select(options).then(async (uris: Array<string>) => {
      this.log('[NATIVE] 选择器返回, uris=' + JSON.stringify(uris));
      if (uris === undefined || uris === null || uris.length === 0) {
        this.log('[NATIVE] 空结果,当作取消');
        this.fail('pickFolder', '已取消');
        return;
      }
      const uri: string = uris[0];
      this.rootUri = uri;
      this.rootName = this.baseName(uri);
      this.log('[NATIVE] 选中 uri=' + uri + ' name=' + this.rootName);

      // 立刻取证:选择器给的 UR 到底能不能访问?不能就说明是 URI/授权的问题,
      // 而不是后面写文件的逻辑问题 —— 之前这里失败被静默吞掉,查了很久。
      let probe: string = 'URI=' + uri;
      try {
        const names: string[] = fs.listFileSync(uri);
        probe += ' | listFile OK, 条目数=' + names.length;
      } catch (e) {
        const be = e as BusinessError;
        probe += ' | listFile 失败 code=' + be.code + ' msg=' + be.message;
      }
      try {
        probe += ' | access=' + fs.accessSync(uri);
      } catch (e) {
        const be2 = e as BusinessError;
        probe += ' | access 抛错 code=' + be2.code;
      }
      try {
        const st = fs.statSync(uri);
        probe += ' | isDir=' + st.isDirectory();
      } catch (e) {
        const be3 = e as BusinessError;
        probe += ' | stat 失败 code=' + be3.code;
      }
      this.lastErr = 'pickFolder 取证: ' + probe;

      // 尽量把授权持久化,下次启动才能直接访问
      let persisted: boolean = false;
      try {
        const policy: fileShare.PolicyInfo = {
          uri: uri,
          operationMode: fileShare.OperationMode.READ_MODE | fileShare.OperationMode.WRITE_MODE
        };
        await fileShare.persistPermission([policy]);
        persisted = true;
      } catch (e) {
        persisted = false;
      }

      try {
        const pref = preferences.getPreferencesSync(this.context, { name: PREF_STORE });
        pref.putSync(PREF_KEY_URI, uri);
        pref.putSync(PREF_KEY_NAME, this.rootName);
        pref.flush();
      } catch (e) {
        // 存不下只影响下次启动恢复
      }

      promptAction.showToast({
        message: persisted ? ('已选定：' + this.rootName) : ('已选定：' + this.rootName + '（本次有效）'),
        duration: 2000
      });
      // 顺便告诉网页这个目录是不是空的(克隆需要一个空目录当仓库根)
      let isEmpty: boolean = false;
      try {
        const names: string[] = fs.listFileSync(uri);
        let visible: number = 0;
        for (let i = 0; i < names.length; i++) {
          const n: string = names[i];
          if (n.length > 0 && n.charAt(0) !== '.') {
            visible++;
          }
        }
        isEmpty = (visible === 0);
      } catch (e) {
        isEmpty = false;
      }
      const picked: MaziPickResult = { uri: uri, name: this.rootName, persisted: persisted, empty: isEmpty };
      this.log('[NATIVE] 回调网页 pickFolder OK, empty=' + isEmpty);
      this.okData('pickFolder', picked);
    }).catch((err: BusinessError) => {
      this.log('[NATIVE] 选择器异常 code=' + (err !== undefined ? err.code : -1)
        + ' msg=' + (err !== undefined ? err.message : '?'));
      if (err !== undefined && err.code !== 13900042) {
        this.fail('pickFolder', '选择文件夹失败');
      } else {
        this.fail('pickFolder', '已取消');
      }
    });
  }

  /** 清除已选文件夹 */
  clearFolder(): void {
    this.rootUri = '';
    this.rootName = '';
    try {
      const pref = preferences.getPreferencesSync(this.context, { name: PREF_STORE });
      pref.deleteSync(PREF_KEY_URI);
      pref.deleteSync(PREF_KEY_NAME);
      pref.flush();
    } catch (e) {
      // 忽略
    }
    this.okEmpty('clearFolder');
  }

  folderInfo(): void {
    if (this.rootUri.length === 0) {
      this.okEmpty('folderInfo');
    } else {
      const info: MaziFolderInfo = { uri: this.rootUri, name: this.rootName, restored: false };
      this.okData('folderInfo', info);
    }
  }

  // ==================== 目录浏览 ====================

  /** 列出某个相对目录下的条目 */
  listDir(rel: string): void {
    if (this.rootUri.length === 0) {
      this.fail('listDir', '还没有选定文件夹');
      return;
    }
    const target: string = this.abs(rel);
    try {
      const names: string[] = fs.listFileSync(target);
      const out: MaziEntry[] = [];
      for (let i = 0; i < names.length; i++) {
        const n: string = names[i];
        if (n.length === 0 || n.charAt(0) === '.') {
          continue;   // 与电脑版一致:隐藏 .git 等隐藏项
        }
        const childRel: string = (rel === undefined || rel === null || rel.length === 0) ? n : (rel + '/' + n);
        let isDir: boolean = false;
        let size: number = 0;
        let mtime: number = 0;
        try {
          const st = fs.statSync(target + '/' + n);
          isDir = st.isDirectory();
          size = st.size;
          mtime = st.mtime;
        } catch (e) {
          continue;   // 读不到属性就跳过
        }
        out.push({ name: n, path: childRel, isDir: isDir, size: size, mtime: mtime });
      }
      // 目录在前,同类按名称排序
      out.sort((a: MaziEntry, b: MaziEntry): number => {
        if (a.isDir !== b.isDir) {
          return a.isDir ? -1 : 1;
        }
        return a.name.localeCompare(b.name);
      });
      const data: MaziDirData = { path: (rel === undefined || rel === null) ? '' : rel, entries: out };
      this.okData('listDir', data);
    } catch (e) {
      const err = e as BusinessError;
      let msg: string = '读取目录失败';
      if (err !== undefined && err.code === 13900012) {
        msg = '没有访问权限，请重新选择文件夹';
      }
      this.fail('listDir', msg);
    }
  }

  // ==================== 文件读写 ====================

  /** 读取文本(与电脑版一致的编码识别:UTF-8 优先,失败回退 GB18030) */
  readText(rel: string): void {
    if (this.rootUri.length === 0) {
      this.fail('readText', '还没有选定文件夹');
      return;
    }
    const target: string = this.abs(rel);
    try {
      const file = fs.openSync(target, fs.OpenMode.READ_ONLY);
      const size: number = fs.statSync(file.fd).size;
      const buffer: ArrayBuffer = new ArrayBuffer(size);
      fs.readSync(file.fd, buffer);
      fs.closeSync(file);

      const bytes: Uint8Array = new Uint8Array(buffer);
      let text: string = '';
      let encoding: string = 'utf-8';
      try {
        text = util.TextDecoder.create('utf-8', { ignoreBOM: true }).decodeToString(bytes);
      } catch (e) {
        try {
          text = util.TextDecoder.create('gb18030').decodeToString(bytes);
          encoding = 'gb18030';
        } catch (e2) {
          text = util.TextDecoder.create('utf-8').decodeToString(bytes);
        }
      }
      if (text.length > 0 && text.charCodeAt(0) === 0xFEFF) {
        text = text.substring(1);
      }
      const read: MaziReadResult = { path: rel, text: text, encoding: encoding };
      this.okData('readText', read);
    } catch (e) {
      this.fail('readText', '读取失败：' + rel);
    }
  }

  /** 按指定编码保存(默认 UTF-8) */
  writeText(rel: string, text: string, encoding?: string): void {
    if (this.rootUri.length === 0) {
      this.fail('writeText', '还没有选定文件夹');
      return;
    }
    const target: string = this.abs(rel);
    try {
      /* 一律按 UTF-8 写:之前用 TextEncoder.create(enc) 传别的编码名会拿到 undefined */
      const buf: ArrayBuffer = this.utf8BufferOf(text);
      const file = fs.openSync(target, fs.OpenMode.READ_WRITE | fs.OpenMode.CREATE | fs.OpenMode.TRUNC);
      fs.writeSync(file.fd, buf);
      fs.closeSync(file);
      const wrote: MaziWriteResult = { path: rel, bytes: buf.byteLength };
      this.log('[WRITE] writeText 成功 ' + rel + ' (' + buf.byteLength + ' 字节)');
      this.okData('writeText', wrote);
    } catch (e) {
      const be = e as BusinessError;
      this.log('[WRITE] writeText 失败 code=' + be.code + ' msg=' + be.message + ' -> ' + rel);
      this.fail('writeText', '保存失败 code=' + be.code + '：' + rel);
    }
  }

  // ==================== 文件操作 ====================

  /**
   * 把字符串按 UTF-8 编成 ArrayBuffer。
   *
   * 为什么不用 util.TextEncoder().encodeInto():它的返回类型/实际值在 ArkTS 里
   * 不稳定 —— 同一个调用在 log() 里能用,在 createFile() 里却拿到 undefined,
   * 报 "Cannot read property buffer/length of undefined"。
   * 手写编码逻辑清晰、行为确定,且不依赖可能变化的 API。
   */
  private utf8BufferOf(s: string): ArrayBuffer {
    const src: string = (s === undefined || s === null) ? '' : s;
    let n: number = 0;
    for (let i = 0; i < src.length; i++) {
      const c: number = src.charCodeAt(i);
      if (c < 0x80) {
        n += 1;
      } else if (c < 0x800) {
        n += 2;
      } else if (c >= 0xD800 && c <= 0xDBFF && i + 1 < src.length) {
        const c2: number = src.charCodeAt(i + 1);
        if (c2 >= 0xDC00 && c2 <= 0xDFFF) {
          n += 4;
          i++;
        } else {
          n += 3;
        }
      } else {
        n += 3;
      }
    }
    const ab: ArrayBuffer = new ArrayBuffer(n);
    const out: Uint8Array = new Uint8Array(ab);
    let p: number = 0;
    for (let i = 0; i < src.length; i++) {
      let c: number = src.charCodeAt(i);
      if (c >= 0xD800 && c <= 0xDBFF && i + 1 < src.length) {
        const c2b: number = src.charCodeAt(i + 1);
        if (c2b >= 0xDC00 && c2b <= 0xDFFF) {
          c = 0x10000 + ((c - 0xD800) << 10) + (c2b - 0xDC00);
          i++;
        } else {
          c = 0xFFFD;
        }
      }
      if (c < 0x80) {
        out[p++] = c;
      } else if (c < 0x800) {
        out[p++] = 0xC0 | (c >> 6);
        out[p++] = 0x80 | (c & 0x3F);
      } else if (c < 0x10000) {
        out[p++] = 0xE0 | (c >> 12);
        out[p++] = 0x80 | ((c >> 6) & 0x3F);
        out[p++] = 0x80 | (c & 0x3F);
      } else {
        out[p++] = 0xF0 | (c >> 18);
        out[p++] = 0x80 | ((c >> 12) & 0x3F);
        out[p++] = 0x80 | ((c >> 6) & 0x3F);
        out[p++] = 0x80 | (c & 0x3F);
      }
    }
    return ab;
  }

  /** 校验/复制 Uint8Array 到新 ArrayBuffer(保留作兼容) */
  private utf8Buffer(bytes: Uint8Array): ArrayBuffer {
    const ab: ArrayBuffer = new ArrayBuffer(bytes.length);
    const view: Uint8Array = new Uint8Array(ab);
    for (let i = 0; i < bytes.length; i++) {
      view[i] = bytes[i];
    }
    return ab;
  }

  createFile(rel: string, text: string): void {
    if (this.rootUri.length === 0) {
      this.fail('createFile', '还没有选定文件夹');
      return;
    }
    const target: string = this.abs(rel);
    this.log('[NEW] createFile rel=' + rel + ' target=' + target);
    try {
      // 用 exists() 而不是直接 fs.accessSync:后者在「不存在」时抛异常,
      // 会让 if 直接跳到 catch,于是永远判不出「已存在」,变成静默覆盖同名文件
      if (this.exists(target)) {
        this.fail('createFile', '同名文件已存在');
        return;
      }
      const content: string = (text === undefined || text === null) ? '' : text;
      const buf: ArrayBuffer = this.utf8BufferOf(content);
      const file = fs.openSync(target, fs.OpenMode.READ_WRITE | fs.OpenMode.CREATE | fs.OpenMode.TRUNC);
      fs.writeSync(file.fd, buf);
      fs.closeSync(file);
      const wrote: MaziWriteResult = { path: rel, bytes: buf.byteLength };
      this.log('[NEW] createFile 成功 ' + target + ' (' + buf.byteLength + ' 字节)');
      this.okData('createFile', wrote);
    } catch (e) {
      const be = e as BusinessError;
      this.log('[NEW] createFile 失败 code=' + be.code + ' msg=' + be.message + ' -> ' + target);
      this.fail('createFile', '新建失败 code=' + be.code + '：' + rel);
    }
  }

  mkdir(rel: string): void {
    if (this.rootUri.length === 0) {
      this.fail('mkdir', '还没有选定文件夹');
      return;
    }
    const target: string = this.abs(rel);
    this.log('[NEW] mkdir rel=' + rel + ' target=' + target + ' 已存在=' + this.exists(target));
    try {
      // 递归模式:新建「a/b」这种多层目录时非递归会失败
      fs.mkdirSync(target, true);
      this.fsOps++;
      const wrote: MaziWriteResult = { path: rel, bytes: 0 };
      this.log('[NEW] mkdir 成功 ' + target);
      this.okData('mkdir', wrote);
    } catch (e) {
      const be = e as BusinessError;
      this.log('[NEW] mkdir 失败 code=' + be.code + ' msg=' + be.message + ' -> ' + target);
      this.fail('mkdir', '新建文件夹失败 code=' + be.code + '：' + rel);
    }
  }

  remove(rel: string, isDir: boolean): void {
    if (this.rootUri.length === 0) {
      this.fail('remove', '还没有选定文件夹');
      return;
    }
    const target: string = this.abs(rel);
    try {
      if (isDir) {
        fs.rmdirSync(target);
      } else {
        fs.unlinkSync(target);
      }
      const wrote: MaziWriteResult = { path: rel, bytes: 0 };
      this.okData('remove', wrote);
    } catch (e) {
      this.fail('remove', '删除失败(目录需为空)：' + rel);
    }
  }

  /** 递归删除目录。rmdirSync 只能删空目录,所以文件夹删除必须用这个。 */
  private rmTreeAbs(path: string): void {
    let names: string[] = [];
    try {
      names = fs.listFileSync(path);
    } catch (e) {
      names = [];
    }
    for (let i = 0; i < names.length; i++) {
      const child: string = path + '/' + names[i];
      let isDir: boolean = false;
      try {
        isDir = fs.statSync(child).isDirectory();
      } catch (e2) {
        isDir = false;
      }
      if (isDir) {
        this.rmTreeAbs(child);
      } else {
        try {
          fs.unlinkSync(child);
        } catch (e3) {
          // 单个文件失败继续删其它
        }
      }
    }
    fs.rmdirSync(path);
  }

  /** 删除文件夹(含全部内容) */
  deleteTree(rel: string): void {
    if (this.rootUri.length === 0) {
      this.fail('deleteTree', '还没有选定文件夹');
      return;
    }
    if (rel === undefined || rel === null || rel.length === 0) {
      this.fail('deleteTree', '不能删除根目录');
      return;
    }
    const target: string = this.abs(rel);
    try {
      this.rmTreeAbs(target);
      this.fsOps++;
      const wrote: MaziWriteResult = { path: rel, bytes: 0 };
      this.okData('deleteTree', wrote);
    } catch (e) {
      const be = e as BusinessError;
      this.fail('deleteTree', '删除失败 code=' + be.code + '：' + rel);
    }
  }

  rename(oldRel: string, newName: string): void {
    if (this.rootUri.length === 0) {
      this.fail('rename', '还没有选定文件夹');
      return;
    }
    let dir: string = '';
    const i: number = oldRel.lastIndexOf('/');
    if (i >= 0) {
      dir = oldRel.substring(0, i + 1);
    }
    const newRel: string = dir + newName;
    try {
      fs.renameSync(this.abs(oldRel), this.abs(newRel));
      const renamed: MaziRenameResult = { from: oldRel, to: newRel };
      this.okData('rename', renamed);
    } catch (e) {
      this.fail('rename', '重命名失败：' + newName);
    }
  }

  // ==================== Git 文件系统桥(同步) ====================
  // isomorphic-git 通过 window.MaziHarmony.git* 同步读写仓库文件。
  // 字符串走 UTF-8;二进制走 base64(前缀 "b64:")以保证字节完全一致。

  /**
   * 读文件。**始终**以 "b64:<base64>" 返回,由网页端按需解码。
   *
   * 为什么不再自动判文本/二进制:之前用「有没有 0x00 字节」来猜,
   * 但 zlib 压缩过的 git 对象(对象头 78 01 / 78 9C / 78 DA)里几乎没有 0 字节,
   * 会被误判成文本 → 走 UTF-8 解码 → 字节被破坏 →
   * pako 报 "incorrect header check",表现为「提交/推送失败」而克隆正常
   * (克隆的数据在内存里,不经过这条读路径)。
   * 一律走 base64 可以保证字节完全一致。
   */
  gitRead(rel: string): string | null {
    if (this.rootUri.length === 0) {
      return null;
    }
    try {
      const file = fs.openSync(this.abs(rel), fs.OpenMode.READ_ONLY);
      const size: number = fs.statSync(file.fd).size;
      if (size <= 0) {
        fs.closeSync(file);
        return 'b64:';
      }
      const buffer: ArrayBuffer = new ArrayBuffer(size);
      const read: number = fs.readSync(file.fd, buffer);
      fs.closeSync(file);
      const bytes: Uint8Array = new Uint8Array(buffer.slice(0, read));
      this.fsOps++;
      return 'b64:' + this.b64.encodeToStringSync(bytes);
    } catch (e) {
      return null;
    }
  }

  /**
   * 路径是否已存在。
   * 注意:fs.accessSync 在「不存在」时是**抛异常**而不是返回 false,
   * 直接写 if (!fs.accessSync(p)) 会因为异常跳到 catch,导致本该创建的目录没被创建
   * —— 这是克隆时 .git/objects/xx/yyy 这类多层路径写不进去的根因。
   */
  private exists(path: string): boolean {
    try {
      return fs.accessSync(path);
    } catch (e) {
      return false;
    }
  }

  /** 确保某个绝对路径的目录存在(含多层递归创建) */
  private ensureDirAbs(path: string): boolean {
    if (path === undefined || path === null || path.length === 0) {
      return false;
    }
    if (this.exists(path)) {
      return true;
    }
    try {
      fs.mkdirSync(path, true);
      this.fsOps++;
      return true;
    } catch (e) {
      // 并发/已存在等情况下再确认一次
      if (this.exists(path)) {
        return true;
      }
      this.noteErr('mkdir', path, e);
      return false;
    }
  }

  /** 写文件:data 为 UTF-8 文本,或 "b64:<base64>" */
  gitWrite(rel: string, data: string): boolean {
    if (this.rootUri.length === 0) {
      this.lastErr = 'gitWrite 失败: 还没选定文件夹';
      return false;
    }
    let stage: string = 'parent';
    let target: string = '';
    try {
      target = this.abs(rel);
      // 只在「相对路径里真的有子目录」时才补建父目录。
      // 注意:千万不要对根目录文件也去补建 —— 根目录的上级是授权范围之外的目录,
      // 对它 mkdir 必然失败(errno 13900002),连根目录文件都会写不进去。
      const relNorm: string = (rel === undefined || rel === null) ? '' : rel;
      const i: number = relNorm.lastIndexOf('/');
      if (i > 0) {
        this.ensureDirAbs(this.abs(relNorm.substring(0, i)));
      }
      stage = 'encode';
      let buf: ArrayBuffer;
      /* 判断必须只看前缀,不能用长度当条件 ——
         之前写的是 data.length > 6,而**空文件的 base64 是空串**,
         payload 恰好是 'b64:'(长度 4,不大于 6),于是被当成文本,
         结果把字面量 "b64:" 写进了文件(首次推送会看到空文件里多了 b64:)。
         前缀判断对空数据也成立,'b64:' 解出来就是 0 字节。
         注意:decodeSync('') 会抛 "length greater than 0",所以空内容要单独处理,
         否则仓库里只要有 0 字节文件,克隆就会整批失败。 */
      if (data !== null && data !== undefined && data.length >= 4 && data.substring(0, 4) === 'b64:') {
        const b64Body: string = data.substring(4);
        if (b64Body.length === 0) {
          buf = new ArrayBuffer(0);       // 空文件:写 0 字节
        } else {
          const raw: Uint8Array = this.b64.decodeSync(b64Body);
          buf = this.utf8Buffer(raw);
        }
      } else {
        buf = this.utf8BufferOf(data === undefined || data === null ? '' : data);
      }
      stage = 'open';
      const file = fs.openSync(target, fs.OpenMode.READ_WRITE | fs.OpenMode.CREATE | fs.OpenMode.TRUNC);
      stage = 'write';
      fs.writeSync(file.fd, buf);
      stage = 'close';
      fs.closeSync(file);
      this.fsOps++;
      return true;
    } catch (e) {
      this.noteErr('gitWrite[' + stage + ']', target.length > 0 ? target : rel, e);
      return false;
    }
  }

  gitMkdir(rel: string): boolean {
    if (this.rootUri.length === 0) {
      return false;
    }
    const relNorm: string = (rel === undefined || rel === null) ? '' : rel;
    if (relNorm.length === 0) {
      return true;   // 根目录本来就存在,不需要建
    }
    try {
      return this.ensureDirAbs(this.abs(relNorm));
    } catch (e) {
      return false;
    }
  }

  gitUnlink(rel: string): boolean {
    if (this.rootUri.length === 0) {
      return false;
    }
    try {
      fs.unlinkSync(this.abs(rel));
      return true;
    } catch (e) {
      return false;
    }
  }

  gitRmdir(rel: string): boolean {
    if (this.rootUri.length === 0) {
      return false;
    }
    try {
      fs.rmdirSync(this.abs(rel));
      return true;
    } catch (e) {
      return false;
    }
  }

  /** 列目录:返回 JSON 数组字符串,失败返回 "[]" */
  gitReaddir(rel: string): string {
    if (this.rootUri.length === 0) {
      return '[]';
    }
    try {
      const names: string[] = fs.listFileSync(this.abs(rel));
      return JSON.stringify(names);
    } catch (e) {
      return '[]';
    }
  }

  /** 文件属性:返回 JSON {"type":"file"|"dir","size":n,"mtimeMs":n} ;失败返回 null */
  gitStat(rel: string): string | null {
    if (this.rootUri.length === 0) {
      return null;
    }
    try {
      const st = fs.statSync(this.abs(rel));
      const type: string = st.isDirectory() ? 'dir' : 'file';
      return '{"type":"' + type + '","size":' + st.size + ',"mtimeMs":' + st.mtime + '}';
    } catch (e) {
      return null;
    }
  }

  /**
   * 路径是否存在。
   * 用 statSync 而不是 accessSync —— 实测 accessSync 对本应用私有目录里的
   * 刚创建目录会返回 false(文件确实存在),而 statSync 判断可靠。
   */
  gitExists(rel: string): boolean {
    if (this.rootUri.length === 0) {
      this.log('[EXISTS] rootUri 为空,直接 false (rel=' + rel + ')');
      return false;
    }
    const full: string = this.abs(rel);
    try {
      fs.statSync(full);
      return true;
    } catch (e) {
      const be = e as BusinessError;
      this.log('[EXISTS] false: ' + full + '  code=' + be.code + ' ' + be.message);
      return false;
    }
  }

  // ==================== HTTP 桥(给 isomorphic-git 用) ====================
  // GitHub/Gitee 的 git 端点不发 CORS 头,网页里 fetch 会被浏览器拦,
  // 因此绕到原生 @ohos.net.http 发请求(原生无同源限制)。

  gitHttp(reqJson: string): void {
    let req: MaziHttpReq;
    try {
      req = JSON.parse(reqJson) as MaziHttpReq;
    } catch (e) {
      this.okData('gitHttp', '');
      return;
    }
    const httpRequest = http.createHttp();
    const method: http.RequestMethod =
      (req.method !== undefined && req.method !== null && req.method.toUpperCase() === 'POST')
        ? http.RequestMethod.POST : http.RequestMethod.GET;
    const reqHeaders: Record<string, string> =
      (req.headers === undefined || req.headers === null) ? {} : req.headers;
    const options: http.HttpRequestOptions = {
      method: method,
      header: reqHeaders,
      connectTimeout: 30000,
      readTimeout: 60000,
      // 默认只有 5MB,packfile 很容易超;设到允许的最大值 100MB
      maxLimit: 100 * 1024 * 1024,
      expectDataType: http.HttpDataType.ARRAY_BUFFER
    };
    let bodyBytesArr: Uint8Array | null = null;
    if (req.body !== undefined && req.body !== null && req.body.length > 0) {
      bodyBytesArr = this.b64.decodeSync(req.body);
      /* 传字节体的正确方式:用 ArrayBuffer。
         不要用 Uint8Array(它是对象,可能不被当成原始字节),
         也不要手动加 content-length —— 实测「手动 content-length + extraData」
         会让 @ohos.net.http 直接报 2300999 Internal error,请求根本发不出去。
         extraData 为 ArrayBuffer 时,库会自己算长度。 */
      const ab: ArrayBuffer = new ArrayBuffer(bodyBytesArr.length);
      const view: Uint8Array = new Uint8Array(ab);
      for (let i = 0; i < bodyBytesArr.length; i++) {
        view[i] = bodyBytesArr[i];
      }
      options.extraData = ab;
    }

    const bridge = this;
    // 记录请求的全部细节 —— 400 这种错误必须看到「实际发出去的 header / body 长度」
    // 以及「响应体里的说明」才能定位。
    let hdrs: string = '';
    try {
      hdrs = JSON.stringify(reqHeaders);
    } catch (e) {
      hdrs = '(序列化失败)';
    }
    let bodyBytes: number = 0;
    if (req.body !== undefined && req.body !== null && req.body.length > 0) {
      try {
        bodyBytes = this.b64.decodeSync(req.body).length;
      } catch (e) {
        bodyBytes = -1;
      }
    }
    bridge.log('[HTTP→] ' + method + ' ' + req.url + '\n  headers=' + hdrs
      + '\n  bodyBase64Len=' + ((req.body !== undefined && req.body !== null) ? req.body.length : 0)
      + ' decodedBytes=' + bodyBytes);
    httpRequest.request(req.url, options, (err: BusinessError, resp: http.HttpResponse) => {
      try {
        if (err !== undefined && err !== null && err.code !== 0) {
          bridge.log('[HTTP✗] ' + req.url + ' err.code=' + err.code + ' msg=' + err.message);
          bridge.okData('gitHttp', '');
          return;
        }
        const status: number = resp.responseCode;
        let bodyB64: string = '';
        const data = resp.result;
        if (data instanceof ArrayBuffer) {
          bodyB64 = bridge.b64.encodeToStringSync(new Uint8Array(data));
        } else if (typeof data === 'string') {
          bodyB64 = bridge.b64.encodeToStringSync(new util.TextEncoder().encodeInto(data as string));
        }
        bridge.log('[HTTP←] ' + status + ' len=' + bodyB64.length + ' ' + req.url);
        // 非 2xx 时把响应体前 400 字符也记下来 —— GitHub 的 400 会说明具体原因
        if (status < 200 || status >= 300) {
          let snippet: string = '';
          try {
            const dec = util.TextDecoder.create('utf-8');
            const raw = bridge.b64.decodeSync(bodyB64);
            snippet = dec.decodeToString(raw);
          } catch (e) {
            snippet = '(响应体解码失败)';
          }
          if (snippet.length > 400) {
            snippet = snippet.substring(0, 400);
          }
          bridge.log('[HTTP-BODY ' + status + '] ' + snippet);
        }
        // resp.header 是 Object 类型;ArkTS 既不允许 src['k'] 也不允许 for...in,
        // 直接把整个对象 JSON 化交给网页过滤(已剔除 set-cookie 等敏感头)。
        let headersJson: string = '{}';
        try {
          headersJson = JSON.stringify(resp.header);
          if (headersJson === undefined || headersJson === null || headersJson.length === 0) {
            headersJson = '{}';
          }
        } catch (e) {
          headersJson = '{}';
        }

        const outJson: string = '{"status":' + status + ',"statusText":"","headers":'
          + headersJson + ',"body":' + JSON.stringify(bodyB64) + '}';
        bridge.okData('gitHttp', outJson);
      } catch (e) {
        const be = e as BusinessError;
        bridge.log('[HTTP✗] 解析响应异常 code=' + be.code + ' msg=' + be.message);
        bridge.okData('gitHttp', '');
      } finally {
        httpRequest.destroy();
      }
    });
  }

  // ==================== 网页配置的原生持久化 ====================
  // ArkWeb 的 localStorage 在部分机器上重启会丢,这里用原生 preferences 兜底。
  // 网页启动时一次性把所有键读回去,之后每次写入都镜像到这里。

  /** 读取全部配置:返回 {"键":"值"} 的 JSON 字符串;没有则返回 "{}" */
  configLoad(): string {
    try {
      const pref = preferences.getPreferencesSync(this.context, { name: PREF_CFG });
      // getAllSync() 返回 Object;ArkTS 既不允许下标读取也不允许对象字面量推断,
      // 直接整体 JSON 化交给网页处理(与 gitHttp 里处理 resp.header 同一思路)
      const all: Object = pref.getAllSync();
      const text: string = JSON.stringify(all);
      return (text === undefined || text === null || text.length === 0) ? '{}' : text;
    } catch (e) {
      return '{}';
    }
  }

  /** 写入一个键(值为字符串) */
  configSet(key: string, value: string): boolean {
    if (key === undefined || key === null || key.length === 0) {
      return false;
    }
    try {
      const pref = preferences.getPreferencesSync(this.context, { name: PREF_CFG });
      pref.putSync(key, value === undefined || value === null ? '' : value);
      pref.flush();
      return true;
    } catch (e) {
      return false;
    }
  }

  /** 删除一个键 */
  configRemove(key: string): boolean {
    if (key === undefined || key === null || key.length === 0) {
      return false;
    }
    try {
      const pref = preferences.getPreferencesSync(this.context, { name: PREF_CFG });
      pref.deleteSync(key);
      pref.flush();
      return true;
    } catch (e) {
      return false;
    }
  }

  // ==================== 导出(保留原有能力) ====================

  /** 导出:弹出系统文件保存框,成功后回调网页 */
  saveText(filename: string, text: string): string {
    let name: string = (filename === undefined || filename === null || filename.length === 0) ? '稿件.txt' : filename;
    if (!name.toLowerCase().endsWith('.txt')) {
      name = name + '.txt';
    }
    const controller = this.controller;
    const context = this.context;

    const options = new picker.DocumentSaveOptions();
    options.newFileNames = [name];
    const documentPicker = new picker.DocumentViewPicker(context);
    documentPicker.save(options).then((uris: Array<string>) => {
      if (uris === undefined || uris.length === 0) {
        return;
      }
      try {
        const file = fs.openSync(uris[0], fs.OpenMode.READ_WRITE | fs.OpenMode.TRUNC);
        fs.writeSync(file.fd, text);
        fs.closeSync(file);
        promptAction.showToast({ message: '已导出 ' + name });
        controller.runJavaScript('window.maziSaved && window.maziSaved("已导出 ' + name + '")');
      } catch (e) {
        promptAction.showToast({ message: '写入失败' });
      }
    }).catch((err: BusinessError) => {
      try {
        const path: string = context.filesDir + '/' + name;
        const file = fs.openSync(path, fs.OpenMode.READ_WRITE | fs.OpenMode.CREATE | fs.OpenMode.TRUNC);
        fs.writeSync(file.fd, text);
        fs.closeSync(file);
        promptAction.showToast({ message: '已保存到应用目录：' + name });
        controller.runJavaScript('window.maziSaved && window.maziSaved("已保存到应用目录")');
      } catch (e2) {
        promptAction.showToast({ message: '导出失败' });
      }
    });
    return '正在导出…';
  }

  /** 导入:弹出系统文件选择框,读文本后回调网页 */
  importFile(): void {
    const controller = this.controller;
    const context = this.context;
    const options = new picker.DocumentSelectOptions();
    options.maxSelectNumber = 1;
    options.fileSuffixFilters = ['.txt'];
    const documentPicker = new picker.DocumentViewPicker(context);
    documentPicker.select(options).then((uris: Array<string>) => {
      if (uris === undefined || uris.length === 0) {
        return;
      }
      try {
        const file = fs.openSync(uris[0], fs.OpenMode.READ_ONLY);
        const size: number = fs.statSync(file.fd).size;
        const buffer: ArrayBuffer = new ArrayBuffer(size);
        fs.readSync(file.fd, buffer);
        fs.closeSync(file);

        const bytes: Uint8Array = new Uint8Array(buffer);
        let text: string = '';
        try {
          text = util.TextDecoder.create('utf-8', { ignoreBOM: true }).decodeToString(bytes);
        } catch (e) {
          try {
            text = util.TextDecoder.create('gb18030').decodeToString(bytes);
          } catch (e2) {
            text = util.TextDecoder.create('utf-8').decodeToString(bytes);
          }
        }
        const raw: string = uris[0];
        let name: string = raw.substring(raw.lastIndexOf('/') + 1);
        try {
          name = decodeURIComponent(name);
        } catch (e) {
          // 用原样
        }
        controller.runJavaScript('window.maziImportFile(' + JSON.stringify(name) + ',' + JSON.stringify(text) + ')');
      } catch (e) {
        promptAction.showToast({ message: '读取文件失败' });
      }
    }).catch((err: BusinessError) => {
      if (err !== undefined && err.code !== 13900042) {
        promptAction.showToast({ message: '导入失败' });
      }
    });
  }
}

@Entry
@Component
struct Index {
  private controller: webview.WebviewController = new webview.WebviewController();
  private bridge: MaziBridge | undefined = undefined;

  aboutToAppear(): void {
    const context = getContext(this) as common.UIAbilityContext;
    this.bridge = new MaziBridge(this.controller, context);
  }

  onBackPress(): boolean {
    if (this.bridge !== undefined && this.bridge.isSheetOpen()) {
      this.controller.runJavaScript('window.maziBack && window.maziBack()');
      return true;
    }
    return false;
  }

  build() {
    Column() {
      Web({ src: $rawfile('index.html'), controller: this.controller })
        .javaScriptAccess(true)
        .domStorageAccess(true)
        .databaseAccess(true)
        .fileAccess(true)
        .imageAccess(true)
        .zoomAccess(false)
        .horizontalScrollBarAccess(false)
        .verticalScrollBarAccess(false)
        .onPageEnd(() => {
          // 页面加载完成后恢复上次选定的文件夹
          if (this.bridge !== undefined) {
            this.bridge.restoreFolder();
          }
        })
        .javaScriptProxy({
          object: this.bridge,
          name: 'MaziHarmony',
          methodList: [
            'platform', 'toast', 'setSheetOpen',
            'pickFolder', 'clearFolder', 'folderInfo',
            'listDir', 'readText', 'writeText',
            'createFile', 'mkdir', 'remove', 'rename',
            'gitRead', 'gitWrite', 'gitMkdir', 'gitUnlink', 'gitRmdir',
            'gitReaddir', 'gitStat', 'gitExists', 'gitHttp', 'lastNativeErr', 'probeUri',
            'configLoad', 'configSet', 'configRemove', 'useAppDir', 'log', 'clearLog', 'testHttpPost',
            'gitReadB64', 'saveB64', 'deleteTree',
            'saveText', 'importFile'
          ],
          controller: this.controller
        })
        .width('100%')
        .height('100%')
    }
    .width('100%')
    .height('100%')
    .backgroundColor('#F6F7F9')
  }
}
