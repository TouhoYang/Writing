using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Writing
{
    public class GitResult
    {
        public int ExitCode;
        public string Output = "";
        public bool Ok { get { return ExitCode == 0; } }
    }

    // 调用本机 git.exe 执行命令(不依赖第三方库)
    public static class GitRunner
    {
        static string _gitPath = null;

        public static string FindGit()
        {
            if (_gitPath != null) return _gitPath;
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathVar))
                {
                    string[] dirs = pathVar.Split(';');
                    for (int i = 0; i < dirs.Length; i++)
                    {
                        string d = dirs[i].Trim();
                        if (d.Length == 0) continue;
                        try
                        {
                            string p = Path.Combine(d, "git.exe");
                            if (File.Exists(p)) { _gitPath = p; return _gitPath; }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            string[] cands = new string[]
            {
                @"C:\Program Files\Git\cmd\git.exe",
                @"C:\Program Files (x86)\Git\cmd\git.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\cmd\git.exe")
            };
            for (int i = 0; i < cands.Length; i++)
            {
                try { if (File.Exists(cands[i])) { _gitPath = cands[i]; return _gitPath; } }
                catch { }
            }
            return null;
        }

        public static GitResult Run(string workDir, string args, string authHeader, int timeoutMs)
        {
            GitResult r = new GitResult();
            string git = FindGit();
            if (git == null)
            {
                r.ExitCode = -1;
                r.Output = "未找到 git.exe，请先安装 Git for Windows（https://git-scm.com）后重试。";
                return r;
            }
            string full = "-c core.quotepath=false " + args;
            if (!string.IsNullOrEmpty(authHeader))
            {
                full = "-c \"http.extraheader=" + authHeader + "\" " + full;
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = git;
            psi.Arguments = full;
            psi.WorkingDirectory = workDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            try
            {
                psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
                psi.EnvironmentVariables["LC_ALL"] = "C.UTF-8";
            }
            catch { }

            try
            {
                using (Process p = Process.Start(psi))
                {
                    StringBuilder sb = new StringBuilder();
                    object gate = new object();
                    DataReceivedEventHandler handler = delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) { lock (gate) { sb.AppendLine(e.Data); } }
                    };
                    p.OutputDataReceived += handler;
                    p.ErrorDataReceived += handler;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        r.ExitCode = -2;
                        r.Output = "命令执行超时（" + (timeoutMs / 1000) + " 秒）";
                        return r;
                    }
                    p.WaitForExit();
                    lock (gate) { r.Output = sb.ToString().Trim(); }
                    r.ExitCode = p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                r.ExitCode = -3;
                r.Output = "执行 git 失败：" + ex.Message;
            }
            return r;
        }

        public static bool IsRepo(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            GitResult r = Run(dir, "rev-parse --is-inside-work-tree", null, 15000);
            return r.Ok && r.Output.Trim().StartsWith("true", StringComparison.OrdinalIgnoreCase);
        }

        public static string RemoteUrl(string dir)
        {
            GitResult r = Run(dir, "remote get-url origin", null, 15000);
            return r.Ok ? r.Output.Trim() : "";
        }

        // https://github.com/user/repo.git -> github.com(非 https 远端返回 null,走 SSH)
        public static string HostOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            string rest = url.Substring("https://".Length);
            int slash = rest.IndexOf('/');
            string host = slash > 0 ? rest.Substring(0, slash) : rest;
            int at = host.IndexOf('@');
            if (at >= 0) host = host.Substring(at + 1);
            int colon = host.IndexOf(':');
            if (colon > 0) host = host.Substring(0, colon);
            return host.Length > 0 ? host : null;
        }

        public static string BasicHeader(string user, string token)
        {
            string u = string.IsNullOrEmpty(user) ? "git" : user;
            string raw = u + ":" + token;
            return "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        }

        // 令牌用 Windows DPAPI 按当前用户加密后存放,换机器需重新填写
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch
            {
                return "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
            }
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try
            {
                if (stored.StartsWith("plain:"))
                    return Encoding.UTF8.GetString(Convert.FromBase64String(stored.Substring("plain:".Length)));
                byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return ""; }
        }

        public static string Quote(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        // XY -> VS Code 风格的单个状态字母
        public static string StatusLetter(string xy)
        {
            if (string.IsNullOrEmpty(xy)) return "";
            if (xy.IndexOf('?') >= 0) return "U";
            if (xy.IndexOf('D') >= 0) return "D";
            if (xy.IndexOf('A') >= 0) return "A";
            if (xy.IndexOf('R') >= 0) return "R";
            if (xy.IndexOf('M') >= 0) return "M";
            return xy.Trim().Length > 0 ? xy.Trim().Substring(0, 1) : "";
        }
    }

    // Git 管理面板:多仓库 / 分支 / 更改 / 提交历史 / 拉取 / 提交 / 推送 / PAT 管理
    public class GitWindow : Window
    {
        MainWindow _owner;
        AppSettings _settings;

        ListBox _repoList;
        TextBlock _statusText, _commitInfo, _remoteText, _changesHeader, _historyHeader;
        ComboBox _branchBox, _tokenCombo;
        TextBox _msgBox, _logBox, _hostBox, _userBox, _cloneUrlBox;
        PasswordBox _tokenBox;
        ListBox _changesList, _historyList;
        Button _addBtn, _removeBtn, _treeBtn, _refreshBtn, _switchBtn, _pullBtn, _commitBtn, _pushBtn, _tokenSaveBtn, _tokenDelBtn;

        bool _busy = false;
        string _currentRepo = null;
        bool _tokenComboLoading = false;
        bool _autoSelecting = false;
        TextBlock _credState;

        public GitWindow(MainWindow owner)
        {
            _owner = owner;
            _settings = owner.Settings;

            try { Resources.MergedDictionaries.Add(UiTheme.Create()); }
            catch { }

            Title = "Git 管理";
            Width = 1020;
            Height = 780;
            MinWidth = 860;
            MinHeight = 620;
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Res("PageBg", Color.FromRgb(0xF6, 0xF7, 0xF9));
            FontFamily = new FontFamily("Microsoft YaHei UI");

            BuildUi();
            ReloadTokenCombo(null);
            ReloadRepoList(RepoOfCurrentFile());
            RefreshStatus();
        }

        Brush Res(string key, Color fallback)
        {
            try
            {
                Brush b = FindResource(key) as Brush;
                if (b != null) return b;
            }
            catch { }
            return new SolidColorBrush(fallback);
        }

        Brush StatusBrush(string letter)
        {
            if (letter == "M") return Res("GitM", Color.FromRgb(0x89, 0x55, 0x03));
            if (letter == "D") return Res("GitD", Color.FromRgb(0xAD, 0x07, 0x07));
            if (letter == "A" || letter == "U" || letter == "R") return Res("GitA", Color.FromRgb(0x58, 0x7C, 0x0C));
            return Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
        }

        TextBlock Label(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 12;
            t.Foreground = Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(0, 0, 6, 0);
            return t;
        }

        TextBlock SectionTitle(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 11;
            t.FontWeight = FontWeights.SemiBold;
            t.Foreground = Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
            t.Margin = new Thickness(0, 8, 0, 4);
            return t;
        }

        Button Btn(string text, RoutedEventHandler h)
        {
            Button b = new Button();
            b.Content = text;
            b.Padding = new Thickness(10, 5, 10, 5);
            b.Margin = new Thickness(0, 0, 6, 0);
            b.FontSize = 13;
            b.Click += h;
            return b;
        }

        Button PrimaryBtn(string text, RoutedEventHandler h)
        {
            Button b = Btn(text, h);
            Style st = null;
            try { st = FindResource("PrimaryButton") as Style; } catch { }
            if (st != null) b.Style = st;
            return b;
        }

        TextBox DarkInput(double width, double height)
        {
            TextBox t = new TextBox();
            t.Width = width;
            t.FontSize = 12;
            t.Padding = new Thickness(6, 4, 6, 4);
            t.Background = Res("InputBg", Color.FromRgb(0xE9, 0xEB, 0xEF));
            t.Foreground = Res("Fg", Color.FromRgb(0x23, 0x27, 0x2E));
            t.BorderBrush = Res("Line", Color.FromRgb(0xE9, 0xEB, 0xEF));
            t.BorderThickness = new Thickness(1);
            if (height > 0) t.Height = height;
            return t;
        }

        void BuildUi()
        {
            Grid root = new Grid();
            ColumnDefinition leftCol = new ColumnDefinition();
            leftCol.Width = new GridLength(240);
            root.ColumnDefinitions.Add(leftCol);
            ColumnDefinition rightCol = new ColumnDefinition();
            rightCol.Width = new GridLength(1, GridUnitType.Star);
            root.ColumnDefinitions.Add(rightCol);
            Content = root;

            // ---- 左:仓库列表 ----
            Border left = new Border();
            left.Background = Res("PanelBg", Color.FromRgb(0x25, 0x25, 0x26));
            left.BorderBrush = Res("Line", Color.FromRgb(0xE9, 0xEB, 0xEF));
            left.BorderThickness = new Thickness(0, 0, 1, 0);
            Grid.SetColumn(left, 0);
            root.Children.Add(left);

            DockPanel lp = new DockPanel();
            left.Child = lp;

            TextBlock lt = new TextBlock();
            lt.Text = "仓库";
            lt.FontSize = 11;
            lt.FontWeight = FontWeights.SemiBold;
            lt.Foreground = Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
            lt.Margin = new Thickness(14, 12, 14, 6);
            DockPanel.SetDock(lt, Dock.Top);
            lp.Children.Add(lt);

            StackPanel lb = new StackPanel();
            lb.Orientation = Orientation.Horizontal;
            lb.Margin = new Thickness(10, 0, 10, 10);
            DockPanel.SetDock(lb, Dock.Bottom);
            _addBtn = Btn("添加仓库", AddRepo_Click);
            _removeBtn = Btn("移除", RemoveRepo_Click);
            lb.Children.Add(_addBtn);
            lb.Children.Add(_removeBtn);
            lp.Children.Add(lb);

            StackPanel lb2 = new StackPanel();
            lb2.Margin = new Thickness(10, 0, 10, 8);
            DockPanel.SetDock(lb2, Dock.Bottom);
            _treeBtn = Btn("在目录树中打开", OpenInTree_Click);
            _treeBtn.HorizontalAlignment = HorizontalAlignment.Left;
            lb2.Children.Add(_treeBtn);
            lp.Children.Add(lb2);

            // 克隆:填仓库链接 → 选文件夹 → 拉取内容
            StackPanel lb3 = new StackPanel();
            lb3.Margin = new Thickness(10, 0, 10, 10);
            DockPanel.SetDock(lb3, Dock.Bottom);
            TextBlock cloneTitle = new TextBlock();
            cloneTitle.Text = "克隆仓库（填链接 → 选文件夹 → 拉取内容）";
            cloneTitle.FontSize = 12;
            cloneTitle.Foreground = Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
            cloneTitle.Margin = new Thickness(0, 0, 0, 6);
            cloneTitle.TextWrapping = TextWrapping.Wrap;
            lb3.Children.Add(cloneTitle);
            _cloneUrlBox = DarkInput(double.NaN, 0);
            _cloneUrlBox.FontSize = 12;
            AutomationProperties.SetName(_cloneUrlBox, "GitCloneUrl");
            lb3.Children.Add(_cloneUrlBox);
            StackPanel cloneBtns = new StackPanel();
            cloneBtns.Orientation = Orientation.Horizontal;
            cloneBtns.Margin = new Thickness(0, 6, 0, 0);
            Button cloneBtn = PrimaryBtn("选择文件夹并克隆", Clone_Click);
            cloneBtn.Margin = new Thickness(0);
            AutomationProperties.SetName(cloneBtn, "GitCloneButton");
            cloneBtns.Children.Add(cloneBtn);
            lb3.Children.Add(cloneBtns);
            lp.Children.Add(lb3);

            _repoList = new ListBox();
            _repoList.BorderThickness = new Thickness(0);
            _repoList.Background = Brushes.Transparent;
            _repoList.FontSize = 12;
            _repoList.Foreground = Res("Fg", Color.FromRgb(0x23, 0x27, 0x2E));
            _repoList.Padding = new Thickness(6, 0, 0, 0);
            _repoList.SelectionChanged += delegate { OnRepoChanged(); };
            AutomationProperties.SetName(_repoList, "GitRepoList");
            lp.Children.Add(_repoList);

            // ---- 右:操作区 ----
            Grid rp = new Grid();
            rp.Margin = new Thickness(14, 10, 14, 12);
            Grid.SetColumn(rp, 1);
            root.Children.Add(rp);
            rp.ColumnDefinitions.Add(new ColumnDefinition());

            int row = 0;
            // 顶部状态
            StackPanel head = new StackPanel();
            Grid.SetRow(head, row++);
            rp.RowDefinitions.Add(new RowDefinition());
            rp.RowDefinitions[rp.RowDefinitions.Count - 1].Height = GridLength.Auto;
            _statusText = new TextBlock();
            _statusText.FontSize = 14;
            _statusText.FontWeight = FontWeights.SemiBold;
            _statusText.Foreground = Res("Fg", Color.FromRgb(0x23, 0x27, 0x2E));
            _statusText.Text = "未选择仓库";
            head.Children.Add(_statusText);
            _commitInfo = new TextBlock();
            _commitInfo.FontSize = 12;
            _commitInfo.Foreground = Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
            _commitInfo.Margin = new Thickness(0, 4, 0, 0);
            _commitInfo.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(_commitInfo);
            _remoteText = new TextBlock();
            _remoteText.FontSize = 12;
            _remoteText.Foreground = Res("FgFaint", Color.FromRgb(0xA8, 0xAE, 0xB8));
            _remoteText.Margin = new Thickness(0, 2, 0, 0);
            _remoteText.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(_remoteText);
            rp.Children.Add(head);

            // 分支 + 操作
            StackPanel branchRow = new StackPanel();
            branchRow.Orientation = Orientation.Horizontal;
            branchRow.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(branchRow, row++);
            rp.RowDefinitions.Add(new RowDefinition());
            rp.RowDefinitions[rp.RowDefinitions.Count - 1].Height = GridLength.Auto;
            branchRow.Children.Add(Label("分支"));
            _branchBox = new ComboBox();
            _branchBox.Width = 240;
            _branchBox.FontSize = 13;
            AutomationProperties.SetName(_branchBox, "GitBranchBox");
            branchRow.Children.Add(_branchBox);
            _switchBtn = Btn("切换分支", SwitchBranch_Click);
            branchRow.Children.Add(_switchBtn);
            _refreshBtn = Btn("刷新", Refresh_Click);
            branchRow.Children.Add(_refreshBtn);
            rp.Children.Add(branchRow);

            StackPanel actRow = new StackPanel();
            actRow.Orientation = Orientation.Horizontal;
            actRow.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(actRow, row++);
            rp.RowDefinitions.Add(new RowDefinition());
            rp.RowDefinitions[rp.RowDefinitions.Count - 1].Height = GridLength.Auto;
            _pullBtn = Btn("拉取 (pull)", Pull_Click);
            _commitBtn = PrimaryBtn("提交 (commit)", Commit_Click);
            _pushBtn = Btn("提交并推送", CommitPush_Click);
            actRow.Children.Add(_pullBtn);
            actRow.Children.Add(_commitBtn);
            actRow.Children.Add(_pushBtn);
            rp.Children.Add(actRow);

            // 提交说明
            StackPanel msgRow = new StackPanel();
            Grid.SetRow(msgRow, row++);
            rp.RowDefinitions.Add(new RowDefinition());
            rp.RowDefinitions[rp.RowDefinitions.Count - 1].Height = GridLength.Auto;
            msgRow.Children.Add(SectionTitle("提交说明"));
            _msgBox = DarkInput(double.NaN, 52);
            _msgBox.AcceptsReturn = true;
            _msgBox.TextWrapping = TextWrapping.Wrap;
            _msgBox.FontSize = 13;
            AutomationProperties.SetName(_msgBox, "GitCommitMsg");
            msgRow.Children.Add(_msgBox);
            rp.Children.Add(msgRow);

            // 更改列表
            StackPanel chgRow = new StackPanel();
            Grid.SetRow(chgRow, row++);
            rp.RowDefinitions.Add(new RowDefinition());
            rp.RowDefinitions[rp.RowDefinitions.Count - 1].Height = new GridLength(1.2, GridUnitType.Star);
            _changesHeader = SectionTitle("更改");
            chgRow.Children.Add(_changesHeader);
            _changesList = new ListBox();
            _changesList.BorderThickness = new Thickness(0);
            _changesList.Background = Brushes.Transparent;
            _changesList.FontSize = 12;
            _changesList.Padding = new Thickness(0);
            AutomationProperties.SetName(_changesList, "GitChangesList");
            chgRow.Children.Add(_changesList);
            rp.Children.Add(chgRow);

            // 令牌
            StackPanel tokRow = new StackPanel();
            Grid.SetRow(tokRow, row++);
            rp.RowDefinitions.Add(new RowDefinition());
            rp.RowDefinitions[rp.RowDefinitions.Count - 1].Height = GridLength.Auto;
            tokRow.Children.Add(SectionTitle("访问令牌 (PAT)"));
            StackPanel t1 = new StackPanel();
            t1.Orientation = Orientation.Horizontal;
            t1.Children.Add(Label("已保存令牌"));
            _tokenCombo = new ComboBox();
            _tokenCombo.Width = 240;
            _tokenCombo.FontSize = 12;
            _tokenCombo.SelectionChanged += TokenCombo_Changed;
            AutomationProperties.SetName(_tokenCombo, "GitTokenCombo");
            t1.Children.Add(_tokenCombo);
            t1.Children.Add(Label("  主机"));
            _hostBox = DarkInput(140, 0);
            AutomationProperties.SetName(_hostBox, "GitHostBox");
            t1.Children.Add(_hostBox);
            t1.Children.Add(Label("  用户名"));
            _userBox = DarkInput(110, 0);
            AutomationProperties.SetName(_userBox, "GitUserBox");
            t1.Children.Add(_userBox);
            t1.Children.Add(Label("  令牌"));
            _tokenBox = new PasswordBox();
            _tokenBox.Width = 200;
            _tokenBox.FontSize = 12;
            _tokenBox.Padding = new Thickness(6, 4, 6, 4);
            _tokenBox.Background = Res("InputBg", Color.FromRgb(0xE9, 0xEB, 0xEF));
            _tokenBox.Foreground = Res("Fg", Color.FromRgb(0x23, 0x27, 0x2E));
            AutomationProperties.SetName(_tokenBox, "GitTokenBox");
            t1.Children.Add(_tokenBox);
            tokRow.Children.Add(t1);

            StackPanel t2 = new StackPanel();
            t2.Orientation = Orientation.Horizontal;
            t2.Margin = new Thickness(0, 6, 0, 0);
            _tokenSaveBtn = PrimaryBtn("确认（添加/更新令牌）", TokenSave_Click);
            _tokenDelBtn = Btn("删除令牌", TokenDelete_Click);
            t2.Children.Add(_tokenSaveBtn);
            t2.Children.Add(_tokenDelBtn);
            TextBlock hint = new TextBlock();
            hint.Text = "令牌按当前 Windows 用户加密存储，仅用于 https 远端";
            hint.FontSize = 11;
            hint.Foreground = Res("FgFaint", Color.FromRgb(0xA8, 0xAE, 0xB8));
            hint.VerticalAlignment = VerticalAlignment.Center;
            t2.Children.Add(hint);
            tokRow.Children.Add(t2);

            _credState = new TextBlock();
            _credState.Text = "";
            _credState.FontSize = 11;
            _credState.TextWrapping = TextWrapping.Wrap;
            _credState.Foreground = Res("AccentText", Color.FromRgb(0x2F, 0x6F, 0xEB));
            _credState.Margin = new Thickness(0, 5, 0, 0);
            AutomationProperties.SetName(_credState, "GitCredState");
            tokRow.Children.Add(_credState);
            rp.Children.Add(tokRow);

            // 提交历史
            StackPanel hisRow = new StackPanel();
            Grid.SetRow(hisRow, row++);
            rp.RowDefinitions.Add(new RowDefinition());
            rp.RowDefinitions[rp.RowDefinitions.Count - 1].Height = new GridLength(1.6, GridUnitType.Star);
            _historyHeader = SectionTitle("提交历史（更新列表）");
            hisRow.Children.Add(_historyHeader);
            _historyList = new ListBox();
            _historyList.BorderThickness = new Thickness(0);
            _historyList.Background = Brushes.Transparent;
            _historyList.FontSize = 12;
            _historyList.Padding = new Thickness(0);
            AutomationProperties.SetName(_historyList, "GitHistoryList");
            hisRow.Children.Add(_historyList);
            rp.Children.Add(hisRow);

            // 执行输出
            StackPanel logRow = new StackPanel();
            Grid.SetRow(logRow, row++);
            rp.RowDefinitions.Add(new RowDefinition());
            rp.RowDefinitions[rp.RowDefinitions.Count - 1].Height = new GridLength(1, GridUnitType.Star);
            logRow.Children.Add(SectionTitle("执行输出"));
            _logBox = new TextBox();
            _logBox.FontSize = 12;
            _logBox.IsReadOnly = true;
            _logBox.AcceptsReturn = true;
            _logBox.TextWrapping = TextWrapping.Wrap;
            _logBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _logBox.Background = Res("InputBg", Color.FromRgb(0xE9, 0xEB, 0xEF));
            _logBox.Foreground = Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
            _logBox.BorderBrush = Res("Line", Color.FromRgb(0xE9, 0xEB, 0xEF));
            _logBox.BorderThickness = new Thickness(1);
            _logBox.Padding = new Thickness(8, 6, 8, 6);
            _logBox.MinHeight = 90;
            AutomationProperties.SetName(_logBox, "GitLogBox");
            logRow.Children.Add(_logBox);
            rp.Children.Add(logRow);
        }

        // ---------- 仓库列表 ----------

        List<string> RepoPaths()
        {
            List<string> list = new List<string>();
            if (!string.IsNullOrEmpty(_settings.GitRepos))
            {
                foreach (string p in _settings.GitRepos.Split('|'))
                {
                    string t = p.Trim();
                    if (t.Length > 0 && !list.Contains(t)) list.Add(t);
                }
            }
            return list;
        }

        void SaveRepos(List<string> list)
        {
            _settings.GitRepos = string.Join("|", list.ToArray());
            _settings.Save();
        }

        void ReloadRepoList(string select)
        {
            _repoList.Items.Clear();
            foreach (string p in RepoPaths()) _repoList.Items.Add(p);
            int idx = -1;
            if (select != null)
            {
                for (int i = 0; i < _repoList.Items.Count; i++)
                {
                    if (string.Equals((string)_repoList.Items[i], select, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                }
            }
            if (idx < 0 && _repoList.Items.Count > 0) idx = 0;
            if (idx >= 0) _repoList.SelectedIndex = idx;
            else { _currentRepo = null; _statusText.Text = "未选择仓库"; }
        }

        string RepoOfCurrentFile()
        {
            string file = _owner.CurrentFile;
            if (string.IsNullOrEmpty(file)) return null;
            foreach (string repo in RepoPaths())
            {
                string prefix = repo.EndsWith("\\") ? repo : repo + "\\";
                if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return repo;
            }
            return null;
        }

        void OnRepoChanged()
        {
            _currentRepo = _repoList.SelectedItem as string;
            RefreshStatus();
        }

        void AddRepo_Click(object sender, RoutedEventArgs e)
        {
            using (System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择本地 git 仓库文件夹";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                string dir = dlg.SelectedPath;
                if (!GitRunner.IsRepo(dir))
                {
                    Log("[" + dir + "] 不是 git 仓库（其中没有 .git），已忽略。");
                    return;
                }
                List<string> list = RepoPaths();
                if (!list.Contains(dir)) list.Add(dir);
                SaveRepos(list);
                ReloadRepoList(dir);
                Log("已添加仓库: " + dir);
            }
        }

        // 克隆:仓库链接 → 选文件夹 → 拉取内容
        void Clone_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) { Log("已有操作正在执行，请稍候…"); return; }

            string url = _cloneUrlBox.Text.Trim();
            if (url.Length == 0)
            {
                Log("请先在左侧填写仓库链接，例如 https://github.com/用户名/仓库名.git");
                _cloneUrlBox.Focus();
                return;
            }

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("git@", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                Log("仓库链接格式无法识别，请使用 https://… 、git@… 或 ssh://…");
                return;
            }

            // 先看是否已经克隆过同一个远端，避免重复拉一份
            string existing = FindRepoByUrl(url);
            if (existing != null)
            {
                Log("该链接已经克隆过，直接切换到已有仓库: " + existing);
                ReloadRepoList(existing);
                RunOne("拉取", existing, "pull --ff-only", AuthForUrl(url), true);
                return;
            }

            string parent, folderName;
            using (System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择克隆到的文件夹（会被 git 用作仓库根目录）";
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                string picked = dlg.SelectedPath;

                bool exists = Directory.Exists(picked);
                if (exists)
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(picked).Length > 0)
                        {
                            Log("所选文件夹不是空的: " + picked);
                            Log("请选一个空文件夹（或新建一个），克隆需要自己创建仓库内容。");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("无法读取所选文件夹：" + ex.Message);
                        return;
                    }
                    // 空文件夹:删掉,c 让 git clone 自己创建,兼容各版本 git 对已存在目录的处理
                    try { Directory.Delete(picked, false); }
                    catch (Exception ex)
                    {
                        Log("无法清空空文件夹以用于克隆：" + ex.Message);
                        return;
                    }
                }

                parent = Path.GetDirectoryName(picked);
                folderName = Path.GetFileName(picked);
                if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(folderName))
                {
                    Log("请直接选择「某个文件夹」作为仓库根目录（不要选盘符根目录）。");
                    return;
                }
            }

            string target = Path.Combine(parent, folderName);
            Log("开始克隆 " + url + " → " + target);
            Log("提示: 私有仓库请先在下方保存对应主机的访问令牌。");
            _busy = true;
            SetBusy(true);
            string auth = AuthForUrl(url);

            ThreadPool.QueueUserWorkItem(delegate
            {
                // 在父目录里用裸仓库名克隆,这样 git 能自己创建目标目录
                GitResult r = GitRunner.Run(parent, "clone " + GitRunner.Quote(url) + " " + GitRunner.Quote(folderName), auth, 900000);
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (r.Output.Length > 0) Log(r.Output);
                    _busy = false;
                    SetBusy(false);
                    if (!r.Ok)
                    {
                        Log("[失败] 克隆（退出码 " + r.ExitCode + "）");
                        return;
                    }
                    Log("[完成] 克隆到 " + target);
                    List<string> list = RepoPaths();
                    if (!list.Contains(target)) list.Add(target);
                    SaveRepos(list);
                    ReloadRepoList(target);
                    RefreshStatus();
                    _owner.RefreshGitStatus();
                }));
            });
        }

        // 按远端地址找已添加的仓库
        string FindRepoByUrl(string url)
        {
            string want = NormalizeUrl(url);
            foreach (string repo in RepoPaths())
            {
                string have = GitRunner.RemoteUrl(repo);
                if (have.Length > 0 && NormalizeUrl(have) == want) return repo;
            }
            return null;
        }

        // 比较远端地址时忽略大小写、结尾的 / 与 .git
        static string NormalizeUrl(string url)
        {
            if (url == null) return "";
            string s = url.Trim().ToLowerInvariant();
            while (s.EndsWith("/")) s = s.Substring(0, s.Length - 1);
            if (s.EndsWith(".git")) s = s.Substring(0, s.Length - 4);
            while (s.EndsWith("/")) s = s.Substring(0, s.Length - 1);
            return s;
        }

        // 克隆时还没有仓库目录,只能按链接里的主机取令牌
        string AuthForUrl(string url)
        {
            string host = GitRunner.HostOf(url);
            if (host == null) return null;
            string u, tk;
            if (GetCred(host, out u, out tk))
            {
                Log("使用已保存的 " + host + " 访问令牌进行认证。");
                return GitRunner.BasicHeader(u, tk);
            }
            Log("提示: 未保存 " + host + " 的访问令牌，若远端需要认证请在下方添加 PAT 并「确认」。");
            return null;
        }

        void RemoveRepo_Click(object sender, RoutedEventArgs e)
        {
            string sel = _repoList.SelectedItem as string;
            if (sel == null) return;
            List<string> list = RepoPaths();
            list.Remove(sel);
            SaveRepos(list);
            ReloadRepoList(null);
            Log("已移除仓库: " + sel);
        }

        void OpenInTree_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRepo == null) return;
            _owner.OpenFolder(_currentRepo);
            Log("已在目录树中打开: " + _currentRepo);
        }

        // ---------- 状态刷新 ----------

        void Refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshStatus();
            _owner.RefreshGitStatus();
        }

        void RefreshStatus()
        {
            string repo = _currentRepo;
            if (string.IsNullOrEmpty(repo) || !Directory.Exists(repo))
            {
                _statusText.Text = "未选择仓库";
                _commitInfo.Text = "";
                _remoteText.Text = "";
                _changesList.Items.Clear();
                _historyList.Items.Clear();
                return;
            }
            _statusText.Text = repo + "   (读取中…)";
            ThreadPool.QueueUserWorkItem(delegate
            {
                GitResult st = GitRunner.Run(repo, "status --porcelain=v1 -b", null, 30000);
                GitResult lg = GitRunner.Run(repo, "log -1 --pretty=" + GitRunner.Quote("format:%h %s"), null, 30000);
                GitResult br = GitRunner.Run(repo, "branch --format=%(refname:short)", null, 30000);
                GitResult rm = GitRunner.Run(repo, "remote get-url origin", null, 30000);
                GitResult hs = GitRunner.Run(repo, "log -n 40 --pretty=" + GitRunner.Quote("format:%h%x1f%an%x1f%ad%x1f%s") + " --date=" + GitRunner.Quote("format:%Y-%m-%d"), null, 30000);
                string[] branches = br.Ok ? br.Output.Split('\n') : new string[0];

                // 解析更改列表
                List<string[]> changes = new List<string[]>();
                string branch = "";
                string extra = "";
                if (st.Ok)
                {
                    foreach (string raw in st.Output.Split('\n'))
                    {
                        string line = raw.TrimEnd('\r');
                        if (line.Length == 0) continue;
                        if (line.StartsWith("##"))
                        {
                            string info = line.Substring(2).Trim();
                            int bracket = info.IndexOf('[');
                            if (bracket > 0)
                            {
                                extra = "   " + info.Substring(bracket).Trim();
                                info = info.Substring(0, bracket).Trim();
                            }
                            int dots = info.IndexOf("...");
                            branch = dots > 0 ? info.Substring(0, dots).Trim() : info;
                        }
                        else if (line.Length > 3)
                        {
                            string xy = line.Substring(0, 2);
                            string path = line.Substring(3).Trim();
                            int arrow = path.IndexOf(" -> ");
                            if (arrow > 0) path = path.Substring(arrow + 4);
                            changes.Add(new string[] { GitRunner.StatusLetter(xy), path });
                        }
                    }
                }
                // 解析提交历史
                List<string[]> history = new List<string[]>();
                if (hs.Ok)
                {
                    foreach (string raw in hs.Output.Split('\n'))
                    {
                        string line = raw.TrimEnd('\r');
                        if (line.Length == 0) continue;
                        string[] parts = line.Split('\u001f');
                        if (parts.Length >= 4) history.Add(parts);
                        else if (parts.Length == 1) history.Add(new string[] { parts[0], "", "", "" });
                    }
                }

                string[] branchArr = branches;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (_currentRepo != repo) return;
                    _statusText.Text = "分支 " + (branch.Length > 0 ? branch : "?") + "   变更文件 " + changes.Count + " 个" + extra;
                    _commitInfo.Text = (lg.Ok && lg.Output.Length > 0) ? ("最近提交: " + lg.Output.Trim()) : "（暂无提交）";
                    string remote = rm.Ok ? rm.Output.Trim() : "";
                    if (remote.Length == 0) _remoteText.Text = "远端: （未配置 origin，推送前请先设置）";
                    else if (remote.StartsWith("git@") || remote.StartsWith("ssh://")) _remoteText.Text = "远端: " + remote + "   [SSH，使用系统密钥]";
                    else _remoteText.Text = "远端: " + remote;

                    // 更改列表(带状态字母与颜色)
                    _changesHeader.Text = "更改" + (changes.Count > 0 ? "  (" + changes.Count + ")" : "");
                    _changesList.Items.Clear();
                    for (int i = 0; i < changes.Count; i++)
                    {
                        Grid g = new Grid();
                        g.ColumnDefinitions.Add(new ColumnDefinition());
                        ColumnDefinition rc = new ColumnDefinition();
                        rc.Width = GridLength.Auto;
                        g.ColumnDefinitions.Add(rc);
                        TextBlock nm = new TextBlock();
                        nm.Text = changes[i][1];
                        nm.FontSize = 12;
                        nm.TextTrimming = TextTrimming.CharacterEllipsis;
                        nm.Foreground = StatusBrush(changes[i][0]);
                        Grid.SetColumn(nm, 0);
                        g.Children.Add(nm);
                        TextBlock stl = new TextBlock();
                        stl.Text = changes[i][0];
                        stl.FontSize = 12;
                        stl.FontWeight = FontWeights.SemiBold;
                        stl.Margin = new Thickness(8, 0, 2, 0);
                        stl.Foreground = StatusBrush(changes[i][0]);
                        Grid.SetColumn(stl, 1);
                        g.Children.Add(stl);
                        _changesList.Items.Add(g);
                    }

                    // 提交历史
                    _historyHeader.Text = "提交历史（更新列表）" + (history.Count > 0 ? "  (" + history.Count + ")" : "");
                    _historyList.Items.Clear();
                    for (int i = 0; i < history.Count; i++)
                    {
                        Grid g = new Grid();
                        g.ColumnDefinitions.Add(new ColumnDefinition());
                        ColumnDefinition hc = new ColumnDefinition();
                        hc.Width = GridLength.Auto;
                        g.ColumnDefinitions.Add(hc);
                        ColumnDefinition dc = new ColumnDefinition();
                        dc.Width = GridLength.Auto;
                        g.ColumnDefinitions.Add(dc);
                        TextBlock subj = new TextBlock();
                        subj.Text = history[i][3];
                        subj.FontSize = 12;
                        subj.TextTrimming = TextTrimming.CharacterEllipsis;
                        subj.Foreground = Res("Fg", Color.FromRgb(0x23, 0x27, 0x2E));
                        Grid.SetColumn(subj, 0);
                        g.Children.Add(subj);
                        TextBlock author = new TextBlock();
                        author.Text = history[i][1];
                        author.FontSize = 11;
                        author.Margin = new Thickness(10, 0, 0, 0);
                        author.Foreground = Res("FgFaint", Color.FromRgb(0xA8, 0xAE, 0xB8));
                        Grid.SetColumn(author, 1);
                        g.Children.Add(author);
                        TextBlock date = new TextBlock();
                        date.Text = history[i][2];
                        date.FontSize = 11;
                        date.Margin = new Thickness(10, 0, 0, 0);
                        date.Foreground = Res("FgFaint", Color.FromRgb(0xA8, 0xAE, 0xB8));
                        Grid.SetColumn(date, 2);
                        g.Children.Add(date);
                        _historyList.Items.Add(g);
                    }

                    string keep = _branchBox.SelectedItem as string;
                    _branchBox.Items.Clear();
                    for (int i = 0; i < branchArr.Length; i++)
                    {
                        string b = branchArr[i].Trim();
                        if (b.Length > 0) _branchBox.Items.Add(b);
                    }
                    if (keep != null && _branchBox.Items.Contains(keep)) _branchBox.SelectedItem = keep;
                    else if (branch.Length > 0 && _branchBox.Items.Contains(branch)) _branchBox.SelectedItem = branch;

                    string host = GitRunner.HostOf(remote);
                    AutoSelectToken(repo, host);
                }));
            });
        }

        // ---------- 令牌管理 ----------

        string TokenKey(string host, string user)
        {
            return host + "  ·  " + (user.Length > 0 ? user : "git");
        }

        void ReloadTokenCombo(string selectKey)
        {
            _tokenComboLoading = true;
            try
            {
                _tokenCombo.Items.Clear();
                List<string> keys = new List<string>();
                foreach (KeyValuePair<string, string> kv in _settings.GitCreds)
                {
                    int bar = kv.Value.IndexOf('|');
                    string user = bar >= 0 ? kv.Value.Substring(0, bar) : "";
                    keys.Add(TokenKey(kv.Key, user));
                }
                keys.Sort(StringComparer.CurrentCulture);
                foreach (string k in keys) _tokenCombo.Items.Add(k);
                if (selectKey != null)
                {
                    for (int i = 0; i < _tokenCombo.Items.Count; i++)
                    {
                        if (string.Equals((string)_tokenCombo.Items[i], selectKey, StringComparison.OrdinalIgnoreCase))
                        {
                            _tokenCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
            finally { _tokenComboLoading = false; }
        }

        void TokenCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_tokenComboLoading || _autoSelecting) return;
            string key = _tokenCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(key)) return;
            string host = key;
            int sep = key.IndexOf("  ·  ");
            if (sep > 0) host = key.Substring(0, sep);
            string v;
            if (_settings.GitCreds.TryGetValue(host, out v))
            {
                int bar = v.IndexOf('|');
                string user = bar >= 0 ? v.Substring(0, bar) : "";
                string token = bar >= 0 ? GitRunner.Unprotect(v.Substring(bar + 1)) : "";
                _hostBox.Text = host;
                _userBox.Text = user;
                _tokenBox.Password = token;
                Log("已载入 " + host + " 的令牌（用户名: " + (user.Length > 0 ? user : "git") + "）。");
                // 手动选择后:把「当前仓库 → 该令牌」的绑定记住
                if (!string.IsNullOrEmpty(_currentRepo))
                {
                    RememberToken(_currentRepo, host, user);
                    if (_credState != null)
                    {
                        _credState.Text = "已记住：本仓库使用 " + host + " · " + (user.Length > 0 ? user : "git") + "（下次进入自动载入）";
                    }
                    Log("已记住 " + Path.GetFileName(_currentRepo) + " → " + host + " · " + (user.Length > 0 ? user : "git"));
                }
            }
        }

        // ---------- 令牌与仓库的绑定记忆 ----------

        void RememberToken(string repo, string host, string user)
        {
            if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(host)) return;
            _settings.GitRepoTokens[repo] = host + "|" + user;
            _settings.Save();
        }

        // 把指定令牌填入输入框,并同步下拉选中项
        void ApplyCred(string host, string user)
        {
            string v;
            if (!_settings.GitCreds.TryGetValue(host, out v)) return;
            int bar = v.IndexOf('|');
            string storedUser = bar >= 0 ? v.Substring(0, bar) : "";
            string token = bar >= 0 ? GitRunner.Unprotect(v.Substring(bar + 1)) : "";
            _autoSelecting = true;
            try
            {
                _hostBox.Text = host;
                _userBox.Text = storedUser;
                _tokenBox.Password = token;
                string key = TokenKey(host, storedUser);
                for (int i = 0; i < _tokenCombo.Items.Count; i++)
                {
                    if (string.Equals((string)_tokenCombo.Items[i], key, StringComparison.OrdinalIgnoreCase))
                    {
                        _tokenCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            finally { _autoSelecting = false; }
        }

        // 进入仓库时自动载入令牌:①该仓库上次用的 ②只有一个令牌 ③远端主机匹配
        void AutoSelectToken(string repo, string remoteHost)
        {
            if (_credState != null) _credState.Text = "";
            string repoKey;
            if (_settings.GitRepoTokens.TryGetValue(repo, out repoKey) && !string.IsNullOrEmpty(repoKey))
            {
                int bar = repoKey.IndexOf('|');
                string host = bar >= 0 ? repoKey.Substring(0, bar) : repoKey;
                string user = bar >= 0 ? repoKey.Substring(bar + 1) : "";
                if (_settings.GitCreds.ContainsKey(host))
                {
                    ApplyCred(host, user);
                    string display = host + " · " + (user.Length > 0 ? user : "git");
                    if (_credState != null) _credState.Text = "已自动载入该仓库上次使用的令牌：" + display;
                    Log("已按仓库记忆自动载入 " + display);
                    return;
                }
            }
            if (_settings.GitCreds.Count == 1)
            {
                foreach (KeyValuePair<string, string> kv in _settings.GitCreds)
                {
                    int bar = kv.Value.IndexOf('|');
                    string user = bar >= 0 ? kv.Value.Substring(0, bar) : "";
                    ApplyCred(kv.Key, user);
                    string display = kv.Key + " · " + (user.Length > 0 ? user : "git");
                    if (_credState != null) _credState.Text = "已自动载入唯一令牌：" + display;
                    Log("已自动载入唯一令牌 " + display);
                    return;
                }
            }
            if (remoteHost != null && _settings.GitCreds.ContainsKey(remoteHost))
            {
                string v = _settings.GitCreds[remoteHost];
                int bar = v.IndexOf('|');
                string user = bar >= 0 ? v.Substring(0, bar) : "";
                ApplyCred(remoteHost, user);
                string display = remoteHost + " · " + (user.Length > 0 ? user : "git");
                if (_credState != null) _credState.Text = "已按远端主机自动载入：" + display;
                Log("已按远端主机自动载入 " + display);
                return;
            }
            // 没有可自动匹配的令牌:清空用户/令牌,主机名仍自动填好
            _autoSelecting = true;
            try
            {
                _userBox.Text = "";
                _tokenBox.Password = "";
                if (remoteHost != null) _hostBox.Text = remoteHost;
            }
            finally { _autoSelecting = false; }
            if (_settings.GitCreds.Count > 1 && _credState != null)
            {
                _credState.Text = "该仓库还没有绑定令牌：请在上方选择或填写后点「确认」，之后会自动记住。";
            }
        }

        void TokenSave_Click(object sender, RoutedEventArgs e)
        {
            string host = _hostBox.Text.Trim();
            if (host.Length == 0)
            {
                Log("请先填写主机名（如 github.com / gitee.com）。");
                return;
            }
            string user = _userBox.Text.Trim();
            string token = _tokenBox.Password;
            if (token.Length == 0)
            {
                Log("令牌为空，若要移除请点「删除令牌」。");
                return;
            }
            _settings.GitCreds[host] = user + "|" + GitRunner.Protect(token);
            _settings.Save();
            ReloadTokenCombo(TokenKey(host, user));
            // 保存即视为"本仓库使用该令牌",记住绑定
            if (!string.IsNullOrEmpty(_currentRepo))
            {
                RememberToken(_currentRepo, host, user);
                if (_credState != null)
                {
                    _credState.Text = "已记住：本仓库使用 " + host + " · " + (user.Length > 0 ? user : "git") + "（下次进入自动载入）";
                }
            }
            Log("已保存 " + host + " 的访问令牌（用户名: " + (user.Length > 0 ? user : "git") + "），已加密写入配置。");
        }

        void TokenDelete_Click(object sender, RoutedEventArgs e)
        {
            string host = _hostBox.Text.Trim();
            string key = _tokenCombo.SelectedItem as string;
            if (host.Length == 0 && key != null)
            {
                int sep = key.IndexOf("  ·  ");
                host = sep > 0 ? key.Substring(0, sep) : key;
            }
            if (host.Length == 0)
            {
                Log("请先在「已保存令牌」中选择要删除的令牌。");
                return;
            }
            if (_settings.GitCreds.ContainsKey(host))
            {
                _settings.GitCreds.Remove(host);
                // 清理指向该令牌的仓库绑定
                List<string> stale = new List<string>();
                foreach (KeyValuePair<string, string> kv in _settings.GitRepoTokens)
                {
                    int bar = kv.Value.IndexOf('|');
                    string h = bar >= 0 ? kv.Value.Substring(0, bar) : kv.Value;
                    if (string.Equals(h, host, StringComparison.OrdinalIgnoreCase)) stale.Add(kv.Key);
                }
                for (int i = 0; i < stale.Count; i++) _settings.GitRepoTokens.Remove(stale[i]);
                _settings.Save();
                ReloadTokenCombo(null);
                _hostBox.Text = "";
                _userBox.Text = "";
                _tokenBox.Password = "";
                if (_credState != null) _credState.Text = "";
                Log("已删除 " + host + " 的令牌（选项列表中不再显示；相关仓库绑定已一并清除）。");
                RefreshStatus();   // 重新按规则自动载入(例如只剩一个令牌时默认载入)
            }
            else
            {
                Log("未找到 " + host + " 的已保存令牌。");
            }
        }

        bool GetCred(string host, out string user, out string token)
        {
            user = "";
            token = "";
            string v;
            if (_settings.GitCreds.TryGetValue(host, out v) && !string.IsNullOrEmpty(v))
            {
                int bar = v.IndexOf('|');
                if (bar >= 0)
                {
                    user = v.Substring(0, bar);
                    token = GitRunner.Unprotect(v.Substring(bar + 1));
                    return token.Length > 0;
                }
            }
            return false;
        }

        string AuthFor(string repo)
        {
            string remote = GitRunner.RemoteUrl(repo);
            string host = GitRunner.HostOf(remote);
            if (host == null) return null;
            string u, tk;
            if (GetCred(host, out u, out tk))
            {
                Log("使用已保存的 " + host + " 访问令牌进行认证。");
                return GitRunner.BasicHeader(u, tk);
            }
            Log("提示: 未保存 " + host + " 的访问令牌，若远端需要认证请在下方添加 PAT 并「确认」。");
            return null;
        }

        // ---------- 操作 ----------

        void SwitchBranch_Click(object sender, RoutedEventArgs e)
        {
            string repo = _currentRepo;
            string branch = _branchBox.SelectedItem as string;
            if (repo == null || branch == null) return;
            RunOne("切换分支到 " + branch, repo, "checkout " + GitRunner.Quote(branch), null, false);
        }

        void Pull_Click(object sender, RoutedEventArgs e)
        {
            string repo = _currentRepo;
            if (repo == null) return;
            RunOne("拉取", repo, "pull --ff-only", AuthFor(repo), true);
        }

        void Commit_Click(object sender, RoutedEventArgs e)
        {
            DoCommit(false);
        }

        void CommitPush_Click(object sender, RoutedEventArgs e)
        {
            DoCommit(true);
        }

        void RunOne(string title, string repo, string args, string auth, bool reloadAfter)
        {
            if (_busy) { Log("已有操作正在执行，请稍候…"); return; }
            _busy = true;
            SetBusy(true);
            Log("> git " + args);
            ThreadPool.QueueUserWorkItem(delegate
            {
                GitResult r = GitRunner.Run(repo, args, auth, 180000);
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (r.Output.Length > 0) Log(r.Output);
                    Log(r.Ok ? ("[完成] " + title) : ("[失败] " + title + "（退出码 " + r.ExitCode + "）"));
                    _busy = false;
                    SetBusy(false);
                    RefreshStatus();
                    _owner.RefreshGitStatus();
                    if (reloadAfter && r.Ok) _owner.ReloadAfterGit(repo);
                }));
            });
        }

        void DoCommit(bool thenPush)
        {
            string repo = _currentRepo;
            if (repo == null) { Log("请先选择仓库。"); return; }
            string msg = _msgBox.Text.Trim();
            if (msg.Length == 0) { Log("请先填写提交说明。"); return; }
            if (_busy) { Log("已有操作正在执行，请稍候…"); return; }
            _busy = true;
            SetBusy(true);
            string auth = AuthFor(repo);

            string tmp = Path.Combine(Path.GetTempPath(), "writing_commit_" + DateTime.Now.Ticks + ".txt");
            try { File.WriteAllText(tmp, msg, new UTF8Encoding(false)); }
            catch (Exception ex) { Log("写入提交说明失败：" + ex.Message); _busy = false; SetBusy(false); return; }

            ThreadPool.QueueUserWorkItem(delegate
            {
                GitResult a = GitRunner.Run(repo, "add -A", null, 120000);
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (a.Output.Length > 0) Log(a.Output);
                    Log(a.Ok ? "[完成] 暂存所有改动 (git add -A)" : ("[失败] git add -A（退出码 " + a.ExitCode + "）"));
                }));

                GitResult c = GitRunner.Run(repo, "commit -F " + GitRunner.Quote(tmp), null, 120000);
                try { File.Delete(tmp); } catch { }

                GitResult p = null;
                if (thenPush && c.Ok) p = GitRunner.Run(repo, "push", auth, 180000);

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (c.Output.Length > 0) Log(c.Output);
                    Log(c.Ok ? "[完成] 提交" : ("[失败] 提交（退出码 " + c.ExitCode + "）"));
                    if (thenPush && c.Ok)
                    {
                        if (p != null && p.Output.Length > 0) Log(p.Output);
                        if (p != null && !p.Ok)
                        {
                            string br = _branchBox.SelectedItem as string;
                            if (br != null && (p.Output.IndexOf("upstream", StringComparison.OrdinalIgnoreCase) >= 0
                                || p.Output.IndexOf("no such ref", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                Log("检测到未设置上游分支，尝试 git push -u origin " + br + " …");
                                GitResult p2 = GitRunner.Run(repo, "push -u origin " + GitRunner.Quote(br), auth, 180000);
                                if (p2.Output.Length > 0) Log(p2.Output);
                                Log(p2.Ok ? "[完成] 推送（已建立上游）" : ("[失败] 推送（退出码 " + p2.ExitCode + "）"));
                            }
                            else Log("[失败] 推送（退出码 " + p.ExitCode + "）");
                        }
                        else Log("[完成] 推送");
                    }
                    _busy = false;
                    SetBusy(false);
                    RefreshStatus();
                    _owner.RefreshGitStatus();
                    if (c.Ok) _msgBox.Text = "";
                    if (thenPush && c.Ok && p != null && p.Ok) _owner.ReloadAfterGit(repo);
                }));
            });
        }

        void SetBusy(bool busy)
        {
            _addBtn.IsEnabled = !busy;
            _removeBtn.IsEnabled = !busy;
            _treeBtn.IsEnabled = !busy;
            _refreshBtn.IsEnabled = !busy;
            _switchBtn.IsEnabled = !busy;
            _pullBtn.IsEnabled = !busy;
            _commitBtn.IsEnabled = !busy;
            _pushBtn.IsEnabled = !busy;
            _tokenSaveBtn.IsEnabled = !busy;
            _tokenDelBtn.IsEnabled = !busy;
        }

        // 可从后台线程调用
        void Log(string s)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { Log(s); }));
                return;
            }
            _logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
            _logBox.ScrollToEnd();
        }
    }
}
