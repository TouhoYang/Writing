using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Writing
{
    // 写作主窗口(纯代码构建 UI,无 XAML,兼容 C#5 编译)
    public class MainWindow : Window
    {
        const string IndentString = "　　";          // 自动缩进:两个全角空格
        const double MinFontSize = 12;
        const double MaxFontSize = 48;
        const string BgWhiteHex = "#FFFFFF";
        const string BgGreenHex = "#C7EDCC";        // 奶绿色(护眼豆沙绿)
        const string BgBlueHex = "#CCE8FF";         // 天蓝色

        // 控件
        TextBox _editor;
        Border _editorHost;
        Border _filePanel;
        TreeView _tree;
        string _treeRootPath = null;
        string _selectedDir = null;
        TextBlock _folderText;
        TextBlock _fontSizeText;
        TextBlock _fileNameText;
        TextBlock _speedText;
        TextBlock _sessionText;
        TextBlock _totalText;
        TextBlock _netText;
        TextBlock _encText;
        TextBlock _saveStateText;
        TextBlock _clockText;
        ToggleButton _indentToggle;
        ToggleButton _topmostToggle;
        ToggleButton _bgWhite;
        ToggleButton _bgGreen;
        ToggleButton _bgBlue;

        // 状态
        AppSettings _settings = new AppSettings();
        string _currentFile = null;
        string _fileEncoding = "utf-8";
        bool _dirty = false;
        bool _loading = false;
        bool _applyingIndent = false;
        bool _skipIndentOnce = false;
        string _currentFolder = null;
        TextBox _renameBox = null;
        TreeViewItem _renameNode = null;
        bool _renameIsNew = false;
        string _renameDir = null;
        int _sessionAdded = 0;
        int _typedSinceLastTick = 0;
        long _lastTypeTick = 0;
        double _speedEma = 0;

        // 全局键盘钩子:统计本程序前台时的按键次数
        Dictionary<int, int> _keyCounts = new Dictionary<int, int>();
        KeyStatsWindow _keyWin = null;
        GitWindow _gitWin = null;
        Dictionary<string, string> _gitStatuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string _gitRepoRoot = null;
        IntPtr _hookHandle = IntPtr.Zero;
        LowLevelKeyboardProc _hookProc = null;
        IntPtr _hwnd = IntPtr.Zero;
        uint _myPid = 0;

        DispatcherTimer _speedTimer;
        DispatcherTimer _clockTimer;
        DispatcherTimer _autoSaveTimer;
        DispatcherTimer _boundsTimer;
        DispatcherTimer _treeStateTimer;
        readonly HashSet<string> _expandedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 自绘滚动条(系统 ScrollBar 在长文本下滑块会被 Track 裁成几个像素)
        Canvas _scrollCanvas;
        Border _scrollThumb;
        ScrollViewer _contentScroll;
        bool _scrollDragging = false;
        double _scrollDragStartY = 0;
        double _scrollDragStartOffset = 0;
        const double MinThumbHeight = 40;

        public MainWindow()
        {
            try { Resources.MergedDictionaries.Add(UiTheme.Create()); }
            catch { }

            BuildUi();

            _settings.Load();
            ApplySettings();
            ApplyWindowSize();
            SizeChanged += Window_SizeChanged;
            _boundsTimer = new DispatcherTimer();
            _boundsTimer.Interval = TimeSpan.FromMilliseconds(600);
            _boundsTimer.Tick += delegate { SaveWindowBounds(); };

            // 目录树状态(展开层级、选中目录)延迟写盘,避免频繁保存
            _treeStateTimer = new DispatcherTimer();
            _treeStateTimer.Interval = TimeSpan.FromMilliseconds(500);
            _treeStateTimer.Tick += delegate { _treeStateTimer.Stop(); SaveTreeState(); };

            // 安装全局键盘钩子,统计本程序前台时的按键次数
            _myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            SourceInitialized += delegate { _hwnd = new WindowInteropHelper(this).Handle; };
            _hookProc = HookProc;
            try
            {
                _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
            }
            catch
            {
                _hookHandle = IntPtr.Zero;
            }

            // 码字速度:每 3 秒取一次样
            _speedTimer = new DispatcherTimer();
            _speedTimer.Interval = TimeSpan.FromSeconds(3);
            _speedTimer.Tick += SpeedTimer_Tick;
            _speedTimer.Start();

            // 时钟
            _clockTimer = new DispatcherTimer();
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += delegate { _clockText.Text = DateTime.Now.ToString("HH:mm:ss"); };
            _clockTimer.Start();

            // 自动保存:每 3 分钟
            _autoSaveTimer = new DispatcherTimer();
            _autoSaveTimer.Interval = TimeSpan.FromSeconds(180);
            _autoSaveTimer.Tick += delegate
            {
                if (_dirty && _currentFile != null) SaveToFile();
            };
            _autoSaveTimer.Start();

            // 恢复上次的目录树状态(用户选择的根目录 + 展开层级 + 选中目录)
            string lastRoot = _settings.TreeRoot;
            if (string.IsNullOrEmpty(lastRoot)) lastRoot = _settings.LastFolder;   // 兼容旧配置
            if (!string.IsNullOrEmpty(lastRoot) && Directory.Exists(lastRoot))
            {
                if (string.IsNullOrEmpty(_settings.TreeRoot))
                {
                    _settings.TreeRoot = lastRoot;   // 旧配置迁移为目录树根
                    _settings.Save();
                }
                OpenFolder(lastRoot, false);
                RestoreTreeState();
            }

            ContentRendered += delegate { _editor.Focus(); };

            UpdateTitle();
            UpdateStats();
            UpdateSpeed();
        }

        // ---------- UI 构建 ----------

        void BuildUi()
        {
            Title = "写作";
            Width = 1280;   // 默认 720P
            Height = 720;
            MinWidth = 860;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Res("PageBg", Color.FromRgb(0xF6, 0xF7, 0xF9));
            FontFamily = new FontFamily("Microsoft YaHei UI");
            AllowDrop = true;
            Drop += Window_Drop;
            DragOver += Window_DragOver;
            PreviewKeyDown += Window_PreviewKeyDown;
            Closing += Window_Closing;

            TryLoadIcon();

            DockPanel root = new DockPanel();
            root.LastChildFill = true;
            root.Children.Add(BuildToolbar());
            root.Children.Add(BuildSidebar());
            root.Children.Add(BuildStatusBar());
            root.Children.Add(BuildEditorArea());
            Content = root;
        }

        // 主题色查找(主题异常时回退到内置色)
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

        // ---------- 顶部工具栏 ----------

        FrameworkElement BuildToolbar()
        {
            Border bar = new Border();
            bar.Background = Res("ToolbarBg", Colors.White);
            bar.BorderBrush = Res("Line", Color.FromRgb(0xE9, 0xEB, 0xEF));
            bar.BorderThickness = new Thickness(0, 0, 0, 1);
            bar.Padding = new Thickness(10, 7, 14, 7);
            DockPanel.SetDock(bar, Dock.Top);

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinition fileCol = new ColumnDefinition();
            fileCol.Width = GridLength.Auto;
            g.ColumnDefinitions.Add(fileCol);
            bar.Child = g;

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(row, 0);
            g.Children.Add(row);

            row.Children.Add(MakeButton("打开文件", OpenFile_Click));
            row.Children.Add(MakeButton("打开文件夹", OpenFolder_Click));
            row.Children.Add(MakeButton("新建文档", NewDoc_Click));
            row.Children.Add(MakeButton("保存", Save_Click));
            row.Children.Add(MakeSeparator());

            _indentToggle = MakeToggle("自动缩进", IndentToggle_Click);
            row.Children.Add(_indentToggle);
            _topmostToggle = MakeToggle("置顶", TopmostToggle_Click);
            row.Children.Add(_topmostToggle);
            row.Children.Add(MakeButton("键盘统计", KeyStats_Click));
            row.Children.Add(MakeButton("Git", Git_Click));
            row.Children.Add(MakeButton("目录", TogglePanel_Click));
            row.Children.Add(MakeSeparator());

            row.Children.Add(MakeLabel("字号"));
            row.Children.Add(MakeButton("A-", FontMinus_Click));
            _fontSizeText = new TextBlock();
            _fontSizeText.Text = "18";
            _fontSizeText.FontSize = 13;
            _fontSizeText.FontWeight = FontWeights.SemiBold;
            _fontSizeText.Foreground = Res("Accent", Color.FromRgb(0x2F, 0x6F, 0xEB));
            _fontSizeText.Width = 30;
            _fontSizeText.TextAlignment = TextAlignment.Center;
            _fontSizeText.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(_fontSizeText);
            row.Children.Add(MakeButton("A+", FontPlus_Click));
            row.Children.Add(MakeSeparator());

            row.Children.Add(MakeLabel("背景"));
            _bgWhite = MakeSwatch("默认白", BgWhiteHex);
            _bgGreen = MakeSwatch("奶绿", BgGreenHex);
            _bgBlue = MakeSwatch("天蓝", BgBlueHex);
            row.Children.Add(_bgWhite);
            row.Children.Add(_bgGreen);
            row.Children.Add(_bgBlue);

            _fileNameText = new TextBlock();
            _fileNameText.Text = "";
            _fileNameText.FontSize = 12;
            _fileNameText.Foreground = Res("FgFaint", Color.FromRgb(0xA8, 0xAE, 0xB8));
            _fileNameText.VerticalAlignment = VerticalAlignment.Center;
            _fileNameText.Margin = new Thickness(18, 0, 2, 0);
            _fileNameText.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(_fileNameText, 1);
            g.Children.Add(_fileNameText);

            return bar;
        }

        // ---------- 左侧目录 ----------

        FrameworkElement BuildSidebar()
        {
            _filePanel = new Border();
            _filePanel.Width = 236;
            _filePanel.Background = Res("PanelBg", Colors.White);
            _filePanel.BorderBrush = Res("Line", Color.FromRgb(0xE9, 0xEB, 0xEF));
            _filePanel.BorderThickness = new Thickness(0, 0, 1, 0);
            DockPanel.SetDock(_filePanel, Dock.Left);

            DockPanel fp = new DockPanel();
            _filePanel.Child = fp;

            Grid head = new Grid();
            head.Margin = new Thickness(16, 12, 10, 2);
            head.ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinition btnCol = new ColumnDefinition();
            btnCol.Width = GridLength.Auto;
            head.ColumnDefinitions.Add(btnCol);
            DockPanel.SetDock(head, Dock.Top);

            TextBlock title = new TextBlock();
            title.Text = "目录";
            title.FontSize = 12;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
            title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 0);
            head.Children.Add(title);

            Button refreshBtn = new Button();
            refreshBtn.Content = "刷新";
            refreshBtn.FontSize = 12;
            refreshBtn.Padding = new Thickness(8, 3, 8, 3);
            refreshBtn.Foreground = Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
            refreshBtn.Click += RefreshTree_Click;
            Grid.SetColumn(refreshBtn, 1);
            head.Children.Add(refreshBtn);
            fp.Children.Add(head);

            _folderText = new TextBlock();
            _folderText.Text = "（未选择文件夹）";
            _folderText.FontSize = 11;
            _folderText.Foreground = Res("FgFaint", Color.FromRgb(0xA8, 0xAE, 0xB8));
            _folderText.Margin = new Thickness(16, 0, 12, 8);
            _folderText.TextTrimming = TextTrimming.CharacterEllipsis;
            DockPanel.SetDock(_folderText, Dock.Top);
            fp.Children.Add(_folderText);

            _tree = new TreeView();
            _tree.BorderThickness = new Thickness(0);
            _tree.Background = Brushes.Transparent;
            _tree.FontSize = 13;
            _tree.Padding = new Thickness(8, 0, 0, 10);
            _tree.MouseDoubleClick += Tree_MouseDoubleClick;
            fp.Children.Add(_tree);

            return _filePanel;
        }

        // ---------- 底部状态栏 ----------

        FrameworkElement BuildStatusBar()
        {
            Border bar = new Border();
            bar.Background = Res("StatusBg", Colors.White);
            bar.BorderBrush = Res("Line", Color.FromRgb(0xE9, 0xEB, 0xEF));
            bar.BorderThickness = new Thickness(0, 1, 0, 0);
            bar.Padding = new Thickness(18, 8, 18, 8);
            DockPanel.SetDock(bar, Dock.Bottom);

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinition clockCol = new ColumnDefinition();
            clockCol.Width = GridLength.Auto;
            g.ColumnDefinitions.Add(clockCol);
            bar.Child = g;

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(row, 0);
            g.Children.Add(row);

            row.Children.Add(StatLabel("码字速度"));
            _speedText = StatValue("0");
            _speedText.Foreground = Res("Accent", Color.FromRgb(0x2F, 0x6F, 0xEB));
            _speedText.FontSize = 16;
            row.Children.Add(_speedText);
            row.Children.Add(StatUnit("字/分"));
            row.Children.Add(StatNote("3 秒采样"));
            row.Children.Add(StatDot());

            row.Children.Add(StatLabel("累计已码"));
            _sessionText = StatValue("0");
            row.Children.Add(_sessionText);
            row.Children.Add(StatUnit("字"));
            row.Children.Add(StatDot());

            row.Children.Add(StatLabel("文档字数"));
            _totalText = StatValue("0");
            row.Children.Add(_totalText);
            row.Children.Add(StatDot());

            row.Children.Add(StatLabel("净字数"));
            _netText = StatValue("0");
            row.Children.Add(_netText);
            row.Children.Add(StatDot());

            row.Children.Add(StatLabel("编码"));
            _encText = StatNote("UTF-8");
            row.Children.Add(_encText);
            row.Children.Add(StatDot());
            _saveStateText = StatNote("就绪");
            row.Children.Add(_saveStateText);

            _clockText = StatNote("00:00:00");
            _clockText.Margin = new Thickness(0);
            _clockText.Foreground = Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
            Grid.SetColumn(_clockText, 1);
            g.Children.Add(_clockText);

            return bar;
        }

        // ---------- 编辑区 ----------

        FrameworkElement BuildEditorArea()
        {
            _editorHost = new Border();
            _editorHost.Background = Res("White", Colors.White);
            _editorHost.CornerRadius = new CornerRadius(10);
            _editorHost.BorderBrush = Res("Line", Color.FromRgb(0xE9, 0xEB, 0xEF));
            _editorHost.BorderThickness = new Thickness(1);
            _editorHost.Margin = new Thickness(12, 12, 14, 12);

            Grid host = new Grid();
            _editorHost.Child = host;

            _editor = new TextBox();
            _editor.AcceptsReturn = true;
            _editor.TextWrapping = TextWrapping.Wrap;
            _editor.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;   // 改用自绘滚动条
            _editor.BorderThickness = new Thickness(0);
            _editor.Background = Brushes.Transparent;
            _editor.Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x28, 0x2E));
            _editor.FontFamily = new FontFamily("Microsoft YaHei UI");
            _editor.FontSize = 18;
            _editor.Padding = new Thickness(40, 32, 32, 32);
            _editor.UndoLimit = 1000;
            _editor.SpellCheck.IsEnabled = false;
            _editor.TextChanged += Editor_TextChanged;
            _editor.PreviewKeyDown += Editor_PreviewKeyDown;
            _editor.PreviewMouseWheel += Editor_PreviewMouseWheel;
            _editor.PreviewTextInput += Editor_PreviewTextInput;
            host.Children.Add(_editor);

            // 自绘滚动条:细长圆角滑条,最小长度 40px,可拖动、可点头跳转
            _scrollCanvas = new Canvas();
            _scrollCanvas.Background = Brushes.Transparent;
            _scrollCanvas.Width = 16;
            _scrollCanvas.HorizontalAlignment = HorizontalAlignment.Right;
            _scrollCanvas.Margin = new Thickness(0, 12, 5, 12);
            _scrollCanvas.MouseLeftButtonDown += ScrollTrack_MouseDown;

            _scrollThumb = new Border();
            _scrollThumb.Width = 6;
            _scrollThumb.CornerRadius = new CornerRadius(3);
            _scrollThumb.Background = new SolidColorBrush(Color.FromArgb(0x59, 0x10, 0x14, 0x1A));
            _scrollThumb.Cursor = Cursors.SizeNS;
            _scrollThumb.Visibility = Visibility.Collapsed;
            _scrollThumb.MouseLeftButtonDown += ScrollThumb_MouseDown;
            _scrollThumb.MouseMove += ScrollThumb_MouseMove;
            _scrollThumb.MouseLeftButtonUp += ScrollThumb_MouseUp;
            _scrollCanvas.Children.Add(_scrollThumb);
            host.Children.Add(_scrollCanvas);

            _editorHost.SizeChanged += delegate { UpdateScrollIndicator(); };
            host.Loaded += delegate
            {
                _contentScroll = _editor.Template.FindName("PART_ContentHost", _editor) as ScrollViewer;
                if (_contentScroll != null)
                {
                    _contentScroll.ScrollChanged += delegate { UpdateScrollIndicator(); };
                }
                UpdateScrollIndicator();
            };

            return _editorHost;
        }

        // 依据 ScrollViewer 的视口/内容比例更新自绘滚动条
        void UpdateScrollIndicator()
        {
            if (_contentScroll == null || _scrollThumb == null || _scrollCanvas == null) return;
            double trackH = _scrollCanvas.ActualHeight;
            double extent = _contentScroll.ExtentHeight;
            double viewport = _contentScroll.ViewportHeight;
            if (trackH <= 0 || extent <= viewport + 0.5)
            {
                _scrollThumb.Visibility = Visibility.Collapsed;
                return;
            }
            double thumbH = Math.Max(MinThumbHeight, trackH * (viewport / extent));
            if (thumbH > trackH) thumbH = trackH;
            double maxOffset = extent - viewport;
            double top = maxOffset > 0 ? (trackH - thumbH) * (_contentScroll.VerticalOffset / maxOffset) : 0;
            if (top < 0) top = 0;
            if (top > trackH - thumbH) top = trackH - thumbH;
            _scrollThumb.Height = thumbH;
            _scrollThumb.Visibility = Visibility.Visible;
            Canvas.SetTop(_scrollThumb, top);
            Canvas.SetLeft(_scrollThumb, (_scrollCanvas.ActualWidth - _scrollThumb.Width) / 2);
        }

        void ScrollThumb_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_contentScroll == null) return;
            _scrollDragging = true;
            _scrollDragStartY = e.GetPosition(_scrollCanvas).Y;
            _scrollDragStartOffset = _contentScroll.VerticalOffset;
            _scrollThumb.CaptureMouse();
            e.Handled = true;
        }

        void ScrollThumb_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_scrollDragging || _contentScroll == null) return;
            double trackH = _scrollCanvas.ActualHeight;
            double thumbH = _scrollThumb.Height;
            double maxOffset = _contentScroll.ExtentHeight - _contentScroll.ViewportHeight;
            double range = trackH - thumbH;
            if (range <= 0 || maxOffset <= 0) return;
            double delta = e.GetPosition(_scrollCanvas).Y - _scrollDragStartY;
            double offset = _scrollDragStartOffset + delta / range * maxOffset;
            if (offset < 0) offset = 0;
            if (offset > maxOffset) offset = maxOffset;
            _contentScroll.ScrollToVerticalOffset(offset);
            e.Handled = true;
        }

        void ScrollThumb_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_scrollDragging) return;
            _scrollDragging = false;
            _scrollThumb.ReleaseMouseCapture();
            e.Handled = true;
        }

        void ScrollTrack_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_contentScroll == null || _scrollThumb.Visibility != Visibility.Visible) return;
            double trackH = _scrollCanvas.ActualHeight;
            double thumbH = _scrollThumb.Height;
            double maxOffset = _contentScroll.ExtentHeight - _contentScroll.ViewportHeight;
            double range = trackH - thumbH;
            if (range <= 0 || maxOffset <= 0) return;
            double y = e.GetPosition(_scrollCanvas).Y - thumbH / 2;
            if (y < 0) y = 0;
            if (y > range) y = range;
            _contentScroll.ScrollToVerticalOffset(y / range * maxOffset);
            e.Handled = true;
        }

        // ---------- 控件工厂 ----------

        Border MakeSeparator()
        {
            Border b = new Border();
            b.Width = 1;
            b.Height = 16;
            b.Margin = new Thickness(9, 0, 9, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Background = Res("Line", Color.FromRgb(0xE9, 0xEB, 0xEF));
            return b;
        }

        Button MakeButton(string text, RoutedEventHandler handler)
        {
            Button b = new Button();
            b.Content = text;
            b.Padding = new Thickness(9, 5, 9, 5);
            b.Margin = new Thickness(0, 0, 2, 0);
            b.Click += handler;
            b.VerticalAlignment = VerticalAlignment.Center;
            return b;
        }

        ToggleButton MakeToggle(string text, RoutedEventHandler handler)
        {
            ToggleButton b = new ToggleButton();
            b.Content = text;
            b.Padding = new Thickness(9, 5, 9, 5);
            b.Margin = new Thickness(0, 0, 2, 0);
            b.Click += handler;
            b.VerticalAlignment = VerticalAlignment.Center;
            return b;
        }

        ToggleButton MakeSwatch(string tip, string hex)
        {
            ToggleButton b = new ToggleButton();
            b.ToolTip = tip;
            b.Margin = new Thickness(0, 0, 6, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            try
            {
                Style st = FindResource("Swatch") as Style;
                if (st != null) b.Style = st;
            }
            catch { }
            try
            {
                BrushConverter conv = new BrushConverter();
                b.Background = (Brush)conv.ConvertFromString(hex);
            }
            catch { }
            b.Click += Bg_Click;
            return b;
        }

        TextBlock MakeLabel(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 12;
            t.Foreground = Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(7, 0, 6, 0);
            return t;
        }

        TextBlock StatLabel(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 11;
            t.Foreground = Res("FgFaint", Color.FromRgb(0xA8, 0xAE, 0xB8));
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(0, 0, 5, 0);
            return t;
        }

        TextBlock StatValue(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 15;
            t.FontWeight = FontWeights.SemiBold;
            t.Foreground = Res("Fg", Color.FromRgb(0x23, 0x27, 0x2E));
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(0, 0, 3, 0);
            return t;
        }

        TextBlock StatUnit(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 11;
            t.Foreground = Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(0, 0, 14, 0);
            return t;
        }

        TextBlock StatNote(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 11;
            t.Foreground = Res("FgFaint", Color.FromRgb(0xA8, 0xAE, 0xB8));
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(0, 0, 14, 0);
            return t;
        }

        TextBlock StatDot()
        {
            TextBlock t = new TextBlock();
            t.Text = "·";
            t.FontSize = 12;
            t.Foreground = Res("FgFaint", Color.FromRgb(0xC9, 0xCE, 0xD6));
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(0, 0, 14, 0);
            return t;
        }

        void TryLoadIcon()
        {
            try
            {
                StreamResourceInfo sri = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
                if (sri != null)
                {
                    Icon = BitmapFrame.Create(sri.Stream);
                    sri.Stream.Close();
                }
            }
            catch { }
        }

        // ---------- 设置与外观 ----------

        void ApplySettings()
        {
            double fs = _settings.FontSize;
            if (fs < MinFontSize) fs = MinFontSize;
            if (fs > MaxFontSize) fs = MaxFontSize;
            _editor.FontSize = fs;
            _fontSizeText.Text = ((int)fs).ToString();
            ApplyFontMetrics();
            _indentToggle.IsChecked = _settings.AutoIndent;
            SetBg(_settings.BgColor);
        }

        void ApplyFontMetrics()
        {
            TextBlock.SetLineHeight(_editor, _editor.FontSize * 1.7);
            TextBlock.SetLineStackingStrategy(_editor, LineStackingStrategy.BlockLineHeight);
        }

        // 应用记忆的窗口大小(默认 1280x720),并限制在屏幕工作区内
        void ApplyWindowSize()
        {
            double w = _settings.WindowWidth;
            double h = _settings.WindowHeight;
            if (w < MinWidth) w = MinWidth;
            if (h < MinHeight) h = MinHeight;
            try
            {
                double maxW = SystemParameters.WorkArea.Width;
                double maxH = SystemParameters.WorkArea.Height;
                if (w > maxW) w = maxW;
                if (h > maxH) h = maxH;
            }
            catch { }
            Width = w;
            Height = h;
        }

        void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_boundsTimer == null) return;
            _boundsTimer.Stop();
            _boundsTimer.Start();   // 拖拽结束后 600ms 写入配置
        }

        void SaveWindowBounds()
        {
            if (_boundsTimer != null) _boundsTimer.Stop();
            if (WindowState == WindowState.Maximized)
            {
                // 最大化时记住还原尺寸
                _settings.WindowWidth = RestoreBounds.Width;
                _settings.WindowHeight = RestoreBounds.Height;
            }
            else if (WindowState == WindowState.Normal)
            {
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
            }
            _settings.Save();
        }

        void SetBg(string hex)
        {
            try
            {
                BrushConverter conv = new BrushConverter();
                _editorHost.Background = (Brush)conv.ConvertFromString(hex);
                _settings.BgColor = hex;
            }
            catch
            {
                _editorHost.Background = Brushes.White;
                _settings.BgColor = BgWhiteHex;
            }
            _bgWhite.IsChecked = _settings.BgColor == BgWhiteHex;
            _bgGreen.IsChecked = _settings.BgColor == BgGreenHex;
            _bgBlue.IsChecked = _settings.BgColor == BgBlueHex;
        }

        void ChangeFontSize(double delta)
        {
            double fs = _editor.FontSize + delta;
            if (fs < MinFontSize) fs = MinFontSize;
            if (fs > MaxFontSize) fs = MaxFontSize;
            _editor.FontSize = fs;
            _fontSizeText.Text = ((int)fs).ToString();
            ApplyFontMetrics();
            _settings.FontSize = fs;
            _settings.Save();
        }

        // ---------- 工具栏事件 ----------

        void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
            if (dlg.ShowDialog(this) == true) LoadFile(dlg.FileName);
        }

        void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            using (System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择小说文件夹";
                dlg.ShowNewFolderButton = true;
                if (!string.IsNullOrEmpty(_currentFolder) && Directory.Exists(_currentFolder))
                {
                    dlg.SelectedPath = _currentFolder;
                }
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    OpenFolder(dlg.SelectedPath);
                }
            }
        }

        void NewDoc_Click(object sender, RoutedEventArgs e)
        {
            string dir = null;
            if (!string.IsNullOrEmpty(_selectedDir) && Directory.Exists(_selectedDir)) dir = _selectedDir;
            else if (!string.IsNullOrEmpty(_currentFolder) && Directory.Exists(_currentFolder)) dir = _currentFolder;

            if (dir == null)
            {
                // 未选目录:走保存对话框
                SaveFileDialog dlg = new SaveFileDialog();
                dlg.Filter = "文本文件 (*.txt)|*.txt";
                dlg.FileName = "新建文档.txt";
                if (dlg.ShowDialog(this) != true) return;
                try
                {
                    File.WriteAllText(dlg.FileName, "", new System.Text.UTF8Encoding(true));
                    LoadFile(dlg.FileName);
                }
                catch (Exception ex)
                {
                    ShowError("新建失败：" + ex.Message);
                }
                return;
            }

            // 在目录树中定位并展开目标目录
            TreeViewItem dirNode = null;
            if (_treeRootPath != null && IsUnderRoot(dir))
            {
                dirNode = ExpandTo(dir);
            }
            else
            {
                OpenFolder(dir);
                dirNode = _tree.Items.Count > 0 ? _tree.Items[0] as TreeViewItem : null;
            }
            if (dirNode == null)
            {
                ShowError("无法在目录树中定位目标目录");
                return;
            }

            // 移除"（空）"等提示节点,追加临时文件节点并进入重命名编辑
            StripHintNodes(dirNode);
            TreeViewItem temp = MakeFileNode(Path.Combine(dir, "新建文档.txt"));
            dirNode.Items.Add(temp);
            StartFileRename(temp, true, dir);
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            SaveToFile();
        }

        void TogglePanel_Click(object sender, RoutedEventArgs e)
        {
            _filePanel.Visibility = _filePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        void FontPlus_Click(object sender, RoutedEventArgs e)
        {
            ChangeFontSize(2);
        }

        void FontMinus_Click(object sender, RoutedEventArgs e)
        {
            ChangeFontSize(-2);
        }

        void IndentToggle_Click(object sender, RoutedEventArgs e)
        {
            _settings.AutoIndent = _indentToggle.IsChecked == true;
            _settings.Save();
        }

        void TopmostToggle_Click(object sender, RoutedEventArgs e)
        {
            Topmost = _topmostToggle.IsChecked == true;
        }

        void Bg_Click(object sender, RoutedEventArgs e)
        {
            ToggleButton b = sender as ToggleButton;
            if (b == _bgWhite) SetBg(BgWhiteHex);
            else if (b == _bgGreen) SetBg(BgGreenHex);
            else if (b == _bgBlue) SetBg(BgBlueHex);
            _settings.Save();
        }

        // ---------- 文件操作 ----------

        public void OpenFolder(string path)
        {
            OpenFolder(path, true);
        }

        // remember=true 表示用户主动选择的根目录(写入记忆);false 用于启动恢复与临时重挂
        public void OpenFolder(string path, bool remember)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                _treeRootPath = path;
                _selectedDir = path;
                _currentFolder = path;
                _expandedDirs.Clear();
                if (remember)
                {
                    _settings.TreeRoot = path;
                    _settings.LastFolder = path;
                    _settings.TreeSelected = path;
                    _settings.TreeExpanded = "";
                    _settings.Save();
                }
                _folderText.Text = path;
                _folderText.ToolTip = path;
                RebuildTree();
                RefreshGitStatus();
            }
            catch (Exception ex)
            {
                ShowError("打开文件夹失败：" + ex.Message);
            }
        }

        // ---------- 目录树状态记忆 ----------

        void ScheduleTreeStateSave()
        {
            if (_treeStateTimer == null) return;
            _treeStateTimer.Stop();
            _treeStateTimer.Start();
        }

        void SaveTreeState()
        {
            if (_treeStateTimer != null) _treeStateTimer.Stop();
            if (_treeRootPath == null) return;
            string expanded = "";
            foreach (string p in _expandedDirs)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (!IsUnderRoot(p) && !string.Equals(p, _treeRootPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (expanded.Length > 0) expanded += "|";
                expanded += p;
            }
            _settings.TreeExpanded = expanded;
            if (!string.IsNullOrEmpty(_selectedDir)) _settings.TreeSelected = _selectedDir;
            _settings.Save();
        }

        // 启动时恢复:展开上次展开的层级,并选中上次选中的目录
        void RestoreTreeState()
        {
            if (_treeRootPath == null) return;
            string root = _treeRootPath;
            if (!string.IsNullOrEmpty(_settings.TreeExpanded))
            {
                string[] parts = _settings.TreeExpanded.Split('|');
                foreach (string p in parts)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    if (!Directory.Exists(p)) continue;
                    if (!IsUnderRoot(p) && !string.Equals(p, root, StringComparison.OrdinalIgnoreCase)) continue;
                    ExpandTo(p);
                }
            }
            string sel = _settings.TreeSelected;
            if (!string.IsNullOrEmpty(sel) && Directory.Exists(sel) &&
                (IsUnderRoot(sel) || string.Equals(sel, root, StringComparison.OrdinalIgnoreCase)))
            {
                TreeViewItem selNode = ExpandTo(sel);
                if (selNode != null)
                {
                    selNode.IsSelected = true;
                    selNode.BringIntoView();
                }
            }
        }

        // ---------- 多层级目录树 ----------

        void RefreshTree_Click(object sender, RoutedEventArgs e)
        {
            if (_treeRootPath != null && Directory.Exists(_treeRootPath))
            {
                RebuildTree();
                RefreshGitStatus();
            }
            else
            {
                _tree.Items.Clear();
                _treeRootPath = null;
                _folderText.Text = "（未选择文件夹）";
            }
        }

        void RebuildTree()
        {
            if (_tree == null || _treeRootPath == null) return;
            _tree.Items.Clear();
            string name = Path.GetFileName(_treeRootPath);
            if (string.IsNullOrEmpty(name)) name = _treeRootPath;
            TreeViewItem root = MakeDirNode(name, _treeRootPath);
            _tree.Items.Add(root);
            LoadChildrenInto(root, _treeRootPath);
            root.IsExpanded = true;
        }

        TreeViewItem MakeDirNode(string name, string fullPath)
        {
            TreeViewItem node = new TreeViewItem();
            node.Header = MakeDirHeader(name, DirHasChanges(fullPath));
            node.Tag = "D|" + fullPath;
            node.FontWeight = FontWeights.SemiBold;
            node.Selected += DirNode_Selected;
            node.Expanded += DirNode_Expanded;
            node.Collapsed += DirNode_Collapsed;
            if (HasSubDirectory(fullPath) || HasTxtFile(fullPath))
            {
                node.Items.Add(MakePlaceholder());
            }
            return node;
        }

        TreeViewItem MakeFileNode(string fullPath)
        {
            TreeViewItem node = new TreeViewItem();
            string path = fullPath;
            string letter;
            string status = _gitStatuses.TryGetValue(path, out letter) ? letter : "";
            node.Header = MakeFileHeader(Path.GetFileName(path), status);
            node.Tag = "F|" + path;
            node.Foreground = string.IsNullOrEmpty(status)
                ? Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C))
                : StatusBrushFor(status);
            // 右键菜单:重命名
            ContextMenu menu = new ContextMenu();
            MenuItem mi = new MenuItem();
            mi.Header = "重命名";
            mi.Click += delegate
            {
                if (_renameBox != null) return;
                string tag = node.Tag as string;
                if (tag != null && tag.StartsWith("F|"))
                {
                    StartFileRename(node, false, Path.GetDirectoryName(tag.Substring(2)));
                }
            };
            menu.Items.Add(mi);
            node.ContextMenu = menu;
            return node;
        }

        // ---------- 目录树 git 状态标记 ----------

        Brush StatusBrushFor(string letter)
        {
            if (letter == "M") return Res("GitM", Color.FromRgb(0x89, 0x55, 0x03));
            if (letter == "D") return Res("GitD", Color.FromRgb(0xAD, 0x07, 0x07));
            if (letter == "A" || letter == "U" || letter == "R") return Res("GitA", Color.FromRgb(0x58, 0x7C, 0x0C));
            return Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C));
        }

        // 文件名 + 右对齐的 git 状态字母
        FrameworkElement MakeFileHeader(string name, string letter)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinition rc = new ColumnDefinition();
            rc.Width = GridLength.Auto;
            g.ColumnDefinitions.Add(rc);
            TextBlock nm = new TextBlock();
            nm.Text = name;
            nm.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(nm, 0);
            g.Children.Add(nm);
            if (!string.IsNullOrEmpty(letter))
            {
                TextBlock st = new TextBlock();
                st.Text = letter;
                st.FontSize = 11;
                st.FontWeight = FontWeights.SemiBold;
                st.Margin = new Thickness(6, 0, 4, 0);
                st.Foreground = StatusBrushFor(letter);
                Grid.SetColumn(st, 1);
                g.Children.Add(st);
            }
            return g;
        }

        // 目录名 + 改动圆点(该目录下存在改动时)
        FrameworkElement MakeDirHeader(string name, bool changed)
        {
            if (!changed)
            {
                TextBlock only = new TextBlock();
                only.Text = name;
                only.TextTrimming = TextTrimming.CharacterEllipsis;
                return only;
            }
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinition rc = new ColumnDefinition();
            rc.Width = GridLength.Auto;
            g.ColumnDefinitions.Add(rc);
            TextBlock nm = new TextBlock();
            nm.Text = name;
            nm.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(nm, 0);
            g.Children.Add(nm);
            TextBlock dot = new TextBlock();
            dot.Text = "●";
            dot.FontSize = 9;
            dot.VerticalAlignment = VerticalAlignment.Center;
            dot.Margin = new Thickness(6, 0, 4, 0);
            dot.Foreground = Res("GitDot", Color.FromRgb(0xA8, 0xA8, 0xA8));
            Grid.SetColumn(dot, 1);
            g.Children.Add(dot);
            return g;
        }

        bool DirHasChanges(string dir)
        {
            if (_gitStatuses.Count == 0) return false;
            string prefix = dir.EndsWith("\\") ? dir : dir + "\\";
            foreach (KeyValuePair<string, string> kv in _gitStatuses)
            {
                if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // 读取当前目录树所在仓库的 git 状态(目录树着色 + 状态字母)
        public void RefreshGitStatus()
        {
            string root = _treeRootPath;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root) || GitRunner.FindGit() == null)
            {
                _gitStatuses.Clear();
                _gitRepoRoot = null;
                ApplyGitStatusToTree();
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                GitResult top = GitRunner.Run(root, "rev-parse --show-toplevel", null, 20000);
                if (!top.Ok)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        _gitStatuses.Clear();
                        _gitRepoRoot = null;
                        ApplyGitStatusToTree();
                    }));
                    return;
                }
                string repoRoot = top.Output.Trim().Replace('/', '\\');
                GitResult st = GitRunner.Run(repoRoot, "status --porcelain", null, 30000);
                Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (st.Ok)
                {
                    foreach (string raw in st.Output.Split('\n'))
                    {
                        string line = raw.TrimEnd('\r');
                        if (line.Length < 4) continue;
                        string xy = line.Substring(0, 2);
                        string path = line.Substring(3).Trim().Trim('"');
                        int arrow = path.IndexOf(" -> ");
                        if (arrow > 0) path = path.Substring(arrow + 4);
                        try { map[Path.Combine(repoRoot, path.Replace('/', '\\'))] = GitRunner.StatusLetter(xy); }
                        catch { }
                    }
                }
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (!string.Equals(_treeRootPath, root, StringComparison.OrdinalIgnoreCase)) return;
                    _gitStatuses.Clear();
                    foreach (KeyValuePair<string, string> kv in map) _gitStatuses[kv.Key] = kv.Value;
                    _gitRepoRoot = repoRoot;
                    ApplyGitStatusToTree();
                }));
            });
        }

        void ApplyGitStatusToTree()
        {
            if (_tree == null) return;
            foreach (object o in _tree.Items)
            {
                TreeViewItem it = o as TreeViewItem;
                if (it != null) UpdateNodeGitStatus(it, 0);
            }
        }

        void UpdateNodeGitStatus(TreeViewItem node, int depth)
        {
            if (node == null || depth > 12 || node.Tag == null) return;
            string tag = node.Tag as string;
            if (tag == null) return;
            if (tag.StartsWith("F|"))
            {
                string path = tag.Substring(2);
                string letter;
                string status = _gitStatuses.TryGetValue(path, out letter) ? letter : "";
                node.Header = MakeFileHeader(Path.GetFileName(path), status);
                node.Foreground = string.IsNullOrEmpty(status)
                    ? Res("FgSoft", Color.FromRgb(0x79, 0x81, 0x8C))
                    : StatusBrushFor(status);
                return;
            }
            if (tag.StartsWith("D|"))
            {
                string dir = tag.Substring(2);
                string name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) name = dir;
                node.Header = MakeDirHeader(name, DirHasChanges(dir));
                foreach (object o in node.Items)
                {
                    TreeViewItem c = o as TreeViewItem;
                    if (c != null) UpdateNodeGitStatus(c, depth + 1);
                }
            }
        }

        static TreeViewItem MakePlaceholder()
        {
            TreeViewItem node = new TreeViewItem();
            node.Header = "加载中…";
            node.Tag = "PLACEHOLDER";
            node.IsEnabled = false;
            return node;
        }

        static bool IsPlaceholder(object item)
        {
            TreeViewItem t = item as TreeViewItem;
            if (t == null || t.Tag == null) return false;
            string s = t.Tag as string;
            return s == "PLACEHOLDER";
        }

        static bool HasSubDirectory(string dir)
        {
            try
            {
                foreach (string d in Directory.EnumerateDirectories(dir)) return true;
            }
            catch { }
            return false;
        }

        static bool HasTxtFile(string dir)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*.txt")) return true;
            }
            catch { }
            return false;
        }

        void DirNode_Expanded(object sender, RoutedEventArgs e)
        {
            TreeViewItem node = sender as TreeViewItem;
            if (node == null || node.Tag == null) return;
            string tag = node.Tag as string;
            if (tag == null || !tag.StartsWith("D|")) return;
            string dir = tag.Substring(2);
            if (!(node.Items.Count > 0 && !IsPlaceholder(node.Items[0])))   // 未加载则加载
            {
                node.Items.Clear();
                LoadChildrenInto(node, dir);
            }
            _expandedDirs.Add(dir);
            ScheduleTreeStateSave();
        }

        void DirNode_Collapsed(object sender, RoutedEventArgs e)
        {
            TreeViewItem node = sender as TreeViewItem;
            if (node == null || node.Tag == null) return;
            string tag = node.Tag as string;
            if (tag == null || !tag.StartsWith("D|")) return;
            _expandedDirs.Remove(tag.Substring(2));
            ScheduleTreeStateSave();
        }

        void DirNode_Selected(object sender, RoutedEventArgs e)
        {
            // 只响应选中事件源头是这个目录节点本身(子节点选中冒泡上来的忽略)
            if (e.OriginalSource != sender) return;
            TreeViewItem node = sender as TreeViewItem;
            if (node == null || node.Tag == null) return;
            string tag = node.Tag as string;
            if (tag == null || !tag.StartsWith("D|")) return;
            _selectedDir = tag.Substring(2);
            _currentFolder = _selectedDir;
            _folderText.Text = _selectedDir;
            _folderText.ToolTip = _selectedDir;
            ScheduleTreeStateSave();
        }

        void LoadChildrenInto(TreeViewItem node, string dir)
        {
            if (node.Items.Count > 0 && IsPlaceholder(node.Items[0])) node.Items.Clear();

            List<string> subdirs = new List<string>();
            List<string> files = new List<string>();
            try
            {
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string dn = Path.GetFileName(d);   // 隐藏版本控制目录,避免干扰
                    if (string.Equals(dn, ".git", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dn, ".svn", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dn, ".hg", StringComparison.OrdinalIgnoreCase)) continue;
                    subdirs.Add(d);
                }
            }
            catch { }
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.txt")) files.Add(f);
            }
            catch { }
            subdirs.Sort(StringComparer.CurrentCulture);
            files.Sort(StringComparer.CurrentCulture);

            const int cap = 500;
            bool truncated = false;
            foreach (string d in subdirs)
            {
                if (node.Items.Count >= cap) { truncated = true; break; }
                node.Items.Add(MakeDirNode(Path.GetFileName(d), d));
            }
            foreach (string f in files)
            {
                if (node.Items.Count >= cap) { truncated = true; break; }
                node.Items.Add(MakeFileNode(f));
            }
            if (truncated)
            {
                TreeViewItem more = new TreeViewItem();
                more.Header = "（条目过多，仅显示前 " + cap + " 项）";
                more.Tag = "MORE";
                more.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                more.IsEnabled = false;
                node.Items.Add(more);
            }
            else if (node.Items.Count == 0)
            {
                TreeViewItem empty = new TreeViewItem();
                empty.Header = "（空）";
                empty.Tag = "EMPTY";
                empty.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                empty.IsEnabled = false;
                node.Items.Add(empty);
            }
        }

        bool IsUnderRoot(string path)
        {
            if (_treeRootPath == null) return false;
            string prefix = _treeRootPath.EndsWith("\\") ? _treeRootPath : _treeRootPath + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // 沿懒加载树逐层展开到指定目录,返回该目录节点
        TreeViewItem ExpandTo(string dir)
        {
            if (_treeRootPath == null || !Directory.Exists(_treeRootPath)) return null;
            if (_tree.Items.Count == 0) return null;
            TreeViewItem current = _tree.Items[0] as TreeViewItem;
            if (current == null) return null;

            if (current.Items.Count == 0 || IsPlaceholder(current.Items[0]))
                LoadChildrenInto(current, _treeRootPath);
            current.IsExpanded = true;

            if (string.Equals(dir, _treeRootPath, StringComparison.OrdinalIgnoreCase)) return current;
            if (!IsUnderRoot(dir)) return null;

            string rel = RelPath(_treeRootPath, dir);
            foreach (string part in rel.Split('\\'))
            {
                if (part.Length == 0) continue;
                TreeViewItem next = null;
                foreach (object child in current.Items)
                {
                    TreeViewItem ti = child as TreeViewItem;
                    if (ti == null || ti.Tag == null) continue;
                    string t = ti.Tag as string;
                    if (t != null && t.StartsWith("D|") &&
                        string.Equals(Path.GetFileName(t.Substring(2)), part, StringComparison.OrdinalIgnoreCase))
                    {
                        next = ti;
                        break;
                    }
                }
                if (next == null) return null;
                if (next.Items.Count == 0 || IsPlaceholder(next.Items[0]))
                    LoadChildrenInto(next, (next.Tag as string).Substring(2));
                next.IsExpanded = true;
                current = next;
            }
            return current;
        }

        // ---------- 文件重命名(目录树内联编辑) ----------

        // 让目录树节点进入"重命名样式":Header 换成输入框
        void StartFileRename(TreeViewItem node, bool isNew, string dir)
        {
            if (_renameBox != null || node == null || node.Tag == null) return;
            string tag = node.Tag as string;
            if (tag == null || !tag.StartsWith("F|")) return;
            string initial = Path.GetFileName(tag.Substring(2));

            _renameNode = node;
            _renameIsNew = isNew;
            _renameDir = dir;

            TextBox box = new TextBox();
            box.Text = initial;
            box.FontSize = 13;
            box.Padding = new Thickness(3, 1, 3, 1);
            box.BorderThickness = new Thickness(1);
            box.BorderBrush = Res("Accent", Color.FromRgb(0x2F, 0x6F, 0xEB));
            box.Background = Res("White", Colors.White);
            box.Foreground = Res("Fg", Color.FromRgb(0x23, 0x27, 0x2E));
            box.KeyDown += RenameBox_KeyDown;
            box.LostFocus += RenameBox_LostFocus;
            _renameBox = box;

            node.Header = box;
            node.IsSelected = true;
            node.BringIntoView();
            box.Loaded += delegate
            {
                box.Focus();
                int dot = initial.LastIndexOf('.');
                box.Select(0, dot > 0 ? dot : initial.Length);   // 选中文件名部分,保留 .txt
            };
        }

        void RenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitRename(false);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CommitRename(true);
            }
        }

        void RenameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitRename(false);
        }

        void CommitRename(bool cancel)
        {
            TextBox box = _renameBox;
            TreeViewItem node = _renameNode;
            bool isNew = _renameIsNew;
            string dir = _renameDir;
            if (box == null || node == null) return;
            _renameBox = null;      // 防重入(LostFocus 与 Enter 重复触发)
            _renameNode = null;

            string name = box.Text.Trim();
            string oldTag = node.Tag as string;
            string oldFull = (oldTag != null && oldTag.StartsWith("F|")) ? oldTag.Substring(2) : null;

            // 取消:新建则移除临时节点,重命名则还原名称
            if (cancel || name.Length == 0)
            {
                if (isNew)
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { RemoveNode(node); }));
                else
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        node.Header = oldFull != null ? Path.GetFileName(oldFull) : name;
                    }));
                return;
            }
            if (name.IndexOfAny(new char[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            {
                ShowError("文件名不能包含 / \\ : * ? \" < > | 字符");
                if (isNew)
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { RemoveNode(node); }));
                else
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        node.Header = oldFull != null ? Path.GetFileName(oldFull) : name;
                    }));
                return;
            }
            if (!name.ToLowerInvariant().EndsWith(".txt")) name += ".txt";

            if (isNew)
            {
                // 新建:先落盘,再从树中移除临时节点,走 LoadFile 流程(自动重载目录、选中)
                string full = Path.Combine(dir, name);
                if (File.Exists(full))
                {
                    ShowError("文件已存在：" + name);
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { RemoveNode(node); }));
                    return;
                }
                try
                {
                    File.WriteAllText(full, "", new System.Text.UTF8Encoding(true));
                }
                catch (Exception ex)
                {
                    ShowError("新建失败：" + ex.Message);
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { RemoveNode(node); }));
                    return;
                }
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    RemoveNode(node);
                    LoadFile(full);
                }));
            }
            else
            {
                // 重命名已有文件
                if (oldFull == null) return;
                string newFull = Path.Combine(Path.GetDirectoryName(oldFull), name);
                if (string.Equals(oldFull, newFull, StringComparison.OrdinalIgnoreCase))
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        node.Header = Path.GetFileName(oldFull);
                    }));
                    return;
                }
                if (File.Exists(newFull))
                {
                    ShowError("文件已存在：" + name);
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        node.Header = Path.GetFileName(oldFull);
                    }));
                    return;
                }
                try
                {
                    File.Move(oldFull, newFull);
                    // 若重命名的是当前打开的文件,同步更新当前路径
                    if (_currentFile != null && string.Equals(_currentFile, oldFull, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentFile = newFull;
                        UpdateTitle();
                    }
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        node.Tag = "F|" + newFull;
                        node.Header = Path.GetFileName(newFull);
                    }));
                }
                catch (Exception ex)
                {
                    ShowError("重命名失败：" + ex.Message);
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        node.Header = Path.GetFileName(oldFull);
                    }));
                }
            }
        }

        void RemoveNode(TreeViewItem node)
        {
            if (node == null) return;
            ItemsControl ic = ItemsControl.ItemsControlFromItemContainer(node);
            if (ic != null) ic.Items.Remove(node);
        }

        void StripHintNodes(TreeViewItem dirNode)
        {
            List<object> toRemove = new List<object>();
            foreach (object child in dirNode.Items)
            {
                TreeViewItem ti = child as TreeViewItem;
                if (ti != null && ti.Tag != null)
                {
                    string t = ti.Tag as string;
                    if (t == "EMPTY" || t == "MORE") toRemove.Add(ti);
                }
            }
            foreach (object r in toRemove) dirNode.Items.Remove(r);
        }

        TreeViewItem FindFileNode(TreeViewItem dirNode, string path)
        {
            foreach (object child in dirNode.Items)
            {
                TreeViewItem ti = child as TreeViewItem;
                if (ti == null || ti.Tag == null) continue;
                string t = ti.Tag as string;
                if (t != null && t.StartsWith("F|") && string.Equals(t.Substring(2), path, StringComparison.OrdinalIgnoreCase))
                    return ti;
            }
            return null;
        }

        // 在目录树中定位并选中文件(必要时逐层展开、重载所在目录)
        void SelectFileInTree(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || _treeRootPath == null) return;
            if (!IsUnderRoot(dir) && !string.Equals(dir, _treeRootPath, StringComparison.OrdinalIgnoreCase)) return;
            TreeViewItem dirNode = ExpandTo(dir);
            if (dirNode == null) return;
            TreeViewItem fileNode = FindFileNode(dirNode, path);
            if (fileNode == null)
            {
                dirNode.Items.Clear();
                LoadChildrenInto(dirNode, dir);
                fileNode = FindFileNode(dirNode, path);
            }
            if (fileNode != null)
            {
                fileNode.IsSelected = true;
                fileNode.BringIntoView();
            }
        }

        static string RelPath(string baseDir, string full)
        {
            string prefix = baseDir.EndsWith("\\") ? baseDir : baseDir + "\\";
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return full.Substring(prefix.Length);
            }
            return full;
        }

        public void LoadFile(string path)
        {
            if (!ConfirmSaveBeforeSwitch(path)) return;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                string encName;
                string text = TextEncodingHelper.Decode(bytes, out encName);

                _loading = true;
                // 清空撤销栈:否则 Ctrl+Z 会撤销掉"设置文本"这一步,表现为回退到上一个文件
                bool undoWasEnabled = _editor.IsUndoEnabled;
                _editor.IsUndoEnabled = false;
                _editor.Text = text;
                _editor.IsUndoEnabled = undoWasEnabled;
                _loading = false;

                _currentFile = path;
                _fileEncoding = encName;
                _dirty = false;
                // 已码字数与按键次数为应用生命周期累计,切换文档不清零
                _typedSinceLastTick = 0;
                _speedEma = 0;
                _lastTypeTick = 0;

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _currentFolder = dir;
                    if (_treeRootPath == null || !IsUnderRoot(path))
                    {
                        // 文件不在当前目录树内:本次会话临时以该目录为根,不覆盖用户记忆的目录树
                        OpenFolder(dir, false);
                    }
                    SelectFileInTree(path);
                }

                _encText.Text = TextEncodingHelper.DisplayName(encName);
                _saveStateText.Text = "已打开 " + DateTime.Now.ToString("HH:mm:ss");
                UpdateTitle();
                UpdateStats();
                UpdateSpeed();
                _editor.Focus();
            }
            catch (Exception ex)
            {
                _loading = false;
                ShowError("打开失败：" + ex.Message);
            }
        }

        // 切换文档前处理当前文档的未保存改动:
        // 已命名文档自动保存(剪切、删改等改动不会因切换而丢失);未命名且有内容时询问
        bool ConfirmSaveBeforeSwitch(string targetPath)
        {
            if (!_dirty) return true;
            if (_currentFile != null)
            {
                if (string.Equals(_currentFile, targetPath, StringComparison.OrdinalIgnoreCase)) return true;
                SaveToFile();
                return !_dirty;
            }
            if (string.IsNullOrEmpty(_editor.Text) || TextStats.TotalChars(_editor.Text) == 0) return true;
            MessageBoxResult r = MessageBox.Show(this,
                "当前内容还没有保存成文件，切换后这些内容会丢失。要先保存吗？",
                "写作", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                SaveToFile();
                return !_dirty;
            }
            return r == MessageBoxResult.No;
        }

        void SaveToFile()
        {
            try
            {
                if (_currentFile == null)
                {
                    SaveFileDialog dlg = new SaveFileDialog();
                    dlg.Filter = "文本文件 (*.txt)|*.txt";
                    dlg.FileName = "新建文档.txt";
                    if (dlg.ShowDialog(this) != true) return;
                    _currentFile = dlg.FileName;
                    _fileEncoding = "utf-8";
                }
                string text = NormalizeNewlines(_editor.Text);
                byte[] bytes = TextEncodingHelper.Encode(text, _fileEncoding);
                File.WriteAllBytes(_currentFile, bytes);
                _dirty = false;
                UpdateTitle();
                _encText.Text = TextEncodingHelper.DisplayName(_fileEncoding);
                _saveStateText.Text = "已保存 " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (Exception ex)
            {
                ShowError("保存失败：" + ex.Message);
            }
        }

        static string NormalizeNewlines(string text)
        {
            string t = text.Replace("\r\n", "\n");
            t = t.Replace("\r", "\n");
            t = t.Replace("\n", "\r\n");
            return t;
        }

        void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            DependencyObject src = e.OriginalSource as DependencyObject;
            while (src != null && !(src is TreeViewItem)) src = VisualTreeHelper.GetParent(src);
            TreeViewItem item = src as TreeViewItem;
            if (item == null || item.Tag == null) return;
            string tag = item.Tag as string;
            if (tag != null && tag.StartsWith("F|"))
            {
                string path = tag.Substring(2);
                if (File.Exists(path))
                {
                    LoadFile(path);
                    e.Handled = true;
                }
            }
        }

        // ---------- 编辑器事件 ----------

        void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _applyingIndent) return;

            // 自动缩进:检测到刚插入的换行且光标紧跟其后,在光标处追加两个全角空格。
            // 不拦截回车键本身,因此对各类输入法(组词回车等)零干扰。
            if (_settings.AutoIndent && e.Changes.Count > 0)
            {
                TextChange last = null;
                foreach (TextChange tc in e.Changes) last = tc;
                if (last != null && last.AddedLength > 0)
                {
                    string added = _editor.Text.Substring(last.Offset, last.AddedLength);
                    bool isNewline = added.EndsWith("\r") || added.EndsWith("\n");
                    if (isNewline && _editor.CaretIndex == last.Offset + last.AddedLength)
                    {
                        if (_skipIndentOnce)
                        {
                            _skipIndentOnce = false;
                        }
                        else
                        {
                            _applyingIndent = true;
                            _editor.SelectedText = IndentString;
                            // SelectedText 插入后文字保持选中且光标停在插入起点,
                            // 需取消选中并把光标移到缩进之后,否则紧接着的输入会
                            // 替换缩进或插到缩进前面
                            int insStart = _editor.SelectionStart;
                            _editor.Select(insStart + IndentString.Length, 0);
                            _applyingIndent = false;
                        }
                    }
                }
            }

            if (!_dirty)
            {
                _dirty = true;
                UpdateTitle();
            }
            UpdateStats();
        }

        void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Shift+Enter:换行但不缩进(放行默认换行处理)
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                _skipIndentOnce = true;
                return;
            }
            // Ctrl+X:自行剪切。WPF 内置 Cut 在系统剪贴板被其它程序占用时(写入失败)
            // 会跳过"删除选区"这一步,表现为"剪切后原文还在",这里改为自己复制再删除
            if (e.Key == Key.X && Keyboard.Modifiers == ModifierKeys.Control && _editor.SelectionLength > 0)
            {
                CutSelection();
                e.Handled = true;
            }
        }

        // 剪切:先尝试写入剪贴板,无论成功与否都删除选区,避免出现"剪切不生效"的错觉
        void CutSelection()
        {
            if (_editor.SelectionLength <= 0) return;
            string text = _editor.SelectedText;
            bool copied = false;
            try
            {
                Clipboard.SetDataObject(text, true);
                copied = true;
            }
            catch { }
            _editor.SelectedText = string.Empty;
            if (!copied && _saveStateText != null)
            {
                _saveStateText.Text = "剪贴板被占用,已剪切(Ctrl+Z 可还原)";
            }
        }

        void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ChangeFontSize(e.Delta > 0 ? 2 : -2);
                e.Handled = true;
            }
        }

        // 按"实际键入的字符"累计已码字数(应用生命周期累计,切换文档不清零;
        // 选中替换、删除重打都算),同时作为码字速度的采样来源。粘贴等非键入操作不计数。
        void Editor_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            long now = DateTime.Now.Ticks;
            _lastTypeTick = now;
            int n = 0;
            foreach (char c in e.Text)
            {
                if (!char.IsWhiteSpace(c))
                {
                    n++;
                    _sessionAdded++;
                }
            }
            if (n > 0)
            {
                _typedSinceLastTick += n;
                _sessionText.Text = _sessionAdded.ToString("N0", CultureInfo.InvariantCulture);
            }
        }

        // ---------- 窗口事件 ----------

        void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SaveToFile();
                e.Handled = true;
            }
            else if (e.Key == Key.F2)
            {
                // F2:重命名目录树中选中的 txt 文件
                if (_renameBox == null)
                {
                    TreeViewItem item = _tree.SelectedItem as TreeViewItem;
                    if (item != null && item.Tag != null)
                    {
                        string tag = item.Tag as string;
                        if (tag != null && tag.StartsWith("F|"))
                        {
                            StartFileRename(item, false, Path.GetDirectoryName(tag.Substring(2)));
                            e.Handled = true;
                        }
                    }
                }
            }
            else if (e.Key == Key.Enter && _tree != null && _tree.IsKeyboardFocusWithin && _renameBox == null)
            {
                // Enter:打开树中选中的文件 / 展开折叠目录
                TreeViewItem item = _tree.SelectedItem as TreeViewItem;
                if (item != null && item.Tag != null)
                {
                    string tag = item.Tag as string;
                    if (tag != null && tag.StartsWith("F|"))
                    {
                        string path = tag.Substring(2);
                        if (File.Exists(path))
                        {
                            LoadFile(path);
                            e.Handled = true;
                        }
                    }
                    else if (tag != null && tag.StartsWith("D|"))
                    {
                        item.IsExpanded = !item.IsExpanded;
                        e.Handled = true;
                    }
                }
            }
            else if (e.Key == Key.OemPlus && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ChangeFontSize(2);
                e.Handled = true;
            }
            else if (e.Key == Key.OemMinus && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ChangeFontSize(-2);
                e.Handled = true;
            }
        }

        void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        void Window_Drop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
            {
                string p = files[0];
                if (Directory.Exists(p)) OpenFolder(p);
                else if (File.Exists(p)) LoadFile(p);
            }
        }

        void Window_Closing(object sender, CancelEventArgs e)
        {
            SaveWindowBounds();
            SaveTreeState();
            if (_hookHandle != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_hookHandle); } catch { }
                _hookHandle = IntPtr.Zero;
            }
            if (!_dirty) return;
            if (_currentFile != null)
            {
                SaveToFile();
                if (_dirty) e.Cancel = true;
                return;
            }
            MessageBoxResult r = MessageBox.Show(this, "文档尚未保存，要保存吗？", "写作",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                SaveToFile();
                if (_dirty) e.Cancel = true;
            }
            else if (r == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
            }
        }

        // ---------- 全局键盘钩子(按键统计) ----------

        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        // 钩子回调在本线程消息循环上执行,只做轻量计数,绝不抛异常
        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        IntPtr fg = GetForegroundWindow();
                        if (fg != IntPtr.Zero)
                        {
                            uint pid;
                            GetWindowThreadProcessId(fg, out pid);
                            if (pid == _myPid)
                            {
                                KBDLLHOOKSTRUCT ks = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                                int vk = (int)ks.vkCode;
                                // 左右 Shift/Ctrl/Alt 归一化
                                if (vk == 0xA0 || vk == 0xA1) vk = 0x10;
                                else if (vk == 0xA2 || vk == 0xA3) vk = 0x11;
                                else if (vk == 0xA4 || vk == 0xA5) vk = 0x12;
                                int old = _keyCounts.ContainsKey(vk) ? _keyCounts[vk] : 0;
                                _keyCounts[vk] = old + 1;
                            }
                        }
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        void KeyStats_Click(object sender, RoutedEventArgs e)
        {
            if (_keyWin != null)
            {
                _keyWin.Activate();
                return;
            }
            _keyWin = new KeyStatsWindow(_keyCounts, _hookHandle != IntPtr.Zero);
            _keyWin.Closed += delegate { _keyWin = null; };
            _keyWin.Show();
        }

        // ---------- Git 面板 ----------

        public AppSettings Settings { get { return _settings; } }

        public string CurrentFile { get { return _currentFile; } }

        void Git_Click(object sender, RoutedEventArgs e)
        {
            if (_gitWin != null)
            {
                _gitWin.Activate();
                return;
            }
            _gitWin = new GitWindow(this);
            _gitWin.Closed += delegate { _gitWin = null; };
            _gitWin.Show();
        }

        // git 拉取/推送后:若当前文档在仓库内,重新载入以显示最新内容
        public void ReloadAfterGit(string repoDir)
        {
            if (_currentFile == null || string.IsNullOrEmpty(repoDir)) return;
            string prefix = repoDir.EndsWith("\\") ? repoDir : repoDir + "\\";
            if (!_currentFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            LoadFile(_currentFile);
        }

        // ---------- 统计 ----------

        void UpdateTitle()
        {
            string name = _currentFile == null ? "未命名" : Path.GetFileName(_currentFile);
            Title = "写作 - " + name + (_dirty ? " ●" : "");
            _fileNameText.Text = name + (_dirty ? " ●" : "");
        }

        void UpdateStats()
        {
            string text = _editor.Text;
            int total = TextStats.TotalChars(text);
            int net = TextStats.NetChars(text);
            _totalText.Text = total.ToString("N0", CultureInfo.InvariantCulture);
            _netText.Text = net.ToString("N0", CultureInfo.InvariantCulture);
            _sessionText.Text = _sessionAdded.ToString("N0", CultureInfo.InvariantCulture);
        }

        void SpeedTimer_Tick(object sender, EventArgs e)
        {
            UpdateSpeed();
        }

        // 每 3 秒取一次样:本次 3 秒内的即时速度(字/分)与历史值做指数平滑,
        // 得到 12、64 这类平滑估值;停止输入超过 9 秒归零
        void UpdateSpeed()
        {
            long now = DateTime.Now.Ticks;
            int instant = _typedSinceLastTick * 20;
            _typedSinceLastTick = 0;
            double ema = _speedEma * 0.5 + instant * 0.5;
            _speedEma = ema;
            int speed = 0;
            if (_lastTypeTick != 0 && now - _lastTypeTick <= TimeSpan.TicksPerSecond * 9)
            {
                speed = (int)Math.Round(ema);
            }
            _speedText.Text = speed.ToString("N0", CultureInfo.InvariantCulture);
        }

        void ShowError(string message)
        {
            MessageBox.Show(this, message, "写作", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // 键盘统计面板:展示应用启动以来累计的按键敲击次数
    public class KeyStatsWindow : Window
    {
        Dictionary<int, int> _counts;
        Dictionary<int, TextBlock> _countTexts = new Dictionary<int, TextBlock>();
        Dictionary<int, Border> _cells = new Dictionary<int, Border>();
        TextBlock _otherText;
        DispatcherTimer _timer;

        static readonly SolidColorBrush KeyBg = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        static readonly SolidColorBrush KeyBgHot = new SolidColorBrush(Color.FromRgb(0xEB, 0xF1, 0xFD));
        static readonly SolidColorBrush KeyBorder = new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xEF));
        static readonly SolidColorBrush KeyBorderHot = new SolidColorBrush(Color.FromRgb(0xBB, 0xD2, 0xFA));
        static readonly SolidColorBrush LabelFg = new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xAD));
        static readonly SolidColorBrush CountFg = new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xAD));
        static readonly SolidColorBrush CountFgHot = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB));

        public KeyStatsWindow(Dictionary<int, int> counts, bool hookOk)
        {
            try { Resources.MergedDictionaries.Add(UiTheme.Create()); }
            catch { }

            _counts = counts;
            Title = "按键统计 - 累计";
            Width = 940;
            Height = 440;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9));
            FontFamily = new FontFamily("Microsoft YaHei UI");
            BuildUi(hookOk);
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += delegate { Refresh(); };
            _timer.Start();
            Closed += delegate { _timer.Stop(); };
        }

        void BuildUi(bool hookOk)
        {
            DockPanel root = new DockPanel();

            Border head = new Border();
            head.Padding = new Thickness(18, 14, 14, 8);
            DockPanel.SetDock(head, Dock.Top);
            DockPanel hd = new DockPanel();
            head.Child = hd;

            Button reset = new Button();
            reset.Content = "重置计数";
            reset.FontSize = 12;
            reset.Padding = new Thickness(10, 4, 10, 4);
            reset.Foreground = new SolidColorBrush(Color.FromRgb(0x79, 0x81, 0x8C));
            reset.Click += delegate { _counts.Clear(); Refresh(); };
            DockPanel.SetDock(reset, Dock.Right);
            hd.Children.Add(reset);

            TextBlock tip = new TextBlock();
            tip.Text = hookOk
                ? "自软件启动以来累计的按键敲击次数（仅统计本软件在前台时）"
                : "提示：键盘统计不可用（系统未允许全局键盘钩子）";
            tip.Foreground = hookOk
                ? new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xAD))
                : new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x2B));
            tip.VerticalAlignment = VerticalAlignment.Center;
            tip.FontSize = 12;
            hd.Children.Add(tip);
            root.Children.Add(head);

            Grid grid = new Grid();
            grid.Margin = new Thickness(14, 6, 14, 6);
            for (int i = 0; i < 15; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions[4].Height = GridLength.Auto;

            // 第 1 行:` 1-0 - = Backspace
            int row = 0, col = 0;
            AddKey(grid, row, ref col, "`", 0xC0, 1);
            for (int d = 0; d <= 9; d++) AddKey(grid, row, ref col, ((char)(0x30 + d)).ToString(), 0x30 + d, 1);
            AddKey(grid, row, ref col, "-", 0xBD, 1);
            AddKey(grid, row, ref col, "=", 0xBB, 1);
            AddKey(grid, row, ref col, "Backspace", 0x08, 2);

            // 第 2 行:Q-P [ ] \
            row = 1; col = 1;
            string qrow = "QWERTYUIOP";
            for (int i = 0; i < qrow.Length; i++) AddKey(grid, row, ref col, qrow[i].ToString(), qrow[i], 1);
            AddKey(grid, row, ref col, "[", 0xDB, 1);
            AddKey(grid, row, ref col, "]", 0xDD, 1);
            AddKey(grid, row, ref col, "\\", 0xDC, 1);

            // 第 3 行:A-L ; ' Enter
            row = 2; col = 1;
            string arow = "ASDFGHJKL";
            for (int i = 0; i < arow.Length; i++) AddKey(grid, row, ref col, arow[i].ToString(), arow[i], 1);
            AddKey(grid, row, ref col, ";", 0xBA, 1);
            AddKey(grid, row, ref col, "'", 0xDE, 1);
            AddKey(grid, row, ref col, "Enter", 0x0D, 2);

            // 第 4 行:Z-M , . / Space
            row = 3; col = 1;
            string zrow = "ZXCVBNM";
            for (int i = 0; i < zrow.Length; i++) AddKey(grid, row, ref col, zrow[i].ToString(), zrow[i], 1);
            AddKey(grid, row, ref col, ",", 0xBC, 1);
            AddKey(grid, row, ref col, ".", 0xBE, 1);
            AddKey(grid, row, ref col, "/", 0xBF, 1);
            AddKey(grid, row, ref col, "Space", 0x20, 5);

            // 第 5 行:Tab Shift Ctrl Alt 其他
            row = 4; col = 1;
            AddKey(grid, row, ref col, "Tab", 0x09, 1);
            AddKey(grid, row, ref col, "Shift", 0x10, 2);
            AddKey(grid, row, ref col, "Ctrl", 0x11, 2);
            AddKey(grid, row, ref col, "Alt", 0x12, 2);

            Border other = new Border();
            other.Width = 96;
            other.Height = 48;
            other.Margin = new Thickness(3);
            other.CornerRadius = new CornerRadius(8);
            other.Background = KeyBg;
            other.BorderBrush = KeyBorder;
            other.BorderThickness = new Thickness(1);
            StackPanel osp = new StackPanel();
            osp.Orientation = Orientation.Horizontal;
            osp.HorizontalAlignment = HorizontalAlignment.Center;
            TextBlock ol = new TextBlock();
            ol.Text = "其他 ";
            ol.FontSize = 11;
            ol.Foreground = LabelFg;
            ol.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(ol, "key_Other");
            _otherText = new TextBlock();
            _otherText.Text = "0";
            _otherText.FontSize = 16;
            _otherText.FontWeight = FontWeights.Bold;
            _otherText.Foreground = CountFg;
            _otherText.VerticalAlignment = VerticalAlignment.Center;
            osp.Children.Add(ol);
            osp.Children.Add(_otherText);
            other.Child = osp;
            Grid.SetRow(other, row);
            Grid.SetColumn(other, col);
            Grid.SetColumnSpan(other, 2);
            grid.Children.Add(other);

            root.Children.Add(grid);
            Content = root;
        }

        void AddKey(Grid grid, int row, ref int col, string label, int vk, int span)
        {
            Border b = new Border();
            b.Height = 54;
            b.Margin = new Thickness(3);
            b.CornerRadius = new CornerRadius(8);
            b.Background = KeyBg;
            b.BorderBrush = KeyBorder;
            b.BorderThickness = new Thickness(1);

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            TextBlock lb = new TextBlock();
            lb.Text = label;
            lb.FontSize = 11;
            lb.Foreground = LabelFg;
            lb.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(lb, "key_" + label);
            TextBlock cb = new TextBlock();
            cb.Text = "0";
            cb.FontSize = 16;
            cb.FontWeight = FontWeights.SemiBold;
            cb.Foreground = CountFg;
            cb.VerticalAlignment = VerticalAlignment.Center;
            cb.Margin = new Thickness(5, 0, 0, 0);
            sp.Children.Add(lb);
            sp.Children.Add(cb);
            b.Child = sp;

            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            Grid.SetColumnSpan(b, span);
            grid.Children.Add(b);

            _countTexts[vk] = cb;
            _cells[vk] = b;
            col += span;
        }

        void Refresh()
        {
            int shown = 0;
            foreach (KeyValuePair<int, TextBlock> kv in _countTexts)
            {
                int c = _counts.ContainsKey(kv.Key) ? _counts[kv.Key] : 0;
                kv.Value.Text = c.ToString();
                kv.Value.Foreground = c > 0 ? CountFgHot : CountFg;
                Border cell = _cells[kv.Key];
                cell.Background = c > 0 ? KeyBgHot : KeyBg;
                cell.BorderBrush = c > 0 ? KeyBorderHot : KeyBorder;
                shown += c;
            }
            int total = 0;
            foreach (KeyValuePair<int, int> kv in _counts) total += kv.Value;
            int other = Math.Max(0, total - shown);
            _otherText.Text = other.ToString();
            _otherText.Foreground = other > 0 ? CountFgHot : CountFg;
        }
    }
}
