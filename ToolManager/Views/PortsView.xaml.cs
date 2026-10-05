using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ToolManager.Models;
using ToolManager.Services;

namespace ToolManager.Views;

/// <summary>
/// Màn hình Ports: port trống dùng được ngay (trên đầu), kiểm tra nhanh 1 port,
/// port đã đặt trước, dải Windows giữ chỗ, và bảng port đang bị chiếm.
/// </summary>
public partial class PortsView : UserControl
{
    public ObservableCollection<int> FreePorts { get; } = new();
    public ObservableCollection<BusyPortItem> BusyPorts { get; } = new();
    public ObservableCollection<PortReservation> Reservations { get; } = new();
    public ObservableCollection<WebPortItem> WebPorts { get; } = new();

    // Danh sách web đọc từ database (cache 60 giây, bấm "Tải lại" để đọc ngay)
    private static readonly TimeSpan WebCacheTime = TimeSpan.FromSeconds(60);
    private List<WebEntry>? _webEntries;
    private DateTime _webLoadedAt = DateTime.MinValue;
    private string? _webError;
    private bool _webLoading;
    private bool _refreshAgain;
    private HashSet<string> _localHosts = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ICollectionView _busyView;

    private PortSettings _settings = new();
    private List<(int Start, int End)> _ranges = new();
    private IList<ToolRunner> _runners = Array.Empty<ToolRunner>();
    private Action<ToolRunner>? _navigateToTool;

    private string _localIp = "localhost";
    private bool _refreshing;
    private int _quickVersion; // bỏ kết quả kiểm tra nhanh cũ khi người dùng gõ tiếp
    private int? _quickPort;

    public PortsView()
    {
        InitializeComponent();
        DataContext = this;

        _busyView = CollectionViewSource.GetDefaultView(BusyPorts);
        _busyView.Filter = o => o is BusyPortItem b && (BusySearch.Text.Trim() is var f && (f.Length == 0 || b.Matches(f)));

        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBorder.Visibility = Visibility.Collapsed; };
    }

    /// <summary>Gọi 1 lần từ MainWindow.</summary>
    public void Initialize(IList<ToolRunner> runners, Action<ToolRunner> navigateToTool)
    {
        _runners = runners;
        _navigateToTool = navigateToTool;

        _settings = ConfigStore.LoadPortSettings();
        if (_settings.FreeCount < 1) _settings.FreeCount = 20;
        _ranges = PortService.ParseRanges(_settings.Ranges, out var err);
        if (err != null)
        {
            _settings.Ranges = "5000-5999";
            _ranges = PortService.ParseRanges(_settings.Ranges, out _);
        }
        RangeBox.Text = _settings.Ranges;
        SyncReservations();
        UpdateWebUi();
    }

    /// <summary>Đang mở tab Ports: quét ngay + tự làm mới mỗi 5 giây.</summary>
    public async void Activate()
    {
        // Chọn tab mặc định khi trang đã hiển thị (đặt IsChecked trong XAML lúc trang còn ẩn bị WPF bỏ chọn)
        if (WebTabRadio.IsChecked != true && BusyTabRadio.IsChecked != true) WebTabRadio.IsChecked = true;
        _refreshTimer.Start();
        await RefreshAsync();
    }

    public void Deactivate() => _refreshTimer.Stop();

    // ================== Quét ==================

    private async Task RefreshAsync()
    {
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true;
        try
        {
            if (_settings.WebDb.IsConfigured && !_webLoading && DateTime.Now - _webLoadedAt > WebCacheTime)
                _ = LoadWebsAsync(); // đọc database ở nền, xong sẽ tự làm mới lại

            // Lấy dữ liệu của UI trước khi sang thread nền
            var runnerPids = _runners.Where(r => r.Pid != null).Select(r => (Pid: r.Pid!.Value, Runner: r)).ToList();
            var webEntries = _webEntries ?? new List<WebEntry>();
            var ranges = _ranges;
            int count = _settings.FreeCount;
            var reservedPorts = _settings.Reservations.Select(r => r.Port).ToList();
            var declaredPorts = _runners.SelectMany(r => r.Config.Ports).ToList();

            var result = await Task.Run(() =>
            {
                // Web có IP/tên máy trùng với máy này
                var localHosts = WebRegistryService.GetLocalHosts();
                var localWebs = webEntries.Where(w => localHosts.Contains(w.Host)).ToList();

                // Port trống: bỏ qua port khai báo cho tool, port đặt trước, port của web trên máy này
                var skip = declaredPorts.Concat(reservedPorts).Concat(localWebs.Select(w => w.Port)).ToHashSet();

                var snap = PortService.TakeSnapshot();
                var free = PortService.FindFreePorts(snap, ranges, count, skip);
                var busy = snap.Listeners
                    .GroupBy(l => (l.Port, l.Pid))
                    .Select(g => new BusyPortItem
                    {
                        Port = g.Key.Port,
                        Pid = g.Key.Pid,
                        Addresses = string.Join(", ", g.Select(l => l.Address).Distinct()),
                        AppName = snap.GetName(g.Key.Pid),
                        Path = snap.GetPath(g.Key.Pid),
                        Tool = runnerPids.FirstOrDefault(rp => snap.IsInTree(g.Key.Pid, rp.Pid)).Runner,
                    })
                    .OrderBy(b => b.Port).ThenBy(b => b.Pid)
                    .ToList();

                var webs = localWebs
                    .Select(w =>
                    {
                        var owners = busy.Where(b => b.Port == w.Port).ToList();
                        return new WebPortItem
                        {
                            Web = w,
                            Owner = string.Join(", ", owners.Select(o => $"{o.AppName} (PID {o.Pid})")),
                            Tool = owners.Select(o => o.Tool).FirstOrDefault(t => t != null),
                        };
                    })
                    .OrderBy(w => w.Port).ThenBy(w => w.Name)
                    .ToList();

                return (Free: free, Busy: busy, Webs: webs, Excluded: snap.ExcludedRanges, Ip: PortService.GetLocalIp(), Hosts: localHosts);
            });
            _localHosts = result.Hosts;

            _localIp = result.Ip;
            LocalIpText.Text = _localIp;
            UpdatedText.Text = $"Cập nhật lúc {DateTime.Now:HH:mm:ss}";

            SyncList(FreePorts, result.Free, p => p);
            SyncList(BusyPorts, result.Busy, b => b.Key);
            SyncList(WebPorts, result.Webs, w => w.Key);
            UpdateWebUi();

            ExcludedList.ItemsSource = result.Excluded.Select(r => r.Start == r.End ? $"{r.Start}" : $"{r.Start} - {r.End}").ToList();
            ExcludedEmpty.Visibility = result.Excluded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateSummaries();
            if (_quickPort.HasValue) await RunQuickCheckAsync(_quickPort.Value);
        }
        catch (Exception ex)
        {
            UpdatedText.Text = $"Lỗi khi quét port: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
        if (_refreshAgain)
        {
            _refreshAgain = false;
            await RefreshAsync();
        }
    }

    // ================== Web trên máy này (từ SQL Server) ==================

    private async Task LoadWebsAsync()
    {
        if (_webLoading) return;
        var cs = WebRegistryService.Unprotect(_settings.WebDb.ProtectedConnection);
        if (cs == null)
        {
            _webError = "Không giải mã được cấu hình database (có thể file được copy từ máy hoặc tài khoản Windows khác). Hãy cấu hình lại.";
            _webLoadedAt = DateTime.Now;
            UpdateWebUi();
            return;
        }

        _webLoading = true;
        UpdateWebUi();
        try
        {
            var query = _settings.WebDb.Query;
            _webEntries = await Task.Run(() => WebRegistryService.LoadAsync(cs, query));
            _webError = null;
        }
        catch (Exception ex)
        {
            _webError = WebDbSettingsWindow.FriendlyError(ex);
        }
        finally
        {
            _webLoading = false;
            _webLoadedAt = DateTime.Now;
        }
        await RefreshAsync();
    }

    private void UpdateWebUi()
    {
        var db = _settings.WebDb;
        int total = _webEntries?.Count ?? 0;
        string ips = string.Join(", ", _localHosts.Where(h => h.Contains('.') && char.IsDigit(h[0])).Take(4));

        WebTabText.Text = $"Web trên máy này ({WebPorts.Count})";
        WebSummary.Text = !db.IsConfigured
            ? "Chưa kết nối database danh sách web"
            : _webLoading && _webEntries == null
                ? "Đang đọc danh sách web từ database..."
                : $"Nguồn: {DescribeSource()} · đọc lúc {_webLoadedAt:HH:mm:ss} · {WebPorts.Count} web trên máy này / {total} web trong database";

        // Thông báo thay cho bảng
        string? title = null, detail = null, icon = "";
        bool showButton = false;
        if (!db.IsConfigured)
        {
            title = "Chưa cấu hình database danh sách web";
            detail = "Kết nối tới SQL Server chứa bảng Tên web + URL. Tool Manager sẽ liệt kê các web có IP trùng với máy này.";
            showButton = true;
        }
        else if (_webError != null)
        {
            title = "Không đọc được danh sách web";
            detail = _webError;
            icon = "";
            showButton = true;
        }
        else if (_webEntries == null)
        {
            title = "Đang đọc danh sách web...";
        }
        else if (WebPorts.Count == 0)
        {
            title = "Không có web nào trên máy này";
            detail = $"Database có {total} web nhưng không có URL nào trùng IP / tên máy này ({ips}, {Environment.MachineName}).";
        }

        WebMessagePanel.Visibility = title != null ? Visibility.Visible : Visibility.Collapsed;
        WebGrid.Visibility = title != null ? Visibility.Collapsed : Visibility.Visible;
        WebMessageTitle.Text = title ?? "";
        WebMessageDetail.Text = detail ?? "";
        WebMessageDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        WebMessageIcon.Text = icon;
        WebMessageButton.Visibility = showButton ? Visibility.Visible : Visibility.Collapsed;
    }

    private string DescribeSource()
    {
        var cs = WebRegistryService.Unprotect(_settings.WebDb.ProtectedConnection);
        return cs == null ? "?" : WebRegistryService.Describe(cs);
    }

    private async void ReloadWebs_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.WebDb.IsConfigured)
        {
            ConfigureWebDb_Click(sender, e);
            return;
        }
        await LoadWebsAsync();
    }

    private async void ConfigureWebDb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new WebDbSettingsWindow(_settings.WebDb) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        _settings.WebDb = dlg.Result;
        SaveSettings();
        _webEntries = null;
        _webError = null;
        _webLoadedAt = DateTime.MinValue;
        WebPorts.Clear();
        UpdateWebUi();
        if (_settings.WebDb.IsConfigured) await LoadWebsAsync();
        else await RefreshAsync();
    }

    private void BottomTab_Checked(object sender, RoutedEventArgs e)
    {
        if (WebPanel == null || BusyPanel == null) return; // đang InitializeComponent
        bool web = WebTabRadio.IsChecked == true;
        WebPanel.Visibility = WebActions.Visibility = WebSummary.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
        BusyPanel.Visibility = BusySearchPanel.Visibility = BusySummary.Visibility = web ? Visibility.Collapsed : Visibility.Visible;
    }

    private static WebPortItem? WebOf(object sender) => (sender as FrameworkContentElement)?.DataContext as WebPortItem
                                                        ?? (sender as FrameworkElement)?.DataContext as WebPortItem;

    private void OpenWeb_Click(object sender, RoutedEventArgs e) => OpenWeb(WebOf(sender));

    private void OpenWebButton_Click(object sender, RoutedEventArgs e) => OpenWeb(WebOf(sender));

    private void OpenWeb(WebPortItem? item)
    {
        if (item == null) return;
        try
        {
            if (!WebRegistryService.TryOpenInBrowser(item.Url))
                MessageBox.Show($"URL không hợp lệ: {item.Url}", "Tool Manager");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không mở được trình duyệt: {ex.Message}", "Tool Manager");
        }
    }

    private void CopyWebUrl_Click(object sender, RoutedEventArgs e)
    {
        if (WebOf(sender) is { } item) CopyText(item.Url);
    }

    private void GoToWebTool_Click(object sender, RoutedEventArgs e)
    {
        if (WebOf(sender)?.Tool is { } tool) _navigateToTool?.Invoke(tool);
    }

    /// <summary>Chỉ thay danh sách khi có thay đổi, để không mất vị trí cuộn / dòng đang chọn.</summary>
    private static void SyncList<T, TKey>(ObservableCollection<T> target, List<T> source, Func<T, TKey> key)
    {
        if (target.Select(key).SequenceEqual(source.Select(key))) return;
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private void UpdateSummaries()
    {
        FreeSummary.Text = FreePorts.Count > 0
            ? $"{FreePorts.Count} port nhỏ nhất còn trống · đã loại port < 1024, port của tool, port đặt trước, port của web trên máy này"
            : $"Khoảng {_settings.Ranges}";
        FreeEmpty.Visibility = FreePorts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FreeEmptyText.Text = $"Không còn port trống nào trong khoảng {_settings.Ranges}.\nHãy mở rộng hoặc đổi khoảng port.";

        int shown = BusyPorts.Count(b => _busyView.Filter(b));
        BusySummary.Text = $"{BusyPorts.Count} port đang mở trên máy · {BusyPorts.Count(b => b.IsManaged)} port của tool trong Tool Manager";
        BusyEmpty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        BusyEmpty.Text = BusyPorts.Count == 0 ? "Không đọc được danh sách port." : $"Không có port nào khớp với “{BusySearch.Text.Trim()}”.";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ================== Khoảng port ==================

    private async void ApplyRange_Click(object sender, RoutedEventArgs e) => await ApplyRangeAsync();

    private async void RangeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await ApplyRangeAsync();
    }

    private async Task ApplyRangeAsync()
    {
        var ranges = PortService.ParseRanges(RangeBox.Text, out var err);
        if (err != null)
        {
            RangeError.Text = err;
            RangeError.Visibility = Visibility.Visible;
            return;
        }
        RangeError.Visibility = Visibility.Collapsed;
        _ranges = ranges;
        _settings.Ranges = string.Join(", ", ranges.Select(r => r.Start == r.End ? $"{r.Start}" : $"{r.Start}-{r.End}"));
        RangeBox.Text = _settings.Ranges;
        SaveSettings();
        await RefreshAsync();
    }

    // ================== Copy / đặt trước ==================

    private static int? PortOf(object sender) => (sender as FrameworkElement)?.Tag as int?;

    private void CopyPort_Click(object sender, RoutedEventArgs e)
    {
        if (PortOf(sender) is int port) CopyText(port.ToString());
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (PortOf(sender) is int port) CopyText($"http://{_localIp}:{port}");
    }

    private async void Reserve_Click(object sender, RoutedEventArgs e)
    {
        if (PortOf(sender) is int port) await ReserveAsync(port);
    }

    private void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            ShowToast($"Đã copy {text}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không copy được: {ex.Message}", "Tool Manager");
        }
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private async Task ReserveAsync(int port)
    {
        if (_settings.Reservations.Any(r => r.Port == port))
        {
            MessageBox.Show($"Port {port} đã được đặt trước rồi.", "Tool Manager");
            return;
        }
        var dlg = new PortReserveWindow(port) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _settings.Reservations.Add(new PortReservation
        {
            Port = port,
            Note = dlg.Note,
            CreatedBy = Environment.UserName,
            CreatedAt = DateTime.Now,
        });
        _settings.Reservations.Sort((a, b) => a.Port.CompareTo(b.Port));
        SaveSettings();
        SyncReservations();
        ShowToast($"Đã đặt trước port {port}");
        await RefreshAsync();
    }

    private async void RemoveReservation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PortReservation r) return;
        if (MessageBox.Show($"Bỏ đặt trước port {r.Port} ({r.Note})?", "Tool Manager",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _settings.Reservations.Remove(r);
        SaveSettings();
        SyncReservations();
        await RefreshAsync();
    }

    private void SyncReservations()
    {
        Reservations.Clear();
        foreach (var r in _settings.Reservations) Reservations.Add(r);
        ReservedEmpty.Visibility = Reservations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReservedCount.Text = Reservations.Count > 0 ? $"{Reservations.Count} port" : "";
    }

    private void SaveSettings()
    {
        try
        {
            ConfigStore.SavePortSettings(_settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không lưu được ports.json:\n{ex.Message}", "Tool Manager",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================== Kiểm tra nhanh ==================

    private async void QuickBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = QuickBox.Text.Trim();
        if (text.Length == 0)
        {
            _quickPort = null;
            _quickVersion++;
            QuickResult.Visibility = Visibility.Collapsed;
            return;
        }
        if (!int.TryParse(text, out var port) || port < 1 || port > 65535)
        {
            _quickPort = null;
            _quickVersion++;
            ShowQuick(PortCheckKind.Invalid, "Port không hợp lệ", "Port phải là số từ 1 đến 65535.", showActions: false);
            return;
        }
        _quickPort = port;
        await RunQuickCheckAsync(port);
    }

    private async Task RunQuickCheckAsync(int port)
    {
        int version = ++_quickVersion;
        var runnerPids = _runners.Where(r => r.Pid != null).Select(r => (Pid: r.Pid!.Value, Runner: r)).ToList();
        var declaredBy = _runners.FirstOrDefault(r => r.Config.Ports.Contains(port));
        var reservation = _settings.Reservations.FirstOrDefault(r => r.Port == port);

        var (snap, bindError) = await Task.Run(() =>
        {
            var s = PortService.TakeSnapshot();
            return (s, PortService.TryBind(port));
        });
        if (version != _quickVersion) return; // người dùng đã gõ port khác

        var owners = snap.Listeners.Where(l => l.Port == port).GroupBy(l => l.Pid).ToList();
        if (owners.Count > 0)
        {
            var lines = owners.Select(g =>
            {
                var tool = runnerPids.FirstOrDefault(rp => snap.IsInTree(g.Key, rp.Pid)).Runner;
                var addr = string.Join(", ", g.Select(l => l.Address).Distinct());
                return $"{snap.GetName(g.Key)} (PID {g.Key}) · {addr}" + (tool != null ? $" · tool “{tool.Config.Name}” trong Tool Manager" : "");
            });
            ShowQuick(PortCheckKind.Busy, $"Port {port} đang bị chiếm", string.Join("\n", lines), showActions: false);
        }
        else if (snap.FindExcluded(port) is { } range)
        {
            ShowQuick(PortCheckKind.Busy, $"Windows đang giữ chỗ port {port}",
                $"Nằm trong dải {range.Start}-{range.End} (Hyper-V / WSL / Docker). App sẽ không mở được port này dù không ai đang dùng.",
                showActions: false);
        }
        else if (bindError != null)
        {
            ShowQuick(PortCheckKind.Busy, $"Không mở được port {port}",
                $"Windows báo lỗi: {bindError}. Có thể port đang được dùng ở trạng thái khác hoặc cần quyền Administrator.",
                showActions: false);
        }
        else if (reservation != null)
        {
            ShowQuick(PortCheckKind.Warning, $"Port {port} trống nhưng đã được đặt trước",
                $"Dành cho: {reservation.Note} ({reservation.Info}).", showActions: true, canReserve: false);
        }
        else if (declaredBy != null)
        {
            ShowQuick(PortCheckKind.Warning, $"Port {port} trống nhưng đã khai báo cho tool “{declaredBy.Config.Name}”",
                "Tool đang tắt, khi chạy tool sẽ dùng port này.", showActions: true);
        }
        else if (port < PortService.MinUserPort)
        {
            ShowQuick(PortCheckKind.Warning, $"Port {port} đang trống (port hệ thống)",
                "Port < 1024 thường cần quyền Administrator, nên dùng port từ 1024 trở lên.", showActions: true);
        }
        else
        {
            ShowQuick(PortCheckKind.Free, $"Port {port} đang trống, dùng được ngay",
                "Không có app nào đang mở port này và Windows cho phép mở.", showActions: true);
        }
    }

    private void ShowQuick(PortCheckKind kind, string title, string detail, bool showActions, bool canReserve = true)
    {
        var (bg, fg, icon) = kind switch
        {
            PortCheckKind.Free => ("SuccessSoftBrush", "SuccessTextBrush", ""),
            PortCheckKind.Warning => ("WarningSoftBrush", "WarningTextBrush", ""),
            PortCheckKind.Busy => ("DangerSoftBrush", "DangerTextBrush", ""),
            _ => ("NeutralSoftBrush", "NeutralTextBrush", ""),
        };
        QuickResult.Background = (Brush)FindResource(bg);
        QuickTitle.Foreground = QuickIcon.Foreground = (Brush)FindResource(fg);
        QuickIcon.Text = icon;
        QuickTitle.Text = title;
        QuickDetail.Text = detail;
        QuickDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        QuickActions.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        QuickReserveButton.Visibility = canReserve ? Visibility.Visible : Visibility.Collapsed;
        QuickResult.Visibility = Visibility.Visible;
    }

    private void QuickCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_quickPort is int port) CopyText(port.ToString());
    }

    private void QuickCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_quickPort is int port) CopyText($"http://{_localIp}:{port}");
    }

    private async void QuickReserve_Click(object sender, RoutedEventArgs e)
    {
        if (_quickPort is int port) await ReserveAsync(port);
    }

    // ================== Bảng port đang bị chiếm ==================

    private void BusySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _busyView.Refresh();
        UpdateSummaries();
    }

    private static BusyPortItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as BusyPortItem;

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender)?.Path is not { } path) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void GoToTool_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender)?.Tool is { } tool) _navigateToTool?.Invoke(tool);
    }

    private async void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;

        // Tool của Tool Manager: dừng qua Tool Manager (không bị tự chạy lại)
        if (item.Tool is { } tool)
        {
            if (MessageBox.Show($"Dừng tool “{tool.Config.Name}” đang giữ port {item.Port}?", "Tool Manager",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            tool.Stop();
            await Task.Delay(800);
            await RefreshAsync();
            return;
        }

        if (PortService.WhyCannotKill(item.Pid, item.AppName, item.Path) is { } reason)
        {
            MessageBox.Show($"Không cho phép kết thúc “{item.AppName}” (PID {item.Pid}) từ Tool Manager.\n{reason}",
                "Tool Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var msg = $"Kết thúc app đang giữ port {item.Port}?\n\n" +
                  $"App: {item.AppName} (PID {item.Pid})\n" +
                  $"Đường dẫn: {item.PathDisplay}\n\n" +
                  "App sẽ bị tắt ngay, dữ liệu chưa lưu của app sẽ mất. Chỉ làm khi chắc chắn đây là app của bạn.";
        if (MessageBox.Show(msg, "Tool Manager - xác nhận kết thúc app", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        try
        {
            using var p = Process.GetProcessById(item.Pid);
            p.Kill();
            p.WaitForExit(3000);
            ShowToast($"Đã kết thúc {item.AppName}");
        }
        catch (ArgumentException)
        {
            // App đã tự thoát
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không kết thúc được {item.AppName}:\n{ex.Message}\n\n" +
                            "Nếu app chạy bằng quyền Administrator, hãy chạy Tool Manager bằng quyền Administrator.",
                "Tool Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await RefreshAsync();
    }
}
