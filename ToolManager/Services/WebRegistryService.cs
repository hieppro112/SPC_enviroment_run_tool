using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace ToolManager.Services;

/// <summary>1 web trong bảng data: tên + URL (đã tách host và port).</summary>
public sealed record WebEntry(string Name, string Url, string Host, int Port);

/// <summary>Đọc danh sách web từ SQL Server và lọc ra các web nằm trên máy hiện tại.</summary>
public static class WebRegistryService
{
    public const string CreateTableScript =
        """
        CREATE TABLE dbo.WebRegistry (
            Id        INT IDENTITY(1,1) PRIMARY KEY,
            Name      NVARCHAR(200) NOT NULL,              -- Tên web, vd: Dashboard HeatGuide
            Url       NVARCHAR(500) NOT NULL,              -- vd: http://192.168.122.15:5000/
            Note      NVARCHAR(500) NULL,
            IsActive  BIT NOT NULL CONSTRAINT DF_WebRegistry_IsActive DEFAULT (1),
            UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_WebRegistry_UpdatedAt DEFAULT (SYSDATETIME())
        );
        """;

    /// <summary>Chạy câu truy vấn, đọc 2 cột Name và Url. URL không hợp lệ thì bỏ qua.</summary>
    public static async Task<List<WebEntry>> LoadAsync(string connectionString, string query, CancellationToken ct = default)
    {
        var list = new List<WebEntry>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        int nameIdx, urlIdx;
        try
        {
            nameIdx = reader.GetOrdinal("Name");
            urlIdx = reader.GetOrdinal("Url");
        }
        catch (IndexOutOfRangeException)
        {
            throw new InvalidOperationException("Câu truy vấn phải trả về 2 cột tên là Name và Url (dùng AS nếu bảng đặt tên khác).");
        }

        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(urlIdx)) continue;
            var name = reader.IsDBNull(nameIdx) ? "" : Convert.ToString(reader.GetValue(nameIdx)) ?? "";
            var url = Convert.ToString(reader.GetValue(urlIdx)) ?? "";
            if (Parse(name, url) is { } entry) list.Add(entry);
        }
        return list;
    }

    public static WebEntry? Parse(string name, string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return new WebEntry(name.Trim(), url, uri.Host.Trim('[', ']'), uri.Port);
    }

    /// <summary>Mọi IP (IPv4 + IPv6) và tên máy của máy hiện tại. Không tính localhost/127.0.0.1.</summary>
    public static HashSet<string> GetLocalHosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.MachineName };
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            if (!string.IsNullOrEmpty(props.DomainName)) hosts.Add($"{props.HostName}.{props.DomainName}");

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                        hosts.Add(a.Address.ToString().Split('%')[0]);
                }
            }
        }
        catch
        {
            // không đọc được card mạng thì chỉ so theo tên máy
        }
        return hosts;
    }

    /// <summary>Mở URL bằng trình duyệt mặc định. Chỉ cho http/https (dữ liệu lấy từ database).</summary>
    public static bool TryOpenInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return true;
    }

    // ================== Mã hóa chuỗi kết nối (Windows DPAPI) ==================

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ToolManager.WebDb");

    public static string Protect(string connectionString) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(connectionString), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>null nếu không giải mã được (file copy từ máy khác / tài khoản Windows khác).</summary>
    public static string? Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"Server/Database" để hiển thị, không lộ tài khoản / mật khẩu.</summary>
    public static string Describe(string connectionString)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            return $"{b.DataSource}/{b.InitialCatalog}";
        }
        catch
        {
            return "(chuỗi kết nối không hợp lệ)";
        }
    }
}
