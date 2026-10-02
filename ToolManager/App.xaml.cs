using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ToolManager.Services;

namespace ToolManager;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Cần cho tùy chọn encoding "oem" (code page 437, 1258...)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Mỗi thư mục cài đặt chỉ cho chạy 1 ToolManager, tránh chạy trùng tool
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(ConfigStore.BaseDir.ToLowerInvariant())));
        _singleInstance = new Mutex(true, "ToolManager_" + hash, out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("ToolManager đang chạy rồi (kiểm tra thanh taskbar).", "ToolManager",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        try { ConfigStore.CleanupOldLogs(keepDays: 30); } catch { }

        base.OnStartup(e);
        new MainWindow().Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Không để lỗi giao diện làm sập app (sập app = dừng toàn bộ tool)
        try
        {
            Directory.CreateDirectory(ConfigStore.LogRoot);
            File.AppendAllText(Path.Combine(ConfigStore.LogRoot, "_toolmanager_error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}");
        }
        catch { }
        MessageBox.Show(e.Exception.Message, "ToolManager - lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
