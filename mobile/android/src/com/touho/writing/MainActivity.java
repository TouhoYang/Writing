package com.touho.writing;

import android.app.Activity;
import android.content.ContentValues;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.provider.MediaStore;
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

import java.io.BufferedReader;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.nio.charset.Charset;

/**
 * 写作 · 安卓版外壳
 * 用 WebView 承载移动版界面(assets/index.html)，并提供原生能力：
 *   1) 导出 txt 到系统「下载」目录（MediaStore，无需存储权限）
 *   2) 导入 txt（系统文件选择器）
 */
public class MainActivity extends Activity {

    private WebView web;
    private ValueCallback<Uri[]> fileCallback;
    private static final int REQ_FILE = 1001;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

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

        web.setWebViewClient(new WebViewClient());
        web.addJavascriptInterface(new Bridge(), "MaziAndroid");
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
        super.onActivityResult(requestCode, resultCode, data);
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

    /** 提供给网页调用的原生能力 */
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
}
