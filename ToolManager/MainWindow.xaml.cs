using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ToolManager.Models;
using ToolManager.Services;

namespace ToolManager;

public partial class MainWindow : Window
{
    public ObservableCollection<ToolRunner> Runners { get; } = new();

    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _deployTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly ICollectionView _view;
    private bool _deployChecking;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        LoadTools();

        // Lọc danh sách theo ô tìm kiếm (DataGrid dùng chung view mặc định này)
        _view = CollectionViewSource.GetDefaultView(Runners);
        _view.Filter = o => o is ToolRunner r
            && (SearchBox.Text.Length == 0 || r.Config.Name.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));

        ConfigPathText.Text = $"Cấu hình: {ConfigStore.ConfigPath}";
        VersionText.Text = $"Tool Manager v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";

        _logTimer.Tick += (_, _) => FlushLogs();
        _tickTimer.Tick += (_, _) => TickAll();
        _logTimer.Start();
        _tickTimer.Start();
        _deployTimer.Tick += async (_, _) => await CheckDeployResultsAsync();

        PortsPage.Initialize(Runners, NavigateToTool);

        Loaded += async (_, _) =>
        {
            foreach (var r in Runners.Where(r => r.Config.AutoStart)) r.Start();
            if (Runners.Count > 0) ToolGrid.SelectedIndex = 0;
            UpdateSummary();
            _deployTimer.Start();
            await CheckDeployResultsAsync();
        };
    }

    private ToolRunner? Selected => ToolGrid.SelectedItem as ToolRunner;

    private static ToolRunner? RunnerOf(object sender) => (sender as FrameworkElement)?.DataContext as ToolRunner;

    // ================== Lưu / tải ==================

    private void LoadTools()
    {
        try
        {
            foreach (var cfg in ConfigStore.Load())
                Runners.Add(new ToolRunner(cfg, Dispatcher));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không đọc được tools.json:\n{ex.Message}", "Tool Manager",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveTools()
    {
        try
        {
            ConfigStore.Save(Runners.Select(r => r.Config));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không lưu được tools.json:\n{ex.Message}", "Tool Manager",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================== Timer ==================

    private void FlushLogs()
    {
        var selected = Selected;
        bool selectedHasNew = false;
        foreach (var r in Runners)
        {
            bool hasNew = r.FlushLogs();
            if (r == selected) selectedHasNew = hasNew;
        }
        if (selectedHasNew && AutoScrollCheck.IsChecked == true && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void TickAll()
    {
        foreach (var r in Runners) r.Tick();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        StatTotal.Text = Runners.Count.ToString();
        StatRunning.Text = Runners.Count(r => r.State == ToolState.Running).ToString();
        StatWaiting.Text = Runners.Count(r => r.State is ToolState.WaitingRestart or ToolState.Stopping).ToString();
        StatCrashed.Text = Runners.Count(r => r.State == ToolState.Crashed || r.HasErrors).ToString();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool noTools = Runners.Count == 0;
        EmptyState.Visibility = noTools || _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = noTools ? "Chưa có tool nào" : "Không tìm thấy tool phù hợp";
        EmptyHint.Text = noTools
            ? "Bấm “Thêm tool” để khai báo file exe cần quản lý."
            : $"Không có tool nào có tên chứa “{SearchBox.Text.Trim()}”.";
        EmptyAddButton.Visibility = noTools ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        if (_view != null) UpdateEmptyState();
    }

    // ================== Thanh công cụ ==================

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ToolEditWindow(new ToolConfig(), OtherTools(null)) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var runner = new ToolRunner(dlg.Result, Dispatcher);
        Runners.Add(runner);
        SaveTools();
        SearchBox.Text = "";
        ToolGrid.SelectedItem = runner;
        UpdateSummary();
    }

    private void DeployRequest_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.DeployRequestWindow(Runners.Select(r => r.Config.Name), ImportDeployResult) { Owner = this };
        dlg.ShowDialog();
    }

    // ================== Tạo từ repo: tự thêm tool khi yêu cầu xong ==================

    /// <summary>
    /// Mỗi 15 giây: yêu cầu "Tạo từ repo" của máy này vừa Xong => thêm tool (chưa chạy); vừa Lỗi => báo.
    /// Chưa cấu hình database / lỗi kết nối thì bỏ qua, lần sau thử lại.
    /// </summary>
    private async Task CheckDeployResultsAsync()
    {
        if (_deployChecking) return;
        var cs = DeployRequestService.GetConnectionString(out _);
        if (cs == null) return;

        _deployChecking = true;
        try
        {
            var state = ConfigStore.LoadDeployImportState();
            if (state == null)
            {
                // Lần đầu: yêu cầu đã xong từ trước không tự thêm (có thể đã thêm tay). Thêm tay trong cửa sổ "Tạo từ repo".
                var baseline = await Task.Run(() => DeployRequestService.MaxFinishedIdAsync(cs));
                ConfigStore.SaveDeployImportState(new DeployImportState { BaselineId = baseline });
                return;
            }

            var rows = await Task.Run(() => DeployRequestService.ListFinishedAsync(cs, state.BaselineId));
            var notes = new List<string>();
            foreach (var r in rows.Where(r => !state.HandledIds.Contains(r.Id)))
            {
                state.HandledIds.Add(r.Id);
                notes.Add(r.Status == DeployRequestStatus.Failed
                    ? $"Yêu cầu #{r.Id} ({r.ToolName}) bị lỗi: {Shorten(r.ErrorLog)} Xem chi tiết ở “Tạo từ repo”."
                    : ImportDeployResult(r).Message);
            }
            if (notes.Count == 0) return;

            if (state.HandledIds.Count > 500) state.HandledIds.RemoveRange(0, state.HandledIds.Count - 500);
            ConfigStore.SaveDeployImportState(state);
            ShowDeployNotice(notes);
        }
        catch
        {
            // Mất kết nối database tạm thời: lần sau thử lại
        }
        finally
        {
            _deployChecking = false;
        }
    }

    /// <summary>Thêm tool từ kết quả của yêu cầu (dùng chung cho tự thêm và nút trong cửa sổ "Tạo từ repo"). Trả về thông báo.</summary>
    private (bool Ok, string Message) ImportDeployResult(DeployRequest r)
    {
        if (Runners.FirstOrDefault(x => x.Config.DeployRequestId == r.Id) is { } existing)
            return (true, $"Tool “{existing.Config.Name}” đã có trong danh sách (yêu cầu #{r.Id}).");

        var cfg = DeployRequestService.BuildToolConfig(r, Runners.Select(x => x.Config.Name), out var warning);
        if (cfg == null) return (false, $"Yêu cầu #{r.Id} ({r.ToolName}): không thêm được tool, {warning}");

        var runner = new ToolRunner(cfg, Dispatcher);
        Runners.Add(runner);
        SaveTools();
        UpdateSummary();

        // Thêm tay từ cửa sổ "Tạo từ repo" cũng đánh dấu đã xử lý, để vòng kiểm tra tự động không báo lại
        if (ConfigStore.LoadDeployImportState() is { } st && !st.HandledIds.Contains(r.Id))
        {
            st.HandledIds.Add(r.Id);
            ConfigStore.SaveDeployImportState(st);
        }
        runner.Note($"Thêm từ yêu cầu “Tạo từ repo” #{r.Id}: {r.FolderPath}");
        runner.Note("Đọc deploy.cmd (nút Xem script), bấm Chạy deploy, chờ “=== Xong” rồi bấm Start.");
        if (warning != null) runner.Note("Lưu ý: " + warning);

        return (true, $"Đã thêm tool “{cfg.Name}” (yêu cầu #{r.Id}). Xem script, bấm Chạy deploy, xong thì Start."
                      + (warning != null ? " Lưu ý: " + warning : ""));
    }

    private void ShowDeployNotice(IEnumerable<string> lines)
    {
        DeployNoticeText.Text = string.Join(Environment.NewLine, lines);
        DeployNotice.Visibility = Visibility.Visible;
    }

    private void CloseDeployNotice_Click(object sender, RoutedEventArgs e) => DeployNotice.Visibility = Visibility.Collapsed;

    private static string Shorten(string? s)
    {
        var line = (s ?? "").Split('\n')[0].Trim();
        return line.Length <= 140 ? line : line[..140] + "...";
    }

    private void RunDeploy_Click(object sender, RoutedEventArgs e)
    {
        if (RunnerOf(sender) is not { } r) return;
        ToolGrid.SelectedItem = r;
        r.RefreshDeployInfo();
        var script = r.DeployScriptPath;
        if (!r.HasDeployScript)
        {
            MessageBox.Show($"Không thấy {script}.", "Tool Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var running = r.IsRunning
            ? "\n\nTool đang chạy: nếu cửa sổ deploy nhắc “Bam STOP…” thì bấm Stop ở Tool Manager, xong bấm Start."
            : "\n\nChạy xong (thấy “=== Xong”) thì bấm Start.";
        if (MessageBox.Show($"Chạy deploy.cmd của “{r.Config.Name}”?\n\n{script}\n\nHãy đọc script trước (nút Xem script).{running}",
                "Chạy deploy", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            // Cửa sổ cmd riêng (/k: giữ cửa sổ để đọc kết quả), người dùng thấy tiến trình build và lời nhắc Stop
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{script}\"\"")
            {
                WorkingDirectory = r.WorkDir,
                UseShellExecute = true,
            });
            r.Note("Đã mở cửa sổ chạy deploy.cmd.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không chạy được deploy.cmd:\n{ex.Message}", "Tool Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ViewDeployScript_Click(object sender, RoutedEventArgs e)
    {
        if (RunnerOf(sender) is not { } r) return;
        ToolGrid.SelectedItem = r;
        var files = new[] { "deploy.cmd", "rollback.cmd" }.Select(f => Path.Combine(r.WorkDir, f)).Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            MessageBox.Show($"Không thấy deploy.cmd trong {r.WorkDir}.", "Tool Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (var f in files) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{f}\"") { UseShellExecute = true });
    }

    private void StartAll_Click(object sender, RoutedEventArgs e)
    {
        var toStart = Runners.Where(r => r.CanStart).ToList();
        if (toStart.Count == 0) return;

        // Cảnh báo trùng port: Yes = vẫn chạy tất cả, No = bỏ qua tool bị trùng, Cancel = không chạy gì
        var conflicts = toStart
            .Select(r => (Runner: r, Items: PortService.FindConflicts(r.Config.Ports, r.Pid)))
            .Where(x => x.Items.Count > 0)
            .ToList();
        if (conflicts.Count > 0)
        {
            var lines = conflicts.SelectMany(c => c.Items.Select(i => $"• {c.Runner.Config.Name}: {i.Message}"));
            var answer = MessageBox.Show(
                "Một số tool bị trùng port:\n\n" + string.Join("\n", lines) +
                "\n\nYes = vẫn chạy tất cả\nNo = chỉ chạy các tool không bị trùng\nCancel = không chạy",
                "Tool Manager - trùng port", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.No)
                toStart = toStart.Except(conflicts.Select(c => c.Runner)).ToList();
        }

        foreach (var r in toStart)
        {
            r.ResetErrorRestartLimit();
            r.Start();
        }
    }

    /// <summary>Port của tool đang bị app khác chiếm => hỏi có chạy tiếp không. true = chạy.</summary>
    private bool ConfirmPortConflicts(ToolRunner r)
    {
        var conflicts = PortService.FindConflicts(r.Config.Ports, r.Pid);
        if (conflicts.Count == 0) return true;
        var msg = string.Join("\n", conflicts.Select(c => "• " + c.Message));
        return MessageBox.Show(
                   $"Tool “{r.Config.Name}” có thể không chạy được vì trùng port:\n\n{msg}\n\nVẫn chạy tool?",
                   "Tool Manager - trùng port", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
               == MessageBoxResult.Yes;
    }

    // ================== Chuyển tab ==================

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PortsPage == null || ToolsPage == null) return; // đang InitializeComponent
        bool ports = PortsTab.IsChecked == true;
        ToolsPage.Visibility = ports ? Visibility.Collapsed : Visibility.Visible;
        PortsPage.Visibility = ports ? Visibility.Visible : Visibility.Collapsed;
        if (ports) PortsPage.Activate();
        else PortsPage.Deactivate(); // tool vẫn chạy bình thường, chỉ dừng quét port
    }

    /// <summary>Từ tab Ports: chuyển sang tab Tools và chọn tool.</summary>
    private void NavigateToTool(ToolRunner r)
    {
        ToolsTab.IsChecked = true;
        SearchBox.Text = "";
        ToolGrid.SelectedItem = r;
        ToolGrid.ScrollIntoView(r);
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        if (!Runners.Any(r => r.CanStop)) return;
        if (MessageBox.Show("Dừng tất cả tool đang chạy?", "Tool Manager",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var r in Runners.Where(r => r.CanStop)) r.Stop();
    }

    private void OpenLogRoot_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ConfigStore.LogRoot);
        OpenInExplorer(ConfigStore.LogRoot);
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(ConfigStore.ConfigPath)) SaveTools();
        OpenInExplorer(ConfigStore.ConfigPath, select: true);
    }

    // ================== Nút trên từng dòng ==================

    // Bấm nút trên dòng nào thì chọn luôn dòng đó để khung log hiển thị đúng tool
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (RunnerOf(sender) is not { } r) return;
        ToolGrid.SelectedItem = r;
        if (!ConfirmPortConflicts(r)) return;
        r.ResetErrorRestartLimit();
        r.Start();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (RunnerOf(sender) is not { } r) return;
        ToolGrid.SelectedItem = r;
        r.Stop();
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (RunnerOf(sender) is not { } r) return;
        ToolGrid.SelectedItem = r;
        if (!ConfirmPortConflicts(r)) return;
        r.ResetErrorRestartLimit();
        r.Restart();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (RunnerOf(sender) is { } r) EditRunner(r);
    }

    private void ToolGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click vào dòng (không phải vào nút) để sửa
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) == null && Selected is { } r)
            EditRunner(r);
    }

    private void EditRunner(ToolRunner r)
    {
        var dlg = new ToolEditWindow(r.Config.Clone(), OtherTools(r)) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        r.Config = dlg.Result;
        SaveTools();
        _view.Refresh();
        UpdateEmptyState();

        if (r.IsRunning)
        {
            if (MessageBox.Show("Đã lưu. Tool đang chạy, Restart ngay để áp dụng cấu hình mới?", "Tool Manager",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                r.Restart();
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RunnerOf(sender) is not { } r) return;

        var msg = r.IsRunning
            ? $"Tool \"{r.Config.Name}\" đang chạy. Dừng và xóa khỏi danh sách?"
            : $"Xóa tool \"{r.Config.Name}\" khỏi danh sách?";
        if (MessageBox.Show(msg + "\n(File exe và file log không bị xóa.)", "Tool Manager",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        r.StopForShutdown();
        r.Dispose();
        Runners.Remove(r);
        SaveTools();
        UpdateSummary();
    }

    // ================== Khung log ==================

    private void ToolGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    /// <summary>Bấm badge "N lỗi": chọn tool, nhảy tới dòng lỗi gần nhất, đánh dấu đã xem.</summary>
    private void ErrorBadge_Click(object sender, RoutedEventArgs e)
    {
        if (RunnerOf(sender) is not { } r) return;
        ToolGrid.SelectedItem = r;
        AutoScrollCheck.IsChecked = false; // không để log mới kéo màn hình đi mất

        // Chờ ListBox đổi sang log của tool vừa chọn rồi mới cuộn
        Dispatcher.BeginInvoke(() =>
        {
            var target = r.LastErrorLine;
            if (target != null && LogList.Items.Contains(target))
            {
                LogList.SelectedItem = target;
                LogList.ScrollIntoView(target);
            }
        }, DispatcherPriority.Background);

        r.AcknowledgeErrors();
        UpdateSummary();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => Selected?.ClearScreen();

    private void OpenToolLog_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        var today = Path.Combine(r.LogFolder, $"{DateTime.Today:yyyy-MM-dd}.log");
        if (File.Exists(today)) OpenInExplorer(today, select: true);
        else if (Directory.Exists(r.LogFolder)) OpenInExplorer(r.LogFolder);
        else MessageBox.Show("Tool này chưa có file log.", "Tool Manager");
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) => CopyLog();

    private void CopyLog_Executed(object sender, ExecutedRoutedEventArgs e) => CopyLog();

    private void CopyLog()
    {
        var lines = LogList.SelectedItems.Count > 0
            ? LogList.SelectedItems.Cast<string>().OrderBy(s => LogList.Items.IndexOf(s))
            : LogList.Items.Cast<string>();
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length > 0) Clipboard.SetText(text);
    }

    // ================== Thoát app ==================

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        int running = Runners.Count(r => r.IsRunning || r.State == ToolState.WaitingRestart);
        if (running > 0 &&
            MessageBox.Show($"Đang có {running} tool chạy.\nThoát Tool Manager sẽ DỪNG tất cả tool. Tiếp tục?",
                "Tool Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _logTimer.Stop();
        _tickTimer.Stop();
        _deployTimer.Stop();
        PortsPage.Deactivate();
        foreach (var r in Runners)
        {
            r.StopForShutdown();
            r.Dispose();
        }
    }

    // ================== Tiện ích ==================

    private IEnumerable<ToolConfig> OtherTools(ToolRunner? except) =>
        Runners.Where(r => r != except).Select(r => r.Config);

    private static void OpenInExplorer(string path, bool select = false)
    {
        var args = select ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = child;
        while (current != null)
        {
            if (current is T match) return match;
            current = current is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
