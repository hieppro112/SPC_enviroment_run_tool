using ToolManager.Services;

namespace ToolManager.Views;

/// <summary>1 dòng trong bảng "Port đang bị chiếm" (gộp IPv4 + IPv6 của cùng port + PID).</summary>
public sealed class BusyPortItem
{
    public required int Port { get; init; }
    public required string Addresses { get; init; }
    public required int Pid { get; init; }
    public required string AppName { get; init; }
    public string? Path { get; init; }

    /// <summary>Tool của Tool Manager đang giữ port này (nếu có).</summary>
    public ToolRunner? Tool { get; init; }

    public bool IsManaged => Tool != null;
    public bool IsSystem => Pid <= 4;
    public bool HasPath => Path != null;
    public string ToolLabel => Tool != null ? $"Tool: {Tool.Config.Name}" : "";
    public string PathDisplay => Path ?? (IsSystem ? "Windows (HTTP.sys / IIS / dịch vụ hệ thống)" : "Không đủ quyền đọc đường dẫn");
    private string? CannotKillReason => IsManaged ? null : PortService.WhyCannotKill(Pid, AppName, Path);
    public bool CanKill => CannotKillReason == null;
    public string KillTooltip => IsManaged ? "Dừng tool này"
        : CannotKillReason is { } reason ? $"Không cho kết thúc: {reason}"
        : "Kết thúc app đang giữ port";

    public string Key => $"{Port}|{Pid}|{Addresses}|{Tool?.Config.Id}";

    public bool Matches(string filter) =>
        Port.ToString().Contains(filter)
        || AppName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || (Tool?.Config.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || Pid.ToString() == filter;
}

/// <summary>1 dòng trong bảng "Web trên máy này".</summary>
public sealed class WebPortItem
{
    public required WebEntry Web { get; init; }
    public string Name => Web.Name.Length > 0 ? Web.Name : "(chưa đặt tên)";
    public string Url => Web.Url;
    public int Port => Web.Port;

    /// <summary>App đang mở port của web (rỗng = web không chạy).</summary>
    public required string Owner { get; init; }
    public ToolRunner? Tool { get; init; }

    public bool IsRunning => Owner.Length > 0;
    public bool IsManaged => Tool != null;
    public string StatusText => IsRunning ? "Đang chạy" : "Không chạy";
    public string OwnerDisplay => IsRunning ? Owner : "Không có app nào mở port này";
    public string ToolLabel => Tool != null ? $"Tool: {Tool.Config.Name}" : "";

    public string Key => $"{Web.Url}|{Owner}|{Tool?.Config.Id}";
}

public enum PortCheckKind { Free, Warning, Busy, Invalid }
