namespace ToolManager.Models;

/// <summary>Cấu hình 1 tool, được lưu vào tools.json.</summary>
public class ToolConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Arguments { get; set; } = "";

    /// <summary>Để trống = thư mục chứa file exe.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Tự chạy khi mở ToolManager.</summary>
    public bool AutoStart { get; set; }

    /// <summary>Tự khởi động lại khi tool bị tắt ngoài ý muốn.</summary>
    public bool AutoRestart { get; set; } = true;

    public int RestartDelaySeconds { get; set; } = 5;

    /// <summary>Tự restart khi log có lỗi (tool vẫn chạy nhưng báo fail/exception).</summary>
    public bool RestartOnError { get; set; }

    /// <summary>Số lần lỗi (kể từ lần chạy gần nhất) thì restart.</summary>
    public int RestartOnErrorCount { get; set; } = 1;

    /// <summary>true = ẩn cửa sổ cmd và hút log vào ToolManager. Tắt đi với app có giao diện (WPF/WinForms).</summary>
    public bool HideWindow { get; set; } = true;

    /// <summary>"utf-8" hoặc "oem" (code page mặc định của console Windows).</summary>
    public string OutputEncoding { get; set; } = "utf-8";

    public ToolConfig Clone() => (ToolConfig)MemberwiseClone();
}
