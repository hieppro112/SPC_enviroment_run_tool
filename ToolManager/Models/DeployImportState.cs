namespace ToolManager.Models;

/// <summary>
/// Yêu cầu "Tạo từ repo" đã xử lý trên máy này (tự thêm tool / đã báo lỗi), lưu trong deploy-import.json.
/// Lưu riêng để tool đã bị xóa khỏi danh sách không bị tự thêm lại.
/// </summary>
public class DeployImportState
{
    /// <summary>Yêu cầu có Id &lt;= mốc này đã xong trước khi bật tính năng: không tự thêm (thêm tay trong cửa sổ "Tạo từ repo").</summary>
    public int BaselineId { get; set; }

    public List<int> HandledIds { get; set; } = new();
}
