package com.touho.writing;

import android.content.ContentResolver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.database.Cursor;
import android.net.Uri;
import android.provider.DocumentsContract;
import android.util.Base64;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * 写作 · 安卓版的「文件夹 + Git 文件系统」桥。
 *
 * 手机不允许直接遍历磁盘,所以走 SAF(Storage Access Framework):
 * 用户用系统选择器挑一个目录,应用 takePersistableUriPermission 后即可长期读写该目录。
 *
 * 设计要点(与鸿蒙版 Index.ets 的 git* 语义保持一致,方便网页端共用一套适配器):
 *   - 相对路径以 '/'' 分隔,根目录 = 用户选定的 tree URI
 *   - 文本走 UTF-8;二进制用 "b64:" 前缀传 base64(javaScriptProxy / JS 桥都不保证二进制)
 *   - gitStat 返回 {"type":"file"|"dir","size":n,"mtimeMs":n}
 *
 * 注意:DocumentsContract 没有 rename,rename 用「复制到新名字 + 删旧」实现。
 */
class AndroidFs {

    private final Context ctx;
    private final ContentResolver cr;
    private static final String PREFS = "mazi_folder";
    private static final String KEY_URI = "rootUri";
    private static final String KEY_NAME = "rootName";

    /** 相对路径 -> documentId(在 readdir / mkdir 时填充) */
    private final Map<String, String> dirIds = new HashMap<String, String>();
    /** 相对路径 -> 是否为目录 */
    private final Map<String, Boolean> isDir = new HashMap<String, Boolean>();

    AndroidFs(Context ctx) {
        this.ctx = ctx;
        this.cr = ctx.getContentResolver();
    }

    // ==================== 根目录(用户授权) ====================

    void saveRoot(Uri treeUri, String name) {
        try {
            cr.takePersistableUriPermission(treeUri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        } catch (Exception ignored) { }
        SharedPreferences sp = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        sp.edit().putString(KEY_URI, treeUri.toString()).putString(KEY_NAME, name).apply();
    }

    Uri rootUri() {
        SharedPreferences sp = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        String s = sp.getString(KEY_URI, null);
        return (s == null || s.length() == 0) ? null : Uri.parse(s);
    }

    String rootName() {
        SharedPreferences sp = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        return sp.getString(KEY_NAME, "");
    }

    void clearRoot() {
        SharedPreferences sp = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        sp.edit().remove(KEY_URI).remove(KEY_NAME).apply();
        dirIds.clear();
        isDir.clear();
    }

    /** 根目录的 documentId,用作相对路径解析的起点 */
    private String rootDocId() {
        Uri root = rootUri();
        if (root == null) return null;
        try {
            return DocumentsContract.getTreeDocumentId(root);
        } catch (Exception e) {
            return null;
        }
    }

    private Uri childrenUri(String parentDocId) {
        return DocumentsContract.buildChildDocumentsUriUsingTree(rootUri(), parentDocId);
    }

    private Uri docUri(String docId) {
        return DocumentsContract.buildDocumentUriUsingTree(rootUri(), docId);
    }

    /** 把相对路径拆成 父目录相对路径 + 文件名 */
    private static String parentOf(String rel) {
        int i = rel.lastIndexOf('/');
        return i < 0 ? "" : rel.substring(0, i);
    }

    private static String nameOf(String rel) {
        int i = rel.lastIndexOf('/');
        return i < 0 ? rel : rel.substring(i + 1);
    }

    /**
     * 解析相对路径 -> documentId。
     * 优先查缓存;查不到就逐层 query 目录。找不到返回 null。
     */
    private String resolveId(String rel) {
        if (rel == null) rel = "";
        while (rel.startsWith("/")) rel = rel.substring(1);
        if (rel.length() == 0) return rootDocId();

        if (dirIds.containsKey(rel)) return dirIds.get(rel);

        String parentRel = parentOf(rel);
        String want = nameOf(rel);
        String parentId = resolveId(parentRel);
        if (parentId == null) return null;

        // 列一次父目录,填充缓存(顺带把兄弟也缓存起来,减少 query 次数)
        List<String[]> kids = listChildren(parentId);
        for (int i = 0; i < kids.size(); i++) {
            String nm = kids.get(i)[0];
            String id = kids.get(i)[1];
            String mime = kids.get(i)[2];
            String childRel = parentRel.length() == 0 ? nm : parentRel + "/" + nm;
            isDir.put(childRel, DocumentsContract.Document.MIME_TYPE_DIR.equals(mime));
            if (!isDir.get(childRel)) dirIds.put(childRel, id);
            if (nm.equals(want)) {
                if (isDir.get(childRel)) dirIds.put(childRel, id);
                return id;
            }
        }
        return null;
    }

    /** 列目录:返回 [name, documentId, mimeType] 列表 */
    private List<String[]> listChildren(String parentDocId) {
        List<String[]> out = new ArrayList<String[]>();
        Cursor c = null;
        try {
            c = cr.query(childrenUri(parentDocId), new String[]{
                    DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                    DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                    DocumentsContract.Document.COLUMN_MIME_TYPE,
            }, null, null, null);
            if (c == null) return out;
            while (c.moveToNext()) {
                out.add(new String[]{ c.getString(0), c.getString(1), c.getString(2) });
            }
        } catch (Exception ignored) {
        } finally {
            if (c != null) try { c.close(); } catch (Exception ignored) { }
        }
        return out;
    }

    /** 逐层确保目录存在(不存在就创建),返回其 documentId */
    private String ensureDir(String rel) {
        if (rel == null) rel = "";
        while (rel.startsWith("/")) rel = rel.substring(1);
        if (rel.length() == 0) return rootDocId();

        String cached = dirIds.get(rel);
        if (cached != null) return cached;

        String parentRel = parentOf(rel);
        String name = nameOf(rel);
        String parentId = ensureDir(parentRel);
        if (parentId == null) return null;

        // 先看是否已存在
        String found = null;
        List<String[]> kids = listChildren(parentId);
        for (int i = 0; i < kids.size(); i++) {
            if (kids.get(i)[0].equals(name)) {
                found = kids.get(i)[1];
                isDir.put(rel, DocumentsContract.Document.MIME_TYPE_DIR.equals(kids.get(i)[2]));
                break;
            }
        }
        if (found == null) {
            try {
                Uri created = DocumentsContract.createDocument(cr, docUri(parentId),
                        DocumentsContract.Document.MIME_TYPE_DIR, name);
                if (created == null) return null;
                found = DocumentsContract.getDocumentId(created);
                isDir.put(rel, Boolean.TRUE);
            } catch (Exception e) {
                return null;
            }
        }
        dirIds.put(rel, found);
        return found;
    }

    /** 在父目录里按名字找已存在的文件/目录,返回 documentId */
    private String findId(String rel) {
        String parentRel = parentOf(rel);
        String want = nameOf(rel);
        String parentId = ensureDir(parentRel);
        if (parentId == null) return null;
        List<String[]> kids = listChildren(parentId);
        for (int i = 0; i < kids.size(); i++) {
            if (kids.get(i)[0].equals(want)) {
                isDir.put(rel, DocumentsContract.Document.MIME_TYPE_DIR.equals(kids.get(i)[2]));
                return kids.get(i)[1];
            }
        }
        return null;
    }

    // ==================== git 文件系统接口 ====================

    /** 读文件:文本返回 UTF-8;二进制返回 "b64:..." ;不存在返回 null */
    synchronized String gitRead(String rel) {
        if (rootUri() == null || rel == null) return null;
        String id = findId(rel);
        if (id == null) return null;
        InputStream in = null;
        try {
            in = cr.openInputStream(docUri(id));
            if (in == null) return null;
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            byte[] buf = new byte[8192];
            int n;
            boolean binary = false;
            while ((n = in.read(buf)) > 0) {
                for (int i = 0; i < n; i++) if (buf[i] == 0) { binary = true; break; }
                bos.write(buf, 0, n);
                if (binary) break;
            }
            if (binary) {
                while ((n = in.read(buf)) > 0) bos.write(buf, 0, n);
                return "b64:" + Base64.encodeToString(bos.toByteArray(), Base64.NO_WRAP);
            }
            return new String(bos.toByteArray(), "UTF-8");
        } catch (Exception e) {
            return null;
        } finally {
            if (in != null) try { in.close(); } catch (Exception ignored) { }
        }
    }

    /** 写文件:data 为 UTF-8 文本或 "b64:...";目录不存在会创建。成功返回 true */
    synchronized boolean gitWrite(String rel, String data) {
        if (rootUri() == null || rel == null) return false;
        try {
            // 只在相对路径里真的有子目录时才补建父目录。
            // 不要对根目录文件去补建父目录 —— 根目录的上级是授权范围之外的目录。
            String relParent = parentOf(rel);
            String name = nameOf(rel);
            String parentId = ensureDir(relParent);
            if (parentId == null) return false;

            byte[] bytes;
            if (data != null && data.length() > 4 && data.startsWith("b64:")) {
                bytes = Base64.decode(data.substring(4), Base64.NO_WRAP);
            } else {
                bytes = (data == null ? "" : data).getBytes("UTF-8");
            }

            String id = findId(rel);
            Uri target;
            if (id == null) {
                target = DocumentsContract.createDocument(cr, docUri(parentId), "application/octet-stream", name);
                if (target == null) return false;
                    dirIds.remove(rel);
                isDir.put(rel, Boolean.FALSE);
            } else {
                target = docUri(id);
            }
            OutputStream os = cr.openOutputStream(target, "wt");
            if (os == null) return false;
            os.write(bytes);
            os.flush();
            os.close();
            return true;
        } catch (Exception e) {
            return false;
        }
    }

    synchronized boolean gitMkdir(String rel) {
        if (rootUri() == null || rel == null) return false;
        return ensureDir(rel) != null;
    }

    synchronized boolean gitUnlink(String rel) {
        if (rootUri() == null || rel == null) return false;
        try {
            String id = findId(rel);
            if (id == null) return false;
            boolean ok = DocumentsContract.deleteDocument(cr, docUri(id));
            dirIds.remove(rel);
            isDir.remove(rel);
            return ok;
        } catch (Exception e) {
            return false;
        }
    }

    synchronized boolean gitRmdir(String rel) {
        // SAF 的 deleteDocument 对空目录即删除;非空会失败
        return gitUnlink(rel);
    }

    /** 列目录:返回 JSON 数组(字符串) */
    synchronized String gitReaddir(String rel) {
        if (rootUri() == null) return "[]";
        if (rel == null) rel = "";
        while (rel.startsWith("/")) rel = rel.substring(1);
        String id = resolveId(rel);
        if (id == null) return "[]";
        List<String[]> kids = listChildren(id);
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < kids.size(); i++) {
            String nm = kids.get(i)[0];
            String cid = kids.get(i)[1];
            String mime = kids.get(i)[2];
            boolean d = DocumentsContract.Document.MIME_TYPE_DIR.equals(mime);
            String childRel = rel.length() == 0 ? nm : rel + "/" + nm;
            isDir.put(childRel, d);
            if (d) dirIds.put(childRel, cid);
            if (i > 0) sb.append(",");
            sb.append(jsonStr(nm));
        }
        sb.append("]");
        return sb.toString();
    }

    /** 文件属性:{"type":..,"size":..,"mtimeMs":..};不存在返回 null */
    synchronized String gitStat(String rel) {
        if (rootUri() == null || rel == null) return null;
        String clean = rel;
        while (clean.startsWith("/")) clean = clean.substring(1);
        if (clean.length() == 0) {
            return "{\"type\":\"dir\",\"size\":0,\"mtimeMs\":0}";
        }
        String id = findId(rel);
        if (id == null) return null;
        Cursor c = null;
        try {
            c = cr.query(docUri(id), new String[]{
                    DocumentsContract.Document.COLUMN_MIME_TYPE,
                    DocumentsContract.Document.COLUMN_SIZE,
                    DocumentsContract.Document.COLUMN_LAST_MODIFIED,
            }, null, null, null);
            if (c == null || !c.moveToFirst()) return null;
            String mime = c.getString(0);
            boolean d = DocumentsContract.Document.MIME_TYPE_DIR.equals(mime);
            long size = c.isNull(1) ? 0 : c.getLong(1);
            long mtime = c.isNull(2) ? 0 : c.getLong(2);
            return "{\"type\":\"" + (d ? "dir" : "file") + "\",\"size\":" + size + ",\"mtimeMs\":" + mtime + "}";
        } catch (Exception e) {
            return null;
        } finally {
            if (c != null) try { c.close(); } catch (Exception ignored) { }
        }
    }

    synchronized boolean gitExists(String rel) {
        if (rootUri() == null || rel == null) return false;
        String clean = rel;
        while (clean.startsWith("/")) clean = clean.substring(1);
        if (clean.length() == 0) return rootUri() != null;
        return findId(rel) != null;
    }

    /** 重命名:SAF 没有 rename,复制到新名字再删旧的 */
    synchronized boolean rename(String oldRel, String newName) {
        if (rootUri() == null || oldRel == null || newName == null || newName.length() == 0) return false;
        try {
            String newRel = parentOf(oldRel).length() == 0 ? newName : (parentOf(oldRel) + "/" + newName);
            boolean wasDir = Boolean.TRUE.equals(isDir.get(oldRel));
            if (wasDir) {
                // 目录:仅移动顶层不可行,这里只支持空目录改名
                boolean del = gitUnlink(oldRel);
                if (!del) return false;
                return gitMkdir(newRel);
            }
            String data = gitRead(oldRel);
            if (data == null) return false;
            if (!gitWrite(newRel, data)) return false;
            return gitUnlink(oldRel);
        } catch (Exception e) {
            return false;
        }
    }

    // ==================== 给「目录树」用的 JSON 列表 ====================

    /** 与鸿蒙 listDir 同构:[{name,path,isDir,size,mtime}] */
    synchronized String listDirJson(String rel) {
        if (rootUri() == null) return "[]";
        if (rel == null) rel = "";
        while (rel.startsWith("/")) rel = rel.substring(1);
        String id = resolveId(rel);
        if (id == null) return "[]";
        List<String[]> kids = listChildren(id);
        List<String> rows = new ArrayList<String>();
        for (int i = 0; i < kids.size(); i++) {
            String nm = kids.get(i)[0];
            String cid = kids.get(i)[1];
            String mime = kids.get(i)[2];
            boolean d = DocumentsContract.Document.MIME_TYPE_DIR.equals(mime);
            String childRel = rel.length() == 0 ? nm : rel + "/" + nm;
            isDir.put(childRel, d);
            if (d) dirIds.put(childRel, cid);
            long size = 0, mtime = 0;
            Cursor c = null;
            try {
                c = cr.query(docUri(cid), new String[]{
                        DocumentsContract.Document.COLUMN_SIZE,
                        DocumentsContract.Document.COLUMN_LAST_MODIFIED,
                }, null, null, null);
                if (c != null && c.moveToFirst()) {
                    size = c.isNull(0) ? 0 : c.getLong(0);
                    mtime = c.isNull(1) ? 0 : c.getLong(1);
                }
            } catch (Exception ignored) {
            } finally {
                if (c != null) try { c.close(); } catch (Exception ignored) { }
            }
            rows.add("{\"name\":" + jsonStr(nm)
                    + ",\"path\":" + jsonStr(childRel)
                    + ",\"isDir\":" + d
                    + ",\"size\":" + size
                    + ",\"mtime\":" + mtime + "}");
        }
        Collections.sort(rows);
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < rows.size(); i++) {
            if (i > 0) sb.append(",");
            sb.append(rows.get(i));
        }
        sb.append("]");
        return sb.toString();
    }

    private static String jsonStr(String s) {
        if (s == null) return "null";
        StringBuilder sb = new StringBuilder("\"");
        for (int i = 0; i < s.length(); i++) {
            char ch = s.charAt(i);
            switch (ch) {
                case '"':  sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\n': sb.append("\\n"); break;
                case '\r': sb.append("\\r"); break;
                case '\t': sb.append("\\t"); break;
                default:
                    if (ch < 0x20) sb.append(String.format("\\u%04x", (int) ch));
                    else sb.append(ch);
            }
        }
        return sb.append("\"").toString();
    }

    // ==================== 网页配置的持久化 ====================
    // 安卓的 WebView localStorage 通常是可靠的,但仍统一镜像到 SharedPreferences,
    // 这样两端行为一致,而且配置可被备份/检查。

    private static final String CFG = "mazi_config";

    /** 读取全部配置:{"键":"值"} 的 JSON 字符串 */
    synchronized String configLoad() {
        try {
            SharedPreferences sp = ctx.getSharedPreferences(CFG, Context.MODE_PRIVATE);
            StringBuilder sb = new StringBuilder("{");
            boolean first = true;
            for (Map.Entry<String, ?> e : sp.getAll().entrySet()) {
                Object v = e.getValue();
                if (!(v instanceof String)) continue;
                if (!first) sb.append(",");
                first = false;
                sb.append(jsonStr(e.getKey())).append(":").append(jsonStr((String) v));
            }
            return sb.append("}").toString();
        } catch (Exception e) {
            return "{}";
        }
    }

    synchronized boolean configSet(String key, String value) {
        if (key == null || key.length() == 0) return false;
        try {
            SharedPreferences sp = ctx.getSharedPreferences(CFG, Context.MODE_PRIVATE);
            sp.edit().putString(key, value == null ? "" : value).commit();
            return true;
        } catch (Exception e) {
            return false;
        }
    }

    synchronized boolean configRemove(String key) {
        if (key == null || key.length() == 0) return false;
        try {
            SharedPreferences sp = ctx.getSharedPreferences(CFG, Context.MODE_PRIVATE);
            sp.edit().remove(key).commit();
            return true;
        } catch (Exception e) {
            return false;
        }
    }
}
