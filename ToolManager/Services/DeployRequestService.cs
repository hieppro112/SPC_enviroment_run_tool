using System.IO;
using Microsoft.Data.SqlClient;

namespace ToolManager.Services;

public enum DeployRequestStatus { Pending, Running, Done, Failed, Cancelled }

/// <summary>1 yêu cầu tạo file deploy (dòng trong bảng dbo.ToolManagerDeployRequest).</summary>
public sealed record DeployRequest(
    int Id,
    string FolderPath,
    string ToolName,
    DeployRequestStatus Status,
    string RequestedBy,
    DateTime CreatedAt,
    DateTime? FinishedAt,
    string? ErrorLog)
{
    public string StatusText => Status switch
    {
        DeployRequestStatus.Pending => "Chờ xử lý",
        DeployRequestStatus.Running => "Đang tạo",
        DeployRequestStatus.Done => "Xong",
        DeployRequestStatus.Failed => "Lỗi",
        _ => "Đã hủy",
    };

    public string Info => FinishedAt is { } f
        ? $"#{Id} · {ToolName} · {RequestedBy} · tạo {CreatedAt:dd/MM HH:mm}, xong {f:dd/MM HH:mm}"
        : $"#{Id} · {ToolName} · {RequestedBy} · tạo {CreatedAt:dd/MM/yyyy HH:mm}";

    public bool CanCancel => Status == DeployRequestStatus.Pending;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorLog);
}

/// <summary>
/// Ghi / đọc yêu cầu tạo file deploy trên SQL Server (dùng chung database với danh sách web).
/// Worker chạy trên cùng máy sẽ nhận yêu cầu theo MachineName và gọi Claude Code tạo file.
/// </summary>
public static class DeployRequestService
{
    public const string CreateTableScript =
        """
        CREATE TABLE dbo.ToolManagerDeployRequest (
            Id          INT IDENTITY(1,1) PRIMARY KEY,
            MachineName NVARCHAR(128) NOT NULL,
            FolderPath  NVARCHAR(500) NOT NULL,
            ToolName    NVARCHAR(200) NOT NULL,
            Note        NVARCHAR(1000) NULL,
            Status      NVARCHAR(20) NOT NULL CONSTRAINT DF_ToolManagerDeployRequest_Status DEFAULT (N'Pending'),
            RequestedBy NVARCHAR(128) NOT NULL,
            CreatedAt   DATETIME2 NOT NULL CONSTRAINT DF_ToolManagerDeployRequest_CreatedAt DEFAULT (SYSDATETIME()),
            StartedAt   DATETIME2 NULL,
            FinishedAt  DATETIME2 NULL,
            ResultJson  NVARCHAR(MAX) NULL,
            ErrorLog    NVARCHAR(MAX) NULL,
            CONSTRAINT CK_ToolManagerDeployRequest_Status CHECK (Status IN (N'Pending', N'Running', N'Done', N'Failed', N'Cancelled'))
        );
        GO
        CREATE INDEX IX_ToolManagerDeployRequest_Machine ON dbo.ToolManagerDeployRequest (MachineName, Status, Id);
        """;

    public const int MaxToolName = 200;
    public const int MaxNote = 1000;
    public const int MaxFolderPath = 500;

    /// <summary>Chuỗi kết nối đã lưu ở tab Ports. null + lý do nếu chưa cấu hình / không giải mã được.</summary>
    public static string? GetConnectionString(out string? error)
    {
        var web = ConfigStore.LoadPortSettings().WebDb;
        if (!web.IsConfigured)
        {
            error = "Chưa cấu hình database. Vào tab Ports > \"Cấu hình database\" (dùng chung database với danh sách web).";
            return null;
        }
        var cs = WebRegistryService.Unprotect(web.ProtectedConnection);
        error = cs == null
            ? "Không giải mã được cấu hình database (ports.json copy từ máy khác hoặc tài khoản Windows khác). Hãy cấu hình lại ở tab Ports."
            : null;
        return cs;
    }

    /// <summary>
    /// Tạo yêu cầu mới. Folder đã có yêu cầu đang chờ / đang tạo thì không tạo thêm,
    /// trả về Id của yêu cầu đó với Created = false.
    /// </summary>
    public static async Task<(int Id, bool Created)> CreateAsync(
        string connectionString, string folderPath, string toolName, string? note, CancellationToken ct = default)
    {
        const string sql =
            """
            DECLARE @existing INT = (
                SELECT TOP 1 Id FROM dbo.ToolManagerDeployRequest WITH (UPDLOCK, HOLDLOCK)
                WHERE MachineName = @machine AND FolderPath = @folder AND Status IN (N'Pending', N'Running'));
            IF @existing IS NOT NULL
                SELECT @existing AS Id, CAST(0 AS BIT) AS Created;
            ELSE
                INSERT INTO dbo.ToolManagerDeployRequest (MachineName, FolderPath, ToolName, Note, RequestedBy)
                OUTPUT INSERTED.Id, CAST(1 AS BIT) AS Created
                VALUES (@machine, @folder, @tool, @note, @user);
            """;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
        cmd.Parameters.AddWithValue("@folder", folderPath);
        cmd.Parameters.AddWithValue("@tool", toolName);
        cmd.Parameters.AddWithValue("@note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim());
        cmd.Parameters.AddWithValue("@user", Environment.UserName);

        (int, bool) result;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Không ghi được yêu cầu vào database.");
            result = (reader.GetInt32(0), reader.GetBoolean(1));
        }
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Các yêu cầu gần đây của máy này, mới nhất trước.</summary>
    public static async Task<List<DeployRequest>> ListAsync(string connectionString, int top = 30, CancellationToken ct = default)
    {
        const string sql =
            """
            SELECT TOP (@top) Id, FolderPath, ToolName, Status, RequestedBy, CreatedAt, FinishedAt, ErrorLog
            FROM dbo.ToolManagerDeployRequest
            WHERE MachineName = @machine
            ORDER BY Id DESC;
            """;

        var list = new List<DeployRequest>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@top", top);
        cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DeployRequest(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                Enum.TryParse<DeployRequestStatus>(reader.GetString(3), out var s) ? s : DeployRequestStatus.Failed,
                reader.GetString(4),
                reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return list;
    }

    /// <summary>Hủy yêu cầu đang chờ. false = worker đã nhận (hoặc đã xong) nên không hủy được.</summary>
    public static async Task<bool> CancelAsync(string connectionString, int id, CancellationToken ct = default)
    {
        const string sql =
            """
            UPDATE dbo.ToolManagerDeployRequest SET Status = N'Cancelled', FinishedAt = SYSDATETIME()
            WHERE Id = @id AND Status = N'Pending';
            """;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ================== Đọc nhanh thông tin folder (chỉ để hiển thị, không gửi lên database) ==================

    private static readonly (string Pattern, string Name)[] Markers =
    {
        ("pom.xml", "Java (Maven)"),
        ("build.gradle", "Java (Gradle)"),
        ("build.gradle.kts", "Java (Gradle)"),
        ("pubspec.yaml", "Flutter"),
        ("*.sln", ".NET"),
        ("*.csproj", ".NET"),
        ("package.json", "Node.js"),
        ("pyproject.toml", "Python"),
        ("requirements.txt", "Python"),
        ("go.mod", "Go"),
    };

    /// <summary>
    /// Mô tả folder: loại project nhận ra được (tìm ở folder và các thư mục con cấp 1),
    /// nhánh git, có sẵn deploy.cmd chưa. Không đọc được thì trả về chuỗi rỗng.
    /// </summary>
    public static string DescribeFolder(string folder)
    {
        try
        {
            var dirs = new List<string> { folder };
            dirs.AddRange(Directory.EnumerateDirectories(folder)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Where(d => !Path.GetFileName(d).Equals("node_modules", StringComparison.OrdinalIgnoreCase)));

            var seen = new HashSet<string>();
            var kinds = new List<string>();
            foreach (var (pattern, name) in Markers)
            {
                if (seen.Contains(name)) continue;
                var hit = dirs.FirstOrDefault(d => Directory.EnumerateFiles(d, pattern).Any());
                if (hit == null) continue;
                var where = hit == folder ? "" : $" trong {Path.GetFileName(hit)}\\";
                seen.Add(name);
                kinds.Add($"{name} ({pattern}{where})");
            }

            var parts = new List<string>
            {
                kinds.Count > 0 ? "Nhận ra: " + string.Join(", ", kinds) : "Không nhận ra loại project (Claude sẽ tự đọc folder)",
            };

            var head = Path.Combine(folder, ".git", "HEAD");
            if (File.Exists(head))
            {
                var text = File.ReadAllText(head).Trim();
                parts.Add(text.StartsWith("ref: refs/heads/") ? "nhánh " + text["ref: refs/heads/".Length..] : "git (không ở nhánh nào)");
            }
            else
            {
                parts.Add("không phải repo git");
            }

            if (File.Exists(Path.Combine(folder, "deploy.cmd"))) parts.Add("đã có deploy.cmd");
            return string.Join(" · ", parts);
        }
        catch
        {
            return "";
        }
    }
}
