using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ToolManager.Services;

namespace ToolManager.Views;

/// <summary>
/// Chọn folder repo trên máy này => ghi yêu cầu vào bảng dbo.ToolManagerDeployRequest.
/// Worker trên cùng máy sẽ nhận yêu cầu và gọi Claude Code tạo file deploy (giai đoạn 2).
/// Cửa sổ tự tải lại danh sách yêu cầu mỗi 5 giây để theo dõi trạng thái.
/// </summary>
public partial class DeployRequestWindow : Window
{
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly HashSet<string> _toolNames;
    private string? _connectionString;
    private bool _loading;
    private bool _nameEdited; // người dùng đã tự sửa tên tool thì không tự điền theo folder nữa

    public DeployRequestWindow(IEnumerable<string> existingToolNames)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        _toolNames = new HashSet<string>(existingToolNames, StringComparer.OrdinalIgnoreCase);

        ListTitle.Text = $"YÊU CẦU GẦN ĐÂY TRÊN MÁY {Environment.MachineName.ToUpperInvariant()}";
        ToolNameBox.TextChanged += (_, _) => { if (ToolNameBox.IsKeyboardFocusWithin) _nameEdited = true; };

        _connectionString = DeployRequestService.GetConnectionString(out var error);
        if (_connectionString == null)
        {
            ShowStatus(false, error!);
            SendButton.IsEnabled = false;
        }

        _refreshTimer.Tick += async (_, _) => await LoadAsync();
        Loaded += async (_, _) =>
        {
            FolderBox.Focus();
            if (_connectionString == null) return;
            await LoadAsync(showError: true);
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    // ================== Chọn folder ==================

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Chọn folder gốc của repo" };
        if (Directory.Exists(FolderBox.Text.Trim())) dlg.InitialDirectory = FolderBox.Text.Trim();
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var folder = NormalizeFolder(FolderBox.Text);
        if (folder == null || !Directory.Exists(folder))
        {
            FolderInfo.Text = "Chọn folder gốc của repo (folder có thư mục .git).";
            return;
        }

        FolderInfo.Text = DeployRequestService.DescribeFolder(folder);
        if (!_nameEdited) ToolNameBox.Text = Path.GetFileName(folder);
    }

    /// <summary>Đường dẫn đầy đủ, bỏ dấu \ cuối. null nếu không hợp lệ.</summary>
    private static string? NormalizeFolder(string text)
    {
        var t = text.Trim().Trim('"');
        if (t.Length == 0) return null;
        try
        {
            var full = Path.GetFullPath(t);
            return full.Length > 3 ? full.TrimEnd('\\', '/') : full; // giữ "D:\" nếu chọn cả ổ đĩa
        }
        catch
        {
            return null;
        }
    }

    // ================== Gửi yêu cầu ==================

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionString == null) return;

        var folder = NormalizeFolder(FolderBox.Text);
        var toolName = ToolNameBox.Text.Trim();
        var note = NoteBox.Text.Trim();

        if (folder == null || !Directory.Exists(folder)) { Warn("Folder không tồn tại trên máy này.", FolderBox); return; }
        if (folder.Length > DeployRequestService.MaxFolderPath) { Warn("Đường dẫn folder quá dài.", FolderBox); return; }
        if (Path.GetPathRoot(folder) == folder) { Warn("Hãy chọn folder của repo, không chọn cả ổ đĩa.", FolderBox); return; }
        if (toolName.Length == 0) { Warn("Chưa nhập tên tool.", ToolNameBox); return; }
        if (toolName.Length > DeployRequestService.MaxToolName) { Warn("Tên tool quá dài.", ToolNameBox); return; }
        if (note.Length > DeployRequestService.MaxNote) { Warn($"Ghi chú tối đa {DeployRequestService.MaxNote} ký tự.", NoteBox); return; }

        if (_toolNames.Contains(toolName) &&
            MessageBox.Show(this, $"Tool Manager đã có tool tên “{toolName}”. Vẫn gửi yêu cầu?", "Tạo deploy từ repo",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (!Directory.Exists(Path.Combine(folder, ".git")) &&
            MessageBox.Show(this, "Folder này không phải repo git (không có thư mục .git) nên deploy.cmd sẽ không git pull được.\nVẫn gửi yêu cầu?",
                "Tạo deploy từ repo", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var cs = _connectionString;
        SendButton.IsEnabled = false;
        ShowStatus(null, "Đang gửi yêu cầu...");
        try
        {
            var (id, created) = await Task.Run(() => DeployRequestService.CreateAsync(cs, folder, toolName, note));
            ShowStatus(true, created
                ? $"Đã gửi yêu cầu #{id}. Worker trên máy này sẽ nhận và tạo file deploy, trạng thái cập nhật ở danh sách bên dưới."
                : $"Folder này đã có yêu cầu #{id} đang chờ / đang tạo, không gửi thêm.");
            if (created) NoteBox.Clear();
            await LoadAsync(showError: true);
        }
        catch (Exception ex)
        {
            ShowStatus(false, "Không gửi được yêu cầu: " + FriendlyError(ex));
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    // ================== Danh sách yêu cầu ==================

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(showError: true);

    private async Task LoadAsync(bool showError = false)
    {
        if (_connectionString == null || _loading) return;
        _loading = true;
        var cs = _connectionString;
        try
        {
            var items = await Task.Run(() => DeployRequestService.ListAsync(cs));
            RequestList.ItemsSource = items;
            ListEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // Lỗi khi tự tải lại thì không hiện liên tục, chỉ hiện khi người dùng bấm hoặc lần đầu mở
            if (showError) ShowStatus(false, "Không đọc được danh sách yêu cầu: " + FriendlyError(ex));
        }
        finally
        {
            _loading = false;
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DeployRequest r || _connectionString == null) return;
        if (MessageBox.Show(this, $"Hủy yêu cầu #{r.Id} cho folder\n{r.FolderPath}?", "Tạo deploy từ repo",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var cs = _connectionString;
        try
        {
            var ok = await Task.Run(() => DeployRequestService.CancelAsync(cs, r.Id));
            ShowStatus(ok, ok ? $"Đã hủy yêu cầu #{r.Id}." : $"Không hủy được #{r.Id}: worker đã nhận yêu cầu hoặc yêu cầu đã xong.");
            await LoadAsync(showError: true);
        }
        catch (Exception ex)
        {
            ShowStatus(false, "Không hủy được yêu cầu: " + FriendlyError(ex));
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DeployRequest r) return;
        if (Directory.Exists(r.FolderPath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{r.FolderPath}\"") { UseShellExecute = true });
        else
            ShowStatus(false, $"Folder không còn tồn tại: {r.FolderPath}");
    }

    private void CopyScript_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(DeployRequestService.CreateTableScript);
        ShowStatus(true, "Đã copy script tạo bảng dbo.ToolManagerDeployRequest. Chạy script trên SQL Server (SSMS), rồi cấp quyền SELECT, INSERT, UPDATE cho tài khoản chạy Tool Manager.");
    }

    // ================== Tiện ích ==================

    private static string FriendlyError(Exception ex)
    {
        var msg = ex.Message;
        if (ex is Microsoft.Data.SqlClient.SqlException { Number: 208 })
            return msg + "\n→ Bảng dbo.ToolManagerDeployRequest chưa tồn tại: bấm \"Copy script tạo bảng\" rồi chạy trên SQL Server.";
        if (ex is Microsoft.Data.SqlClient.SqlException { Number: 229 or 230 })
            return msg + "\n→ Tài khoản chưa có quyền: cần GRANT SELECT, INSERT, UPDATE ON dbo.ToolManagerDeployRequest.";
        return WebDbSettingsWindow.FriendlyError(ex);
    }

    private void Warn(string message, Control focus)
    {
        ShowStatus(false, message);
        focus.Focus();
        if (focus is TextBox tb) tb.SelectAll();
    }

    /// <summary>ok: true = thành công, false = lỗi, null = đang xử lý.</summary>
    private void ShowStatus(bool? ok, string text)
    {
        var (bg, fg, icon) = ok switch
        {
            true => ("SuccessSoftBrush", "SuccessTextBrush", "\uE73E"),
            false => ("DangerSoftBrush", "DangerTextBrush", "\uE783"),
            _ => ("NeutralSoftBrush", "NeutralTextBrush", "\uE895"),
        };
        StatusBorder.Background = (Brush)FindResource(bg);
        StatusText.Foreground = StatusIcon.Foreground = (Brush)FindResource(fg);
        StatusIcon.Text = icon;
        StatusText.Text = text;
        StatusBorder.Visibility = Visibility.Visible;
    }
}
