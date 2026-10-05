using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ToolManager.Services;

/// <summary>1 socket đang LISTEN trên máy.</summary>
public sealed record ListenerInfo(int Port, string Address, int Pid);

public sealed record PortConflict(int Port, string Message);

/// <summary>
/// Ảnh chụp tại 1 thời điểm: port đang mở, tiến trình (cha/con), dải port Windows giữ chỗ.
/// Dùng được từ thread nền.
/// </summary>
public sealed class PortSnapshot
{
    public required List<ListenerInfo> Listeners { get; init; }
    public required Dictionary<int, (int ParentPid, string Name)> Processes { get; init; }
    public required List<(int Start, int End)> ExcludedRanges { get; init; }

    private HashSet<int>? _busy;
    public HashSet<int> BusyPorts => _busy ??= Listeners.Select(l => l.Port).ToHashSet();

    private readonly Dictionary<int, string?> _paths = new();

    public string GetName(int pid) => pid switch
    {
        0 => "System Idle Process",
        4 => "System",
        _ => Processes.TryGetValue(pid, out var p) ? Path.GetFileNameWithoutExtension(p.Name) : $"PID {pid}",
    };

    public string? GetPath(int pid)
    {
        if (!_paths.TryGetValue(pid, out var path))
            _paths[pid] = path = PortService.GetProcessPath(pid);
        return path;
    }

    /// <summary>pid là rootPid hoặc tiến trình con/cháu của rootPid.</summary>
    public bool IsInTree(int pid, int rootPid)
    {
        for (int i = 0; i < 16 && pid > 4; i++)
        {
            if (pid == rootPid) return true;
            if (!Processes.TryGetValue(pid, out var p) || p.ParentPid == pid) return false;
            pid = p.ParentPid;
        }
        return false;
    }

    public (int Start, int End)? FindExcluded(int port)
    {
        foreach (var r in ExcludedRanges)
            if (port >= r.Start && port <= r.End) return r;
        return null;
    }
}

/// <summary>Đọc port đang mở (giống netstat -ano), kiểm tra port trống, tìm port trống.</summary>
public static partial class PortService
{
    public const int MinUserPort = 1024;

    // ================== Ảnh chụp ==================

    public static PortSnapshot TakeSnapshot() => new()
    {
        Listeners = ReadTcpListeners(AF_INET).Concat(ReadTcpListeners(AF_INET6)).ToList(),
        Processes = ReadProcesses(),
        ExcludedRanges = GetExcludedRanges(),
    };

    // ================== Kiểm tra / tìm port trống ==================

    /// <summary>
    /// Thử bind port trên mọi IP (IPv4 + IPv6). null = bind được (port trống).
    /// Chỉ Bind, không Listen, nên Windows Firewall không bật hộp thoại hỏi quyền.
    /// </summary>
    public static SocketError? TryBind(int port)
    {
        var v4 = TryBindOne(AddressFamily.InterNetwork, IPAddress.Any, port);
        if (v4 != null || !Socket.OSSupportsIPv6) return v4;
        return TryBindOne(AddressFamily.InterNetworkV6, IPAddress.IPv6Any, port);
    }

    private static SocketError? TryBindOne(AddressFamily family, IPAddress address, int port)
    {
        try
        {
            using var s = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            s.ExclusiveAddressUse = true; // bind thất bại nếu có app khác bind port này ở bất kỳ IP nào
            s.Bind(new IPEndPoint(address, port));
            return null;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode;
        }
    }

    /// <summary>Tìm port trống theo thứ tự tăng dần trong các khoảng, bỏ qua port &lt; 1024 và các port trong skip.</summary>
    public static List<int> FindFreePorts(PortSnapshot snap, IEnumerable<(int Start, int End)> ranges, int count, ISet<int> skip)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var (start, end) in ranges.OrderBy(r => r.Start))
        {
            for (int port = Math.Max(start, MinUserPort); port <= end && result.Count < count; port++)
            {
                if (!seen.Add(port)) continue;
                if (snap.BusyPorts.Contains(port) || skip.Contains(port) || snap.FindExcluded(port) != null) continue;
                if (TryBind(port) != null) continue;
                result.Add(port);
            }
            if (result.Count >= count) break;
        }
        return result;
    }

    /// <summary>
    /// Port của tool đang bị app khác chiếm, hoặc nằm trong dải Windows giữ chỗ.
    /// ownPid = PID của chính tool (nếu đang chạy) để không tự báo trùng với chính nó.
    /// </summary>
    public static List<PortConflict> FindConflicts(IReadOnlyCollection<int> ports, int? ownPid)
    {
        var conflicts = new List<PortConflict>();
        if (ports.Count == 0) return conflicts;

        PortSnapshot snap;
        try { snap = TakeSnapshot(); }
        catch { return conflicts; } // không đọc được thì bỏ qua, không chặn việc chạy tool

        foreach (var port in ports.Distinct())
        {
            var owners = snap.Listeners
                .Where(l => l.Port == port && !(ownPid.HasValue && snap.IsInTree(l.Pid, ownPid.Value)))
                .Select(l => l.Pid).Distinct().ToList();
            if (owners.Count > 0)
            {
                var who = string.Join(", ", owners.Select(pid => $"{snap.GetName(pid)} (PID {pid})"));
                conflicts.Add(new PortConflict(port, $"Port {port} đang bị {who} chiếm"));
            }
            else if (snap.FindExcluded(port) is { } r)
            {
                conflicts.Add(new PortConflict(port, $"Port {port} nằm trong dải Windows giữ chỗ {r.Start}-{r.End}"));
            }
        }
        return conflicts;
    }

    /// <summary>Đọc "5000-5999, 7000-7100, 8080" thành danh sách khoảng.</summary>
    public static List<(int Start, int End)> ParseRanges(string text, out string? error)
    {
        error = null;
        var result = new List<(int, int)>();
        foreach (var raw in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2
                || !int.TryParse(parts[0], out var start)
                || !int.TryParse(parts.Length == 2 ? parts[1] : parts[0], out var end)
                || start < 1 || end > 65535 || start > end)
            {
                error = $"Khoảng \"{raw}\" không hợp lệ. Ví dụ đúng: 5000-5999, 7000-7100";
                return new();
            }
            result.Add((start, end));
        }
        if (result.Count == 0) error = "Chưa nhập khoảng port. Ví dụ: 5000-5999";
        return result;
    }

    // ================== Thông tin máy / tiến trình ==================

    /// <summary>IPv4 chính của máy (card mạng đang chạy, ưu tiên card có gateway).</summary>
    public static string GetLocalIp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n =>
                {
                    var props = n.GetIPProperties();
                    bool hasGateway = props.GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    return props.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => (Ip: a.Address.ToString(), hasGateway));
                })
                .OrderByDescending(x => x.hasGateway)
                .Select(x => x.Ip)
                .FirstOrDefault() ?? "localhost";
        }
        catch
        {
            return "localhost";
        }
    }

    /// <summary>Đường dẫn exe của tiến trình. null nếu không đủ quyền đọc (app chạy bằng admin/SYSTEM).</summary>
    public static string? GetProcessPath(int pid)
    {
        if (pid <= 4) return null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static readonly HashSet<string> CriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "System Idle Process", "Registry", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "svchost", "spoolsv", "dwm", "fontdrvhost", "explorer",
        "MsMpEng", "NisSrv", "SecurityHealthService", "MsSense", "SenseNdr", "SenseIR",
    };

    /// <summary>Lý do không cho kết thúc tiến trình từ Tool Manager. null = cho phép (vẫn phải hỏi xác nhận).</summary>
    public static string? WhyCannotKill(int pid, string name, string? path)
    {
        if (pid <= 4) return "Đây là tiến trình hệ thống của Windows.";
        if (pid == Environment.ProcessId) return "Đây là chính Tool Manager.";
        if (CriticalProcesses.Contains(name)) return "Đây là tiến trình quan trọng của Windows hoặc phần mềm bảo mật.";
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path != null && path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
            return "App nằm trong thư mục Windows (app hệ thống).";
        return null;
    }

    // ================== Dải port Windows giữ chỗ ==================

    private static readonly object ExcludedLock = new();
    private static List<(int, int)> _excludedCache = new();
    private static DateTime _excludedAt = DateTime.MinValue;
    private static readonly TimeSpan ExcludedCacheTime = TimeSpan.FromSeconds(60);

    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)", RegexOptions.Multiline)]
    private static partial Regex ExcludedLineRegex();

    /// <summary>
    /// Dải port Hyper-V / WSL / Docker giữ chỗ: không app nào mở được dù không ai đang dùng.
    /// Đọc từ "netsh int ipv4 show excludedportrange protocol=tcp", cache 60 giây.
    /// </summary>
    public static List<(int Start, int End)> GetExcludedRanges()
    {
        lock (ExcludedLock)
        {
            if (DateTime.Now - _excludedAt < ExcludedCacheTime) return _excludedCache;
            try
            {
                var psi = new ProcessStartInfo("netsh", "int ipv4 show excludedportrange protocol=tcp")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                _excludedCache = ExcludedLineRegex().Matches(output)
                    .Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)))
                    .Where(r => r.Item1 <= r.Item2)
                    .ToList();
            }
            catch
            {
                _excludedCache = new();
            }
            _excludedAt = DateTime.Now;
            return _excludedCache;
        }
    }

    // ================== Win32: bảng TCP (giống netstat -ano) ==================

    private const int AF_INET = 2, AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    private static List<ListenerInfo> ReadTcpListeners(int af)
    {
        var list = new List<ListenerInfo>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                uint rc = GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (rc == ERROR_INSUFFICIENT_BUFFER) continue; // bảng vừa to lên, thử lại với size mới
                if (rc != 0) return list;

                int count = Marshal.ReadInt32(buf);
                if (af == AF_INET)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid (6 x 4 byte)
                    const int rowSize = 24;
                    for (int i = 0; i < count; i++)
                    {
                        int off = 4 + i * rowSize;
                        uint addr = (uint)Marshal.ReadInt32(buf, off + 4);
                        int port = NetworkPort(Marshal.ReadInt32(buf, off + 8));
                        int pid = Marshal.ReadInt32(buf, off + 20);
                        list.Add(new ListenerInfo(port, new IPAddress(addr).ToString(), pid));
                    }
                }
                else
                {
                    // MIB_TCP6ROW_OWNER_PID: localAddr[16], scopeId, localPort, remoteAddr[16], remoteScopeId, remotePort, state, owningPid
                    const int rowSize = 56;
                    var bytes = new byte[16];
                    for (int i = 0; i < count; i++)
                    {
                        int off = 4 + i * rowSize;
                        Marshal.Copy(buf + off, bytes, 0, 16);
                        int port = NetworkPort(Marshal.ReadInt32(buf, off + 20));
                        int pid = Marshal.ReadInt32(buf, off + 52);
                        list.Add(new ListenerInfo(port, $"[{new IPAddress(bytes)}]", pid));
                    }
                }
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return list;
    }

    /// <summary>Port trong bảng TCP nằm ở 2 byte thấp, theo thứ tự byte mạng.</summary>
    private static int NetworkPort(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    // ================== Win32: danh sách tiến trình (kèm PID cha) ==================

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static Dictionary<int, (int, string)> ReadProcesses()
    {
        var map = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32First(snap, ref entry)) return map;
            do
            {
                map[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
            } while (Process32Next(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }
}
