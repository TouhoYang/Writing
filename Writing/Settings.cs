using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Writing
{
    // 简单键值配置,存于 Windows 通用配置目录:%APPDATA%\写作\Writing.ini
    public class AppSettings
    {
        public double FontSize = 18;
        public bool AutoIndent = true;
        public string BgColor = "#FFFFFF";
        public string LastFolder = "";
        public string TreeRoot = "";        // 用户选择的目录树根目录
        public string TreeExpanded = "";    // 已展开的目录(用 | 分隔)
        public string TreeSelected = "";    // 当前选中的目录
        public string GitRepos = "";        // Git 仓库列表(用 | 分隔)
        public Dictionary<string, string> GitCreds = new Dictionary<string, string>();   // 主机 -> 用户名|加密令牌
        public Dictionary<string, string> GitRepoTokens = new Dictionary<string, string>();  // 仓库路径 -> 主机|用户名(该仓库上次使用的令牌)
        public double WindowWidth = 1280;   // 默认 720P
        public double WindowHeight = 720;

        public void Load()
        {
            try
            {
                string path = null;
                bool legacy = false;
                if (File.Exists(ConfigPath())) path = ConfigPath();
                else if (File.Exists(OldNamePath()))
                {
                    path = OldNamePath();     // 改名前的 LiveTyperSettings.ini(同目录)
                    legacy = true;
                }
                else if (File.Exists(OldConfigPath()))
                {
                    path = OldConfigPath();   // 改名前的配置目录
                    legacy = true;
                }
                else if (File.Exists(LegacyPath()))
                {
                    path = LegacyPath();      // 更早的 exe 同目录配置
                    legacy = true;
                }
                if (path == null) return;
                Parse(File.ReadAllLines(path));
                if (legacy) MigrateLegacy(path);
            }
            catch { }
        }

        // 把旧位置配置迁移到通用配置目录,写入成功后删除旧文件
        void MigrateLegacy(string sourcePath)
        {
            Save();
            try
            {
                if (File.Exists(ConfigPath()) && !string.Equals(sourcePath, ConfigPath(), StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(sourcePath);
                }
            }
            catch { }
        }

        // 配置文件目录:%APPDATA%\写作
        public static string ConfigDir()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
            }
            return Path.Combine(baseDir, "写作");
        }

        // 改名前的配置目录:%APPDATA%\码字直播助手
        static string OldConfigPath()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
            }
            return Path.Combine(Path.Combine(baseDir, "码字直播助手"), "LiveTyperSettings.ini");
        }

        // 配置文件完整路径
        public static string ConfigPath()
        {
            return Path.Combine(ConfigDir(), "Writing.ini");
        }

        // 同目录下的旧文件名(改名前的 LiveTyperSettings.ini)
        static string OldNamePath()
        {
            return Path.Combine(ConfigDir(), "LiveTyperSettings.ini");
        }

        // 旧版位置(exe 同目录),仅用于迁移与极端情况下的回退写入
        static string LegacyPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LiveTyperSettings.ini");
        }

        public void Save()
        {
            List<string> lines = new List<string>();
            lines.Add("FontSize=" + FontSize.ToString(CultureInfo.InvariantCulture));
            lines.Add("AutoIndent=" + AutoIndent);
            lines.Add("BgColor=" + BgColor);
            lines.Add("LastFolder=" + LastFolder);
            lines.Add("TreeRoot=" + TreeRoot);
            lines.Add("TreeExpanded=" + TreeExpanded);
            lines.Add("TreeSelected=" + TreeSelected);
            lines.Add("WindowWidth=" + WindowWidth.ToString(CultureInfo.InvariantCulture));
            lines.Add("WindowHeight=" + WindowHeight.ToString(CultureInfo.InvariantCulture));
            lines.Add("GitRepos=" + GitRepos);
            foreach (KeyValuePair<string, string> kv in GitCreds)
            {
                lines.Add("GitCred_" + kv.Key + "=" + kv.Value);
            }
            foreach (KeyValuePair<string, string> kv in GitRepoTokens)
            {
                lines.Add("GitRepoToken_" + kv.Key + "=" + kv.Value);
            }
            string[] arr = lines.ToArray();
            try
            {
                Directory.CreateDirectory(ConfigDir());
                File.WriteAllLines(ConfigPath(), arr);
            }
            catch
            {
                // 极端情况(%APPDATA% 不可写)退回 exe 同目录
                try { File.WriteAllLines(LegacyPath(), arr); } catch { }
            }
        }

        void Parse(string[] lines)
        {
            foreach (string line in lines)
            {
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                string key = line.Substring(0, idx).Trim();
                string val = line.Substring(idx + 1).Trim();
                if (key == "FontSize")
                {
                    double d;
                    if (double.TryParse(val, out d)) FontSize = d;
                }
                else if (key == "AutoIndent")
                {
                    bool b;
                    if (bool.TryParse(val, out b)) AutoIndent = b;
                }
                else if (key == "BgColor") BgColor = val;
                else if (key == "LastFolder") LastFolder = val;
                else if (key == "TreeRoot") TreeRoot = val;
                else if (key == "TreeExpanded") TreeExpanded = val;
                else if (key == "TreeSelected") TreeSelected = val;
                else if (key == "GitRepos") GitRepos = val;
                else if (key.StartsWith("GitCred_")) GitCreds[key.Substring("GitCred_".Length)] = val;
                else if (key.StartsWith("GitRepoToken_")) GitRepoTokens[key.Substring("GitRepoToken_".Length)] = val;
                else if (key == "WindowWidth")
                {
                    double d;
                    if (double.TryParse(val, out d)) WindowWidth = d;
                }
                else if (key == "WindowHeight")
                {
                    double d;
                    if (double.TryParse(val, out d)) WindowHeight = d;
                }
            }
        }
    }
}
