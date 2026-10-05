using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ToolManager.Models;

namespace ToolManager.Services;

public enum ToolState { Stopped, Running, Stopping, WaitingRestart, Crashed }

/// <summary>
/// Quản lý vòng đời của 1 tool: start / stop / restart, tự restart khi crash, hút log.
/// Mọi thuộc tính binding chỉ được thay đổi trên UI thread.
/// </summary>
public sealed partial class ToolRunner : ObservableObject, IDisposable
{
    public const int MaxLinesOnScreen = 2000;

    // Tool chạy dưới mốc này rồi thoát thì tính là "crash nhanh"
    private static readonly TimeSpan FastCrashWindow = TimeSpan.FromSeconds(30);
    private const int MaxFastCrashes = 5;

    private readonly Dispatcher _ui;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly object _fileLock = new();
    private StreamWriter? _logWriter;
    private DateTime _logDate;

    private Process? _proc;
    private bool _manualStop;
    private bool _restartAfterExit;
    private int _fastCrashCount;
    private CancellationTokenSource? _restartCts;

    // Restart khi log có lỗi: tối đa MaxErrorRestarts lần trong ErrorRestartWindow, quá thì tạm ngừng
    private static readonly TimeSpan ErrorRestartWindow = TimeSpan.FromMinutes(10);
    private const int MaxErrorRestarts = 3;
    private readonly Queue<DateTime> _errorRestartTimes = new();
    private int _errorsSinceStart;
    private bool _errorRestartArmed;      // đã đủ số lỗi, chờ stack trace ghi xong rồi restart
    private bool _restartForError;        // đang dừng tool để restart vì lỗi
    private bool _errorRestartSuspended;  // đã báo "tạm ngừng" để không báo lặp lại

    public ToolRunner(ToolConfig config, Dispatcher ui)
    {
        _config = config;
        _ui = ui;
    }

    public ObservableCollection<string> Logs { get; } = new();

    private ToolConfig _config;
    public ToolConfig Config
    {
        get => _config;
        set
        {
            if (!SetField(ref _config, value)) return;
            // Đổi tên tool => log ghi sang thư mục mới
            lock (_fileLock) { _logWriter?.Dispose(); _logWriter = null; }
        }
    }

    private ToolState _state = ToolState.Stopped;
    public ToolState State
    {
        get => _state;
        private set
        {
            if (!SetField(ref _state, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(IsRunning));
        }
    }

    public bool IsRunning => State is ToolState.Running or ToolState.Stopping;
    public bool CanStart => State is ToolState.Stopped or ToolState.Crashed;
    public bool CanStop => State is ToolState.Running or ToolState.WaitingRestart;

    public string StatusText => State switch
    {
        ToolState.Running => "Đang chạy",
        ToolState.Stopping => "Đang dừng...",
        ToolState.WaitingRestart => $"Chờ chạy lại ({Config.RestartDelaySeconds}s)",
        ToolState.Crashed => "Lỗi / đã thoát",
        _ => "Đã dừng",
    };

    private int? _pid;
    public int? Pid { get => _pid; private set => SetField(ref _pid, value); }

    private DateTime? _startedAt;

    private string _uptime = "";
    public string Uptime { get => _uptime; private set => SetField(ref _uptime, value); }

    private int _restartCount;
    public int RestartCount { get => _restartCount; private set => SetField(ref _restartCount, value); }

    private string _lastExit = "";
    public string LastExit { get => _lastExit; private set => SetField(ref _lastExit, value); }

    public string LogFolder => Path.Combine(ConfigStore.LogRoot, SafeFileName(Config.Name));

    // ================== Lỗi phát hiện trong log ==================
    // Tool vẫn chạy nhưng log có lỗi (vd: ASP.NET bắt exception trong request, trả 500)

    private int _errorCount;
    /// <summary>Số lần lỗi từ lần "đã xem" gần nhất. Một chuỗi dòng lỗi liền nhau (stack trace) tính là 1.</summary>
    public int ErrorCount
    {
        get => _errorCount;
        private set
        {
            if (!SetField(ref _errorCount, value)) return;
            OnPropertyChanged(nameof(HasErrors));
            OnPropertyChanged(nameof(ErrorBadgeText));
        }
    }

    public bool HasErrors => ErrorCount > 0;
    public string ErrorBadgeText => ErrorCount > 99 ? "99+ lỗi" : $"{ErrorCount} lỗi";

    private string _errorTooltip = "";
    public string ErrorTooltip { get => _errorTooltip; private set => SetField(ref _errorTooltip, value); }

    /// <summary>Dòng đầu tiên của lần lỗi gần nhất, dùng để nhảy tới trong khung log.</summary>
    public string? LastErrorLine { get; private set; }

    private DateTime _lastErrorLineAt = DateTime.MinValue;

    /// <summary>Đánh dấu đã xem: xóa bộ đếm lỗi.</summary>
    public void AcknowledgeErrors() => ErrorCount = 0;

    // ================== Điều khiển ==================

    public void Start()
    {
        if (_proc != null) return;
        CancelPendingRestart();
        _manualStop = false;
        _restartAfterExit = false;
        _restartForError = false;
        _errorRestartArmed = false;
        _errorsSinceStart = 0;

        var cfg = Config;
        if (!File.Exists(cfg.ExePath))
        {
            AppendSystem($"Không tìm thấy file: {cfg.ExePath}");
            State = ToolState.Crashed;
            return;
        }

        // Mọi lần start (kể cả tự chạy lại) đều ghi cảnh báo trùng port vào log
        foreach (var c in PortService.FindConflicts(cfg.Ports, null))
            AppendSystem($"Cảnh báo: {c.Message}. Tool có thể không mở được port.");

        var workDir = string.IsNullOrWhiteSpace(cfg.WorkingDirectory)
            ? Path.GetDirectoryName(cfg.ExePath)!
            : cfg.WorkingDirectory;

        var psi = new ProcessStartInfo(cfg.ExePath, cfg.Arguments ?? "")
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
        };

        if (cfg.HideWindow)
        {
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            var enc = ResolveEncoding(cfg.OutputEncoding);
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;
        }

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (cfg.HideWindow)
        {
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Append(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Append("[ERR] " + e.Data); };
        }
        p.Exited += (_, _) => OnProcessExited(p);

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            AppendSystem($"Không khởi động được: {ex.Message}");
            p.Dispose();
            State = ToolState.Crashed;
            return;
        }

        JobObject.Assign(p);
        if (cfg.HideWindow)
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        _proc = p;
        _startedAt = DateTime.Now;
        Pid = p.Id;
        State = ToolState.Running;
        Tick();
        AppendSystem($"Đã khởi động (PID {p.Id}) {cfg.ExePath} {cfg.Arguments}".TrimEnd());
    }

    public void Stop()
    {
        _restartAfterExit = false;
        _restartForError = false;
        StopCore();
    }

    private void StopCore()
    {
        _errorRestartArmed = false;
        if (State == ToolState.WaitingRestart)
        {
            CancelPendingRestart();
            State = ToolState.Stopped;
            AppendSystem("Đã hủy lịch chạy lại.");
            return;
        }
        if (_proc == null) return;

        _manualStop = true;
        State = ToolState.Stopping;
        AppendSystem("Đang dừng tool...");
        try
        {
            _proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppendSystem($"Lỗi khi dừng: {ex.Message}");
        }
    }

    public void Restart()
    {
        if (_proc == null)
        {
            Start();
            return;
        }
        _restartForError = false;
        _restartAfterExit = true;
        StopCore();
    }

    /// <summary>Người dùng tự Start/Restart: cho phép tự restart vì lỗi lại từ đầu.</summary>
    public void ResetErrorRestartLimit()
    {
        _errorRestartTimes.Clear();
        _errorRestartSuspended = false;
    }

    private void TryRestartForError()
    {
        var now = DateTime.Now;
        while (_errorRestartTimes.Count > 0 && now - _errorRestartTimes.Peek() > ErrorRestartWindow)
            _errorRestartTimes.Dequeue();

        if (_errorRestartTimes.Count >= MaxErrorRestarts)
        {
            if (!_errorRestartSuspended)
            {
                AppendSystem($"Đã tự restart vì lỗi {MaxErrorRestarts} lần trong {ErrorRestartWindow.TotalMinutes:0} phút nhưng vẫn lỗi. " +
                             "Tạm ngừng tự restart vì lỗi (tool vẫn chạy), hãy kiểm tra log.");
                _errorRestartSuspended = true;
            }
            return;
        }

        _errorRestartSuspended = false;
        _errorRestartTimes.Enqueue(now);
        AppendSystem($"Phát hiện {_errorsSinceStart} lỗi trong log => tự restart " +
                     $"(lần {_errorRestartTimes.Count}/{MaxErrorRestarts} trong {ErrorRestartWindow.TotalMinutes:0} phút).");
        _restartAfterExit = false;
        _restartForError = true;
        StopCore();
    }

    /// <summary>Dùng khi thoát app: dừng đồng bộ, chờ tối đa vài giây.</summary>
    public void StopForShutdown()
    {
        CancelPendingRestart();
        var p = _proc;
        if (p == null) return;
        _manualStop = true;
        try
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
        }
        catch { /* app đang thoát, Job Object sẽ dọn nốt */ }
    }

    // ================== Xử lý khi tool thoát ==================

    private void OnProcessExited(Process p)
    {
        // Chạy trên thread pool. WaitForExit() đảm bảo đã đọc hết output còn lại.
        int code;
        try
        {
            p.WaitForExit();
            code = p.ExitCode;
        }
        catch
        {
            code = -1;
        }
        _ui.BeginInvoke(() => HandleExit(p, code));
    }

    private void HandleExit(Process p, int code)
    {
        if (!ReferenceEquals(p, _proc)) return;

        var ranFor = _startedAt.HasValue ? DateTime.Now - _startedAt.Value : TimeSpan.Zero;
        _proc = null;
        p.Dispose();
        Pid = null;
        _startedAt = null;
        Uptime = "";
        LastExit = $"{DateTime.Now:dd/MM HH:mm:ss} (mã {code})";

        if (_manualStop)
        {
            AppendSystem("Đã dừng.");
            State = ToolState.Stopped;
            _fastCrashCount = 0;
            if (_restartForError)
            {
                _restartForError = false;
                ScheduleRestart(); // chờ RestartDelaySeconds như khi crash
                return;
            }
            if (_restartAfterExit)
            {
                _restartAfterExit = false;
                Start();
            }
            return;
        }

        AppendSystem($"Tool tự thoát với mã {code} sau {FormatDuration(ranFor)}.");

        if (!Config.AutoRestart)
        {
            State = code == 0 ? ToolState.Stopped : ToolState.Crashed;
            return;
        }

        _fastCrashCount = ranFor < FastCrashWindow ? _fastCrashCount + 1 : 0;
        if (_fastCrashCount >= MaxFastCrashes)
        {
            AppendSystem($"Tool bị tắt {MaxFastCrashes} lần liên tiếp ngay sau khi chạy. Ngừng tự chạy lại, hãy kiểm tra log.");
            _fastCrashCount = 0;
            State = ToolState.Crashed;
            return;
        }

        ScheduleRestart();
    }

    private void ScheduleRestart()
    {
        var delay = Math.Max(1, Config.RestartDelaySeconds);
        State = ToolState.WaitingRestart;
        AppendSystem($"Sẽ chạy lại sau {delay}s...");

        var cts = new CancellationTokenSource();
        _restartCts = cts;
        Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _ui.BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested || State != ToolState.WaitingRestart) return;
                RestartCount++;
                Start();
            });
        }, TaskScheduler.Default);
    }

    private void CancelPendingRestart()
    {
        _restartCts?.Cancel();
        _restartCts = null;
    }

    // ================== Log ==================

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();

    /// <summary>Gọi từ thread bất kỳ.</summary>
    private void Append(string line)
    {
        line = AnsiRegex().Replace(line, "");
        var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        _pending.Enqueue(stamped);
        WriteToFile(stamped);
    }

    private void AppendSystem(string message) => Append(">>> " + message);

    private void WriteToFile(string line)
    {
        lock (_fileLock)
        {
            try
            {
                var today = DateTime.Today;
                if (_logWriter == null || _logDate != today)
                {
                    _logWriter?.Dispose();
                    Directory.CreateDirectory(LogFolder);
                    var path = Path.Combine(LogFolder, $"{today:yyyy-MM-dd}.log");
                    var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _logWriter = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                    _logDate = today;
                }
                _logWriter.WriteLine(line);
            }
            catch
            {
                // Không để lỗi ghi file làm hỏng việc đọc output của tool
            }
        }
    }

    /// <summary>Đẩy log đang chờ lên màn hình. Gọi định kỳ từ UI thread. Trả về true nếu có dòng mới.</summary>
    public bool FlushLogs()
    {
        bool any = false;
        while (_pending.TryDequeue(out var line))
        {
            Logs.Add(line);
            DetectError(line);
            any = true;
        }
        while (Logs.Count > MaxLinesOnScreen) Logs.RemoveAt(0);
        return any;
    }

    // Lỗi cách nhau dưới mốc này được gom thành 1 lần (stack trace, log nhiều dòng)
    private static readonly TimeSpan ErrorBurstWindow = TimeSpan.FromSeconds(3);

    [GeneratedRegex(@"[A-Za-z_][\w.]*Exception:")]
    private static partial Regex ExceptionRegex();

    /// <summary>Nhận diện dòng log lỗi. Dòng có dạng "HH:mm:ss nội dung".</summary>
    public static bool IsErrorLine(string line)
    {
        var msg = line.Length > 9 ? line[9..] : line;
        if (msg.StartsWith(">>> ")) return false; // thông báo của Tool Manager
        return msg.StartsWith("fail:", StringComparison.OrdinalIgnoreCase)
            || msg.StartsWith("crit:", StringComparison.OrdinalIgnoreCase)
            || msg.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
            || msg.StartsWith("[ERR]")
            || msg.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase)
            || ExceptionRegex().IsMatch(msg);
    }

    private void DetectError(string line)
    {
        if (!IsErrorLine(line)) return;

        var now = DateTime.Now;
        if (now - _lastErrorLineAt > ErrorBurstWindow)
        {
            ErrorCount++;
            _errorsSinceStart++;
            if (Config.RestartOnError && State == ToolState.Running
                && _errorsSinceStart >= Math.Max(1, Config.RestartOnErrorCount))
                _errorRestartArmed = true; // Tick() sẽ restart khi stack trace đã ghi xong
            LastErrorLine = line;
            var restartInfo = !Config.RestartOnError
                ? "Tự restart khi log có lỗi: ĐANG TẮT (bấm Sửa để bật)."
                : _errorRestartSuspended
                    ? "Tự restart khi log có lỗi: tạm ngừng do đã restart quá nhiều lần."
                    : $"Tự restart khi log có lỗi: bật (khi có {Math.Max(1, Config.RestartOnErrorCount)} lỗi).";
            ErrorTooltip = $"Lỗi gần nhất lúc {now:HH:mm:ss}: {Shorten(line[Math.Min(9, line.Length)..])}\n{restartInfo}\nBấm để xem trong log và đánh dấu đã xem.";
        }
        _lastErrorLineAt = now;
    }

    private static string Shorten(string s) => s.Length <= 160 ? s.Trim() : s[..160].Trim() + "...";

    public void ClearScreen() => Logs.Clear();

    /// <summary>Cập nhật thời gian chạy. Gọi mỗi giây từ UI thread.</summary>
    public void Tick()
    {
        if (_startedAt.HasValue) Uptime = FormatDuration(DateTime.Now - _startedAt.Value);

        if (_errorRestartArmed && State == ToolState.Running && DateTime.Now - _lastErrorLineAt >= ErrorBurstWindow)
        {
            _errorRestartArmed = false;
            TryRestartForError();
        }
    }

    // ================== Tiện ích ==================

    private static Encoding ResolveEncoding(string? name)
    {
        if (string.Equals(name, "oem", StringComparison.OrdinalIgnoreCase))
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        return new UTF8Encoding(false);
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h{t.Minutes:00}m"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:00}s"
        : $"{t.Seconds}s";

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(safe) ? "tool" : safe;
    }

    public void Dispose()
    {
        CancelPendingRestart();
        lock (_fileLock) { _logWriter?.Dispose(); _logWriter = null; }
    }
}
