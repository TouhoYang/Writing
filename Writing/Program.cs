using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Writing
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                try
                {
                    MessageBox.Show("发生未处理的错误：" + e.Exception.Message, "写作",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                e.Handled = true;
            };
            MainWindow win = new MainWindow();
            // 支持命令行直接打开文件夹或 txt 文件
            if (args != null && args.Length > 0)
            {
                try
                {
                    string p = args[0];
                    if (Directory.Exists(p)) win.OpenFolder(p);
                    else if (File.Exists(p)) win.LoadFile(p);
                }
                catch { }
            }
            app.Run(win);
        }
    }
}
