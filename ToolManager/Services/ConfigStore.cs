using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ToolManager.Models;

namespace ToolManager.Services;

public static class ConfigStore
{
    public static string BaseDir => AppContext.BaseDirectory;
    public static string ConfigPath => Path.Combine(BaseDir, "tools.json");
    public static string LogRoot => Path.Combine(BaseDir, "logs");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // giữ nguyên tiếng Việt trong file
    };

    public static List<ToolConfig> Load()
    {
        if (!File.Exists(ConfigPath)) return new();
        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<List<ToolConfig>>(json, JsonOptions) ?? new();
    }

    public static void Save(IEnumerable<ToolConfig> tools)
    {
        var json = JsonSerializer.Serialize(tools.ToList(), JsonOptions);
        // Ghi ra file tạm rồi thay thế để không hỏng tools.json nếu bị tắt giữa chừng
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    /// <summary>Xóa file log cũ hơn số ngày chỉ định (chỉ trong thư mục logs của ToolManager).</summary>
    public static void CleanupOldLogs(int keepDays)
    {
        if (!Directory.Exists(LogRoot)) return;
        var limit = DateTime.Now.AddDays(-keepDays);
        foreach (var file in Directory.EnumerateFiles(LogRoot, "*.log", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTime(file) < limit) File.Delete(file);
            }
            catch { /* file đang bị dùng thì bỏ qua */ }
        }
    }
}
