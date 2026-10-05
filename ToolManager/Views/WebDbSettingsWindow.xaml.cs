using System.Windows;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using ToolManager.Models;
using ToolManager.Services;

namespace ToolManager.Views;

/// <summary>
/// Form cấu hình SQL Server chứa danh sách web.
/// Mật khẩu không bao giờ hiện lại trên form; chuỗi kết nối lưu xuống file đã mã hóa DPAPI.
/// </summary>
public partial class WebDbSettingsWindow : Window
{
    private readonly string? _oldPassword;

    /// <summary>Cấu hình mới sau khi bấm Lưu. null + Cleared = người dùng xóa cấu hình.</summary>
    public WebDbSettings? Result { get; private set; }
    public bool Cleared { get; private set; }

    public WebDbSettingsWindow(WebDbSettings current)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 40;

        QueryBox.Text = string.IsNullOrWhiteSpace(current.Query) ? WebDbSettings.DefaultQuery : current.Query;
        TrustCertCheck.IsChecked = true;
        WindowsAuthRadio.IsChecked = true;

        if (current.IsConfigured)
        {
            ClearButton.Visibility = Visibility.Visible;
            var cs = WebRegistryService.Unprotect(current.ProtectedConnection);
            if (cs == null)
            {
                ShowStatus(false, "Không giải mã được cấu hình đã lưu (file được copy từ máy khác hoặc tài khoản Windows khác). Hãy nhập lại.");
            }
            else
            {
                try
                {
                    var b = new SqlConnectionStringBuilder(cs);
                    ServerBox.Text = b.DataSource;
                    DatabaseBox.Text = b.InitialCatalog;
                    TrustCertCheck.IsChecked = b.TrustServerCertificate;
                    if (!b.IntegratedSecurity)
                    {
                        SqlAuthRadio.IsChecked = true;
                        UserBox.Text = b.UserID;
                        _oldPassword = b.Password;
                        PasswordHint.Visibility = string.IsNullOrEmpty(_oldPassword) ? Visibility.Collapsed : Visibility.Visible;
                    }
                }
                catch
                {
                    ShowStatus(false, "Cấu hình đã lưu không hợp lệ. Hãy nhập lại.");
                }
            }
        }
        UpdateAuthUi();
        Loaded += (_, _) => ServerBox.Focus();
    }

    private void Auth_Checked(object sender, RoutedEventArgs e) => UpdateAuthUi();

    private void UpdateAuthUi()
    {
        if (SqlLoginPanel == null) return; // đang InitializeComponent
        bool sql = SqlAuthRadio.IsChecked == true;
        SqlLoginPanel.Visibility = sql ? Visibility.Visible : Visibility.Collapsed;
        AuthHint.Text = sql
            ? "Mật khẩu được mã hóa bằng Windows DPAPI khi lưu: chỉ tài khoản Windows hiện tại trên máy này giải mã được."
            : "Dùng tài khoản Windows đang chạy Tool Manager, không cần lưu mật khẩu. Tài khoản này cần quyền đọc bảng trên SQL Server.";
    }

    /// <summary>Tạo chuỗi kết nối từ các ô nhập. null + hiện lỗi nếu thiếu thông tin.</summary>
    private string? BuildConnectionString()
    {
        var server = ServerBox.Text.Trim();
        var db = DatabaseBox.Text.Trim();
        if (server.Length == 0) { ShowStatus(false, "Chưa nhập Server."); ServerBox.Focus(); return null; }
        if (db.Length == 0) { ShowStatus(false, "Chưa nhập Database."); DatabaseBox.Focus(); return null; }

        var b = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = db,
            TrustServerCertificate = TrustCertCheck.IsChecked == true,
            ConnectTimeout = 8,
            ApplicationName = "ToolManager",
        };

        if (SqlAuthRadio.IsChecked == true)
        {
            var user = UserBox.Text.Trim();
            var pwd = PasswordBox.Password.Length > 0 ? PasswordBox.Password : _oldPassword ?? "";
            if (user.Length == 0) { ShowStatus(false, "Chưa nhập tài khoản SQL Server."); UserBox.Focus(); return null; }
            if (pwd.Length == 0) { ShowStatus(false, "Chưa nhập mật khẩu."); PasswordBox.Focus(); return null; }
            b.UserID = user;
            b.Password = pwd;
        }
        else
        {
            b.IntegratedSecurity = true;
        }
        return b.ConnectionString;
    }

    private string Query => string.IsNullOrWhiteSpace(QueryBox.Text) ? WebDbSettings.DefaultQuery : QueryBox.Text.Trim();

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var cs = BuildConnectionString();
        if (cs == null) return;
        var query = Query; // đọc ô nhập trên UI thread, không đọc bên trong Task.Run

        TestButton.IsEnabled = false;
        ShowStatus(null, "Đang kết nối...");
        try
        {
            var entries = await Task.Run(() => WebRegistryService.LoadAsync(cs, query));
            var local = WebRegistryService.GetLocalHosts();
            int mine = entries.Count(x => local.Contains(x.Host));
            ShowStatus(true, $"Kết nối thành công: đọc được {entries.Count} web, trong đó {mine} web thuộc máy này.");
        }
        catch (Exception ex)
        {
            ShowStatus(false, "Không kết nối được: " + FriendlyError(ex));
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    public static string FriendlyError(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            msg += "\n→ Hãy bật \"Tin cậy chứng chỉ của SQL Server\".";
        else if (ex is SqlException { Number: 208 })
            msg += "\n→ Bảng chưa tồn tại: dùng nút \"Copy script tạo bảng\" hoặc sửa câu truy vấn.";
        else if (ex is SqlException { Number: 18456 })
            msg += "\n→ Sai tài khoản/mật khẩu, hoặc tài khoản chưa được cấp quyền vào database.";
        return msg;
    }

    private void CopyScript_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(WebRegistryService.CreateTableScript);
        ShowStatus(true, "Đã copy script tạo bảng. Chạy script này trên SQL Server (SSMS) rồi thêm dữ liệu vào bảng.");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cs = BuildConnectionString();
        if (cs == null) return;
        Result = new WebDbSettings { ProtectedConnection = WebRegistryService.Protect(cs), Query = Query };
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Xóa cấu hình database? Tab Ports sẽ không hiện danh sách web nữa.", "Tool Manager",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Cleared = true;
        Result = new WebDbSettings();
        DialogResult = true;
    }

    /// <summary>ok: true = thành công, false = lỗi, null = đang xử lý.</summary>
    private void ShowStatus(bool? ok, string text)
    {
        var (bg, fg, icon) = ok switch
        {
            true => ("SuccessSoftBrush", "SuccessTextBrush", ""),
            false => ("DangerSoftBrush", "DangerTextBrush", ""),
            _ => ("NeutralSoftBrush", "NeutralTextBrush", ""),
        };
        StatusBorder.Background = (Brush)FindResource(bg);
        StatusText.Foreground = StatusIcon.Foreground = (Brush)FindResource(fg);
        StatusIcon.Text = icon;
        StatusText.Text = text;
        StatusBorder.Visibility = Visibility.Visible;
    }
}
