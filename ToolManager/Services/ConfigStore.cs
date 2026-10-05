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

    public static void Save(IEnumerable<ToolConfig> tools) => WriteJson(ConfigPath, tools.ToList());

    public static string PortSettingsPath => Path.Combine(BaseDir, "ports.json");

    public static PortSettings LoadPortSettings()
    {
        if (!File.Exists(PortSettingsPath)) return new();
        try
        {
            return JsonSerializer.Deserialize<PortSettings>(File.ReadAllText(PortSettingsPath), JsonOptions) ?? new();
        }
        catch
        {
            return new(); // file hỏng thì dùng mặc định, không làm sập app
        }
    }

    public static void SavePortSettings(PortSettings settings) => WriteJson(PortSettingsPath, settings);

    private static void WriteJson<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        // Ghi ra file tạm rồi thay thế để không hỏng file nếu bị tắt giữa chừng
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
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
