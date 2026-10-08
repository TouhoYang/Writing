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
            // 支持命令行直接打开文件夹或 txt 文件(多个文件会各自打开一个标签页)
            if (args != null)
            {
                foreach (string arg in args)
                {
                    try
                    {
                        if (Directory.Exists(arg)) win.OpenFolder(arg);
                        else if (File.Exists(arg)) win.LoadFile(arg);
                    }
                    catch { }
                }
            }
            app.Run(win);
        }
    }
}
