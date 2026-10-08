package com.touho.writing;

import android.app.Activity;
import android.content.ContentValues;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.provider.MediaStore;
import android.util.Base64;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowManager;
import android.webkit.JavascriptInterface;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.Charset;
import java.util.Iterator;
import java.util.List;
import java.util.Map;

/**
 * 写作 · 安卓版外壳
 * 用 WebView 承载移动版界面(assets/index.html)，并提供原生能力：
 *   1) 导出 txt 到系统「下载」目录（MediaStore，无需存储权限）
 *   2) 导入 txt（系统文件选择器）
 *   3) 文件夹访问（SAF 选目录 + 持久授权）→ 目录树 / 读写 txt
 *   4) Git 文件系统桥 + HTTP 桥（isomorphic-git 跑在网页里）
 */
public class MainActivity extends Activity {

    private WebView web;
    private ValueCallback<Uri[]> fileCallback;
    private static final int REQ_FILE = 1001;
    private static final int REQ_TREE = 1002;

    private AndroidFs fs;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        fs = new AndroidFs(this);

        // 状态栏/导航栏白色,浅色图标
        Window w = getWindow();
        if (Build.VERSION.SDK_INT >= 21) {
            w.addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS);
            w.setStatusBarColor(0xFFFFFFFF);
            w.setNavigationBarColor(0xFFFFFFFF);
        }
        if (Build.VERSION.SDK_INT >= 23) {
            w.getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR);
        }
        // 码字时不让屏幕熄灭
        w.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        web = new WebView(this);
        web.setLayoutParams(new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        web.setBackgroundColor(0xFFF6F7F9);

        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);          // localStorage:稿件与设置都存这里
        s.setDatabaseEnabled(true);
        s.setAllowFileAccess(true);
        s.setSupportZoom(false);
        s.setBuiltInZoomControls(false);
        s.setTextZoom(100);                    // 固定字号,避免系统字体缩放打乱排版
        s.setLoadWithOverviewMode(false);
        s.setUseWideViewPort(false);
        s.setCacheMode(WebSettings.LOAD_NO_CACHE);

        web.setWebViewClient(new WebViewClient() {
            @Override
            public void onPageFinished(WebView view, String url) {
                // 页面就绪后恢复上次授权的文件夹
                pushRestoredFolder();
            }
        });
        web.addJavascriptInterface(new Bridge(), "MaziAndroid");
        // 网页端统一按 window.MaziHarmony 探测原生能力,这里挂同名对象,
        // 让安卓复用与鸿蒙同一套 gitFs()/gitHttp() 适配器代码。
        web.addJavascriptInterface(new HarmonyBridge(), "MaziHarmony");
        web.setWebChromeClient(new WebChromeClient() {
            @Override
            public boolean onShowFileChooser(WebView view, ValueCallback<Uri[]> callback,
                                             FileChooserParams params) {
                if (fileCallback != null) fileCallback.onReceiveValue(null);
                fileCallback = callback;
                Intent intent = new Intent(Intent.ACTION_GET_CONTENT);
                intent.addCategory(Intent.CATEGORY_OPENABLE);
                intent.setType("text/plain");
                try {
                    startActivityForResult(Intent.createChooser(intent, "选择 txt 文件"), REQ_FILE);
                } catch (Exception e) {
                    fileCallback = null;
                    return false;
                }
                return true;
            }
        });

        // 用固定 baseUrl 载入,保证 localStorage 有稳定来源
        String html = readAsset("index.html");
        web.loadDataWithBaseURL("https://mazi.local/", html, "text/html", "UTF-8", null);
        setContentView(web);
    }

    private String readAsset(String name) {
        StringBuilder sb = new StringBuilder();
        InputStream in = null;
        try {
            in = getAssets().open(name);
            BufferedReader br = new BufferedReader(new InputStreamReader(in, Charset.forName("UTF-8")));
            String line;
            while ((line = br.readLine()) != null) sb.append(line).append('\n');
        } catch (IOException e) {
            return "<html><body style='font-family:sans-serif;padding:24px'>读取内置页面失败：" + e.getMessage() + "</body></html>";
        } finally {
            try { if (in != null) in.close(); } catch (IOException ignored) { }
        }
        return sb.toString();
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        if (requestCode == REQ_FILE) {
            Uri[] result = null;
            if (resultCode == RESULT_OK && data != null && data.getData() != null) {
                result = new Uri[]{ data.getData() };
            }
            if (fileCallback != null) {
                fileCallback.onReceiveValue(result);
                fileCallback = null;
            }
            return;
        }
        if (requestCode == REQ_TREE) {
            if (resultCode == RESULT_OK && data != null && data.getData() != null) {
                Uri tree = data.getData();
                fs.saveRoot(tree, lastSegment(tree));
                emit("pickFolder", ok("{\"uri\":" + js(tree.toString())
                        + ",\"name\":" + js(fs.rootName()) + ",\"persisted\":true}"));
            } else {
                emit("pickFolder", fail("已取消"));
            }
            return;
        }
        super.onActivityResult(requestCode, resultCode, data);
    }

    /** 从 tree URI 里取最后一段作为显示名(形如 content://.../tree/primary%3ADocuments) */
    private static String lastSegment(Uri uri) {
        String s = uri.toString();
        int i = s.lastIndexOf("%3A");
        if (i < 0) i = s.lastIndexOf("%3a");
        if (i >= 0) s = s.substring(i + 3);
        int j = s.lastIndexOf('/');
        if (j >= 0) s = s.substring(j + 1);
        try { s = Uri.decode(s); } catch (Exception ignored) { }
        return s.length() == 0 ? "稿件文件夹" : s;
    }

    /** 页面加载完成后把已保存的根目录告诉网页 */
    private void pushRestoredFolder() {
        Uri root = fs.rootUri();
        if (root == null) {
            emit("folder", "{\"ok\":true,\"data\":null}");
        } else {
            emit("folder", ok("{\"uri\":" + js(root.toString())
                    + ",\"name\":" + js(fs.rootName()) + ",\"restored\":true}"));
        }
    }

    // ==================== 原生 -> 网页 回调 ====================

    private void emit(final String name, final String payload) {
        runOnUiThread(new Runnable() {
            @Override public void run() {
                if (web == null) return;
                String jsCode = "window.maziOn && window.maziOn(" + js(name) + "," + payload + ")";
                web.evaluateJavascript(jsCode, null);
            }
        });
    }

    private static String ok(String dataJson) {
        return "{\"ok\":true,\"data\":" + dataJson + "}";
    }

    private static String fail(String msg) {
        return "{\"ok\":false,\"error\":" + js(msg) + "}";
    }

    private static String js(String s) {
        if (s == null) return "null";
        StringBuilder sb = new StringBuilder("\"");
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"':  sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\n': sb.append("\\n"); break;
                case '\r': sb.append("\\r"); break;
                case '\t': sb.append("\\t"); break;
                default:
                    if (c < 0x20) sb.append(String.format("\\u%04x", (int) c));
                    else sb.append(c);
            }
        }
        return sb.append("\"").toString();
    }

    @Override
    public void onBackPressed() {
        // 交给页面处理(关闭抽屉等),页面不处理则退出
        web.evaluateJavascript(
                "(function(){ if(window.maziBack && window.maziBack()) return '1'; return '0'; })()",
                new ValueCallback<String>() {
                    @Override
                    public void onReceiveValue(String value) {
                        if (value == null || !value.contains("1")) {
                            finish();
                        }
                    }
                });
    }

    @Override
    protected void onPause() {
        super.onPause();
        if (web != null) web.onPause();
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (web != null) web.onResume();
    }

    /** 提供给网页调用的原生能力(旧接口,保持兼容) */
    private class Bridge {
        /** 保存文本到系统「下载」目录,返回提示信息 */
        @JavascriptInterface
        public String saveText(String filename, String text) {
            String safe = filename == null || filename.trim().length() == 0 ? "稿件.txt" : filename.trim();
            if (!safe.toLowerCase().endsWith(".txt")) safe = safe + ".txt";
            try {
                if (Build.VERSION.SDK_INT >= 29) {
                    ContentValues cv = new ContentValues();
                    cv.put(MediaStore.Downloads.DISPLAY_NAME, safe);
                    cv.put(MediaStore.Downloads.MIME_TYPE, "text/plain");
                    Uri uri = getContentResolver().insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, cv);
                    if (uri == null) return "保存失败：无法创建文件";
                    OutputStream os = getContentResolver().openOutputStream(uri);
                    os.write(text.getBytes("UTF-8"));
                    os.flush();
                    os.close();
                    return "已保存到「下载」：" + safe;
                }
                File dir = getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS);
                if (dir == null) dir = getFilesDir();
                if (!dir.exists()) dir.mkdirs();
                File f = new File(dir, safe);
                FileOutputStream fos = new FileOutputStream(f);
                fos.write(text.getBytes("UTF-8"));
                fos.flush();
                fos.close();
                return "已保存：" + f.getAbsolutePath();
            } catch (Exception e) {
                return "保存失败：" + e.getMessage();
            }
        }

        /** 原生提示 */
        @JavascriptInterface
        public void toast(final String msg) {
            runOnUiThread(new Runnable() {
                @Override
                public void run() {
                    Toast.makeText(MainActivity.this, msg, Toast.LENGTH_SHORT).show();
                }
            });
        }

        /** 版本标记,网页据此判断是否运行在安卓外壳内 */
        @JavascriptInterface
        public String platform() {
            return "android";
        }
    }

    /**
     * 与鸿蒙版 Index.ets 的 MaziHarmony 同名同语义,让网页端一套适配器两端通用。
     * 安卓 JS 桥只能异步,因此:
     *   - 目录/文件类(listDir/readText/...)完成后 emit 同名事件
     *   - git 同步类(gitRead/gitWrite/...)直接返回,供 isomorphic-git 的 promises 适配器包装
     *   - gitHttp 完成后 emit("gitHttp", ...)
     */
    private class HarmonyBridge {

        @JavascriptInterface public String platform() { return "android"; }
        @JavascriptInterface public void toast(String m) { new Bridge().toast(m); }
        @JavascriptInterface public void setSheetOpen(boolean open) { }
        @JavascriptInterface public void restoreFolder() { pushRestoredFolder(); }

        @JavascriptInterface
        public void folderInfo() {
            Uri root = fs.rootUri();
            if (root == null) {
                emit("folderInfo", "{\"ok\":true,\"data\":null}");
            } else {
                emit("folderInfo", ok("{\"uri\":" + js(root.toString())
                        + ",\"name\":" + js(fs.rootName()) + ",\"restored\":false}"));
            }
        }

        // ---------- 文件夹选择 ----------
        @JavascriptInterface
        public void pickFolder() {
            try {
                Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE);
                i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION
                        | Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                        | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION
                        | Intent.FLAG_GRANT_PREFIX_URI_PERMISSION);
                startActivityForResult(i, REQ_TREE);
            } catch (Exception e) {
                emit("pickFolder", fail("无法打开文件夹选择器：" + e.getMessage()));
            }
        }

        @JavascriptInterface
        public void clearFolder() {
            fs.clearRoot();
            emit("clearFolder", ok("null"));
        }

        // ---------- 目录树 / 文本读写(异步回调) ----------
        @JavascriptInterface
        public void listDir(final String rel) {
            if (fs.rootUri() == null) { emit("listDir", fail("还没有选定文件夹")); return; }
            new Thread(new Runnable() { @Override public void run() {
                String entries = fs.listDirJson(rel == null ? "" : rel);
                emit("listDir", ok("{\"path\":" + js(rel == null ? "" : rel)
                        + ",\"entries\":" + entries + "}"));
            } }).start();
        }

        @JavascriptInterface
        public void readText(final String rel) {
            if (fs.rootUri() == null) { emit("readText", fail("还没有选定文件夹")); return; }
            new Thread(new Runnable() { @Override public void run() {
                String r = fs.gitRead(rel);
                if (r == null) { emit("readText", fail("读取失败：" + rel)); return; }
                boolean bin = r.startsWith("b64:");
                emit("readText", ok("{\"path\":" + js(rel) + ",\"text\":" + js(bin ? "" : r)
                        + ",\"encoding\":" + js(bin ? "base64" : "utf-8") + "}"));
            } }).start();
        }

        @JavascriptInterface
        public void writeText(final String rel, final String text, final String encoding) {
            if (fs.rootUri() == null) { emit("writeText", fail("还没有选定文件夹")); return; }
            new Thread(new Runnable() { @Override public void run() {
                if (fs.gitWrite(rel, text)) emit("writeText", ok("{\"path\":" + js(rel) + ",\"bytes\":0}"));
                else emit("writeText", fail("保存失败：" + rel));
            } }).start();
        }

        @JavascriptInterface
        public void createFile(final String rel, final String text) {
            if (fs.rootUri() == null) { emit("createFile", fail("还没有选定文件夹")); return; }
            new Thread(new Runnable() { @Override public void run() {
                if (fs.gitExists(rel)) { emit("createFile", fail("同名文件已存在")); return; }
                if (fs.gitWrite(rel, text == null ? "" : text)) emit("createFile", ok("{\"path\":" + js(rel) + ",\"bytes\":0}"));
                else emit("createFile", fail("新建失败：" + rel));
            } }).start();
        }

        @JavascriptInterface
        public void mkdir(final String rel) {
            if (fs.rootUri() == null) { emit("mkdir", fail("还没有选定文件夹")); return; }
            new Thread(new Runnable() { @Override public void run() {
                if (fs.gitMkdir(rel)) emit("mkdir", ok("{\"path\":" + js(rel) + ",\"bytes\":0}"));
                else emit("mkdir", fail("新建文件夹失败：" + rel));
            } }).start();
        }

        @JavascriptInterface
        public void remove(final String rel, final boolean isDir) {
            if (fs.rootUri() == null) { emit("remove", fail("还没有选定文件夹")); return; }
            new Thread(new Runnable() { @Override public void run() {
                boolean okk = isDir ? fs.gitRmdir(rel) : fs.gitUnlink(rel);
                if (okk) emit("remove", ok("{\"path\":" + js(rel) + ",\"bytes\":0}"));
                else emit("remove", fail("删除失败(目录需为空)：" + rel));
            } }).start();
        }

        @JavascriptInterface
        public void rename(final String oldRel, final String newName) {
            if (fs.rootUri() == null) { emit("rename", fail("还没有选定文件夹")); return; }
            new Thread(new Runnable() { @Override public void run() {
                if (fs.rename(oldRel, newName)) {
                    String dir = "";
                    int i = oldRel.lastIndexOf('/');
                    if (i >= 0) dir = oldRel.substring(0, i + 1);
                    emit("rename", ok("{\"from\":" + js(oldRel) + ",\"to\":" + js(dir + newName) + "}"));
                } else {
                    emit("rename", fail("重命名失败：" + newName));
                }
            } }).start();
        }

        // ---------- git 文件系统(同步返回) ----------
        @JavascriptInterface public String gitRead(String rel) { return fs.gitRead(rel); }
        @JavascriptInterface public boolean gitWrite(String rel, String data) { return fs.gitWrite(rel, data); }
        @JavascriptInterface public boolean gitMkdir(String rel) { return fs.gitMkdir(rel); }
        @JavascriptInterface public boolean gitUnlink(String rel) { return fs.gitUnlink(rel); }
        @JavascriptInterface public boolean gitRmdir(String rel) { return fs.gitRmdir(rel); }
        @JavascriptInterface public String gitReaddir(String rel) { return fs.gitReaddir(rel); }
        @JavascriptInterface public String gitStat(String rel) { return fs.gitStat(rel); }
        @JavascriptInterface public boolean gitExists(String rel) { return fs.gitExists(rel); }

        // ---------- 网页配置的原生持久化(与鸿蒙同名同语义) ----------
        @JavascriptInterface public String configLoad() { return fs.configLoad(); }
        @JavascriptInterface public boolean configSet(String key, String value) { return fs.configSet(key, value); }
        @JavascriptInterface public boolean configRemove(String key) { return fs.configRemove(key); }

        /**
         * HTTP 桥:入参 {url,method,headers,body(base64)} 的 JSON 字符串,
         * 完成后 emit("gitHttp", {ok,data:{status,statusText,headers,body(base64)}})。
         */
        @JavascriptInterface
        public void gitHttp(final String reqJson) {
            new Thread(new Runnable() { @Override public void run() {
                HttpURLConnection conn = null;
                try {
                    JSONObject req = new JSONObject(reqJson);
                    URL u = new URL(req.getString("url"));
                    String method = req.optString("method", "GET");
                    conn = (HttpURLConnection) u.openConnection();
                    conn.setRequestMethod(method);
                    conn.setConnectTimeout(30000);
                    conn.setReadTimeout(120000);
                    conn.setInstanceFollowRedirects(true);
                    if (req.has("headers")) {
                        JSONObject hs = req.getJSONObject("headers");
                        Iterator<String> it = hs.keys();
                        while (it.hasNext()) {
                            String k = it.next();
                            conn.setRequestProperty(k, hs.getString(k));
                        }
                    }
                    if (req.has("body") && !req.isNull("body")) {
                        byte[] body = Base64.decode(req.getString("body"), Base64.NO_WRAP);
                        conn.setDoOutput(true);
                        conn.setFixedLengthStreamingMode(body.length);
                        OutputStream os = conn.getOutputStream();
                        os.write(body);
                        os.flush();
                        os.close();
                    }
                    int status = conn.getResponseCode();
                    InputStream in;
                    try { in = conn.getInputStream(); }
                    catch (IOException e) { in = conn.getErrorStream(); }
                    ByteArrayOutputStream bos = new ByteArrayOutputStream();
                    if (in != null) {
                        byte[] buf = new byte[16384];
                        int n;
                        while ((n = in.read(buf)) > 0) bos.write(buf, 0, n);
                        in.close();
                    }
                    String b64 = Base64.encodeToString(bos.toByteArray(), Base64.NO_WRAP);

                    JSONObject outHeaders = new JSONObject();
                    for (Map.Entry<String, List<String>> e : conn.getHeaderFields().entrySet()) {
                        if (e.getKey() == null) continue;
                        String lk = e.getKey().toLowerCase();
                        if (lk.equals("content-type") || lk.equals("content-encoding") || lk.equals("www-authenticate")) {
                            outHeaders.put(lk, e.getValue().isEmpty() ? "" : e.getValue().get(0));
                        }
                    }
                    JSONObject out = new JSONObject();
                    out.put("status", status);
                    out.put("statusText", conn.getResponseMessage() == null ? "" : conn.getResponseMessage());
                    out.put("headers", outHeaders);
                    out.put("body", b64);
                    emit("gitHttp", ok(out.toString()));
                } catch (Exception e) {
                    emit("gitHttp", fail("网络请求失败：" + e.getMessage()));
                } finally {
                    if (conn != null) try { conn.disconnect(); } catch (Exception ignored) { }
                }
            } }).start();
        }
    }
}
