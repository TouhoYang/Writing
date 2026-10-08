import { webview } from '@kit.ArkWeb';
import { fileIo as fs, picker } from '@kit.CoreFileKit';
import { common } from '@kit.AbilityKit';
import { promptAction } from '@kit.ArkUI';
import { util } from '@kit.ArkTS';
import { BusinessError } from '@kit.BasicServicesKit';

/**
 * 提供给网页的原生能力(网页通过 window.MaziHarmony 调用)
 */
class MaziBridge {
  private controller: webview.WebviewController;
  private context: common.UIAbilityContext;
  private sheetOpen: boolean = false;

  constructor(controller: webview.WebviewController, context: common.UIAbilityContext) {
    this.controller = controller;
    this.context = context;
  }

  platform(): string {
    return 'harmony';
  }

  toast(msg: string): void {
    promptAction.showToast({ message: msg, duration: 2000 });
  }

  /** 网页抽屉开合状态,用于返回键判定 */
  setSheetOpen(open: boolean): void {
    this.sheetOpen = open;
  }

  isSheetOpen(): boolean {
    return this.sheetOpen;
  }

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
      // 用户取消或没有权限时,退回写入应用目录
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

        let text: string = '';
        try {
          text = util.TextDecoder.create('utf-8', { ignoreBOM: true }).decodeToString(new Uint8Array(buffer));
        } catch (e) {
          text = util.TextDecoder.create('utf-8').decodeToString(new Uint8Array(buffer));
        }
        const raw: string = uris[0];
        const name: string = raw.substring(raw.lastIndexOf('/') + 1);
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
    // 抽屉打开时:先关抽屉;否则交给系统(退出应用)
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
        .javaScriptProxy({
          object: this.bridge,
          name: 'MaziHarmony',
          methodList: ['platform', 'toast', 'setSheetOpen', 'saveText', 'importFile'],
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
