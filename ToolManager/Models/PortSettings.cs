namespace ToolManager.Models;

/// <summary>Cấu hình màn hình Ports, được lưu vào ports.json.</summary>
public class PortSettings
{
    /// <summary>Các khoảng port để tìm port trống, vd: "5000-5999, 7000-7100".</summary>
    public string Ranges { get; set; } = "5000-5999";

    /// <summary>Số port trống hiển thị.</summary>
    public int FreeCount { get; set; } = 20;

    /// <summary>Port đã "đặt trước" cho app nào đó, không gợi ý cho người khác.</summary>
    public List<PortReservation> Reservations { get; set; } = new();

    /// <summary>Database chứa danh sách web (Tên + URL) dùng chung cho các server.</summary>
    public WebDbSettings WebDb { get; set; } = new();
}

public class WebDbSettings
{
    public const string DefaultQuery = "SELECT Name, Url FROM dbo.WebRegistry WHERE IsActive = 1";

    /// <summary>Chuỗi kết nối đã mã hóa bằng Windows DPAPI (chỉ tài khoản Windows này trên máy này giải mã được).</summary>
    public string ProtectedConnection { get; set; } = "";

    /// <summary>Câu truy vấn, phải trả về 2 cột Name và Url.</summary>
    public string Query { get; set; } = DefaultQuery;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => ProtectedConnection.Length > 0;
}

public class PortReservation
{
    public int Port { get; set; }
    public string Note { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string Info => $"{CreatedBy} · {CreatedAt:dd/MM/yyyy HH:mm}";
}
