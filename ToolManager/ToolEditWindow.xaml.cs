using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ToolManager.Models;

namespace ToolManager;

public partial class ToolEditWindow : Window
{
    private readonly HashSet<string> _otherNames;
    private readonly List<ToolConfig> _otherTools;

    public ToolConfig Result { get; }

    public ToolEditWindow(ToolConfig config, IEnumerable<ToolConfig> otherTools)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 40; // màn hình thấp thì phần thân form tự cuộn
        Result = config;
        _otherTools = otherTools.ToList();
        _otherNames = new HashSet<string>(_otherTools.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        bool isNew = string.IsNullOrEmpty(config.Name);
        Title = isNew ? "Thêm tool" : $"Sửa tool - {config.Name}";
        HeaderTitle.Text = isNew ? "Thêm tool mới" : $"Sửa tool: {config.Name}";
        HeaderIcon.Text = isNew ? "" : "";

        NameBox.Text = config.Name;
        ExeBox.Text = config.ExePath;
        ArgsBox.Text = config.Arguments;
        PortsBox.Text = config.PortsText;
        WorkDirBox.Text = config.WorkingDirectory;
        AutoStartCheck.IsChecked = config.AutoStart;
        AutoRestartCheck.IsChecked = config.AutoRestart;
        DelayBox.Text = config.RestartDelaySeconds.ToString();
        HideWindowCheck.IsChecked = config.HideWindow;
        RestartOnErrorCheck.IsChecked = config.RestartOnError;
        ErrorCountBox.Text = Math.Max(1, config.RestartOnErrorCount).ToString();
        if (string.Equals(config.OutputEncoding, "oem", StringComparison.OrdinalIgnoreCase)) OemRadio.IsChecked = true;
        else Utf8Radio.IsChecked = true;

        Loaded += (_, _) => NameBox.Focus();
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Chọn file chạy của tool",
            Filter = "Chương trình (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|Tất cả file (*.*)|*.*",
        };
        if (File.Exists(ExeBox.Text)) dlg.InitialDirectory = Path.GetDirectoryName(ExeBox.Text);
        if (dlg.ShowDialog(this) != true) return;

        ExeBox.Text = dlg.FileName;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
    }

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Chọn thư mục làm việc" };
        if (Directory.Exists(WorkDirBox.Text)) dlg.InitialDirectory = WorkDirBox.Text;
        else if (File.Exists(ExeBox.Text)) dlg.InitialDirectory = Path.GetDirectoryName(ExeBox.Text);
        if (dlg.ShowDialog(this) == true) WorkDirBox.Text = dlg.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var exe = ExeBox.Text.Trim().Trim('"');
        var workDir = WorkDirBox.Text.Trim().Trim('"');

        if (name.Length == 0) { Warn("Chưa nhập tên tool.", NameBox); return; }
        if (_otherNames.Contains(name)) { Warn("Tên này đã có trong danh sách, hãy đặt tên khác.", NameBox); return; }
        if (!File.Exists(exe)) { Warn("Không tìm thấy file chạy.", ExeBox); return; }
        if (workDir.Length > 0 && !Directory.Exists(workDir)) { Warn("Thư mục làm việc không tồn tại.", WorkDirBox); return; }
        if (!int.TryParse(DelayBox.Text.Trim(), out var delay) || delay < 1 || delay > 3600)
        {
            Warn("Số giây chờ chạy lại phải từ 1 đến 3600.", DelayBox);
            return;
        }
        if (!int.TryParse(ErrorCountBox.Text.Trim(), out var errorCount) || errorCount < 1 || errorCount > 1000)
        {
            Warn("Số lỗi để restart phải từ 1 đến 1000.", ErrorCountBox);
            return;
        }
        if (!TryParsePorts(PortsBox.Text, out var ports))
        {
            Warn("Port phải là số từ 1 đến 65535, nhiều port cách nhau bằng dấu phẩy.", PortsBox);
            return;
        }

        // Port đã khai báo cho tool khác: chỉ cảnh báo, vẫn cho lưu nếu người dùng muốn
        var dup = ports
            .SelectMany(p => _otherTools.Where(t => t.Ports.Contains(p)).Select(t => $"• Port {p} đã khai báo cho tool “{t.Name}”"))
            .ToList();
        if (dup.Count > 0 &&
            MessageBox.Show(this, string.Join("\n", dup) + "\n\nHai tool chạy cùng lúc sẽ bị trùng port. Vẫn lưu?",
                "Trùng port", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            PortsBox.Focus();
            return;
        }

        Result.Name = name;
        Result.ExePath = exe;
        Result.Arguments = ArgsBox.Text.Trim();
        Result.Ports = ports;
        Result.WorkingDirectory = workDir;
        Result.AutoStart = AutoStartCheck.IsChecked == true;
        Result.AutoRestart = AutoRestartCheck.IsChecked == true;
        Result.RestartDelaySeconds = delay;
        Result.HideWindow = HideWindowCheck.IsChecked == true;
        // Không có log thì không phát hiện được lỗi
        Result.RestartOnError = Result.HideWindow && RestartOnErrorCheck.IsChecked == true;
        Result.RestartOnErrorCount = errorCount;
        Result.OutputEncoding = OemRadio.IsChecked == true ? "oem" : "utf-8";

        DialogResult = true;
    }

    /// <summary>"5044, 5045" => [5044, 5045]. Ô trống => danh sách rỗng.</summary>
    private static bool TryParsePorts(string text, out List<int> ports)
    {
        ports = new List<int>();
        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var p) || p < 1 || p > 65535) return false;
            if (!ports.Contains(p)) ports.Add(p);
        }
        return true;
    }

    /// <summary>Hiện lỗi ngay trên form (footer) thay vì bật MessageBox.</summary>
    private void Warn(string message, Control focus)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        focus.Focus();
        if (focus is TextBox tb) tb.SelectAll();
    }
}
