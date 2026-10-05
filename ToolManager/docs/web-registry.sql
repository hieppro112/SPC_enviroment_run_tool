-- Bảng danh sách web cho Tool Manager (tab Ports > "Web trên máy này")
-- Tool Manager chỉ cần ĐỌC bảng này: SELECT Name, Url FROM dbo.WebRegistry WHERE IsActive = 1
-- Mỗi máy chạy Tool Manager sẽ tự lọc ra các web có IP (hoặc tên máy) trong URL trùng với máy đó.

CREATE TABLE dbo.WebRegistry (
    Id        INT IDENTITY(1,1) PRIMARY KEY,
    Name      NVARCHAR(200) NOT NULL,              -- Tên web, vd: Dashboard HeatGuide
    Url       NVARCHAR(500) NOT NULL,              -- vd: http://192.168.122.15:5000/
    Note      NVARCHAR(500) NULL,
    IsActive  BIT NOT NULL CONSTRAINT DF_WebRegistry_IsActive DEFAULT (1),
    UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_WebRegistry_UpdatedAt DEFAULT (SYSDATETIME())
);
GO

-- Dữ liệu chép từ ảnh chụp danh sách web. HÃY KIỂM TRA LẠI trước khi chạy.
INSERT INTO dbo.WebRegistry (Name, Url) VALUES
 (N'Dashboard HeatGuide',          N'http://192.168.122.15:5000/'),
 (N'Cost Monitoring',              N'http://192.168.122.15:5001/'),
 (N'SmallForging A-2894 Cam 1',    N'http://10.4.4.106:5000/'),
 (N'Packing PO Monitoring',        N'http://192.168.122.16:5004/'),
 (N'Dashboard Molybden',           N'http://192.168.122.15:5004/'),
 (N'S-Patrol',                     N'https://spcspatrol-misumig.msappproxy.net/'),
 (N'MA Monitoring',                N'http://192.168.122.15:5002/'),
 (N'GUIDEKC A-2108',               N'http://10.4.4.111:5000/'),
 (N'Utility Dashboard',            N'http://192.168.122.16:5003/'),
 (N'SmallForging A-2894 Cam 2',    N'http://10.4.4.106:5001/'),
 (N'MTSEntou(A-015,A-1225)',       N'http://10.4.7.112:5001/'),
 (N'Dashboard HeatPress',          N'http://192.168.122.16:4999/'),
 (N'Warehouse Management',         N'https://192.168.122.16:5009/'),
 (N'Dashboard Production',         N'http://192.168.122.16:6001/'),
 (N'Dashboard HR',                 N'http://192.168.122.15:5006/');

-- 3 dòng dưới bị mờ phần IP trong ảnh, hãy điền đúng IP rồi bỏ dấu "--":
-- INSERT INTO dbo.WebRegistry (Name, Url) VALUES
--  (N'3D Printer',    N'http://192.168.10.?:5003/'),
--  (N'TE Dashboard',  N'http://192.168.10.?:3000/'),
--  (N'Color Printer', N'http://192.168.10.?:5007/');
GO

-- Cấp quyền đọc cho tài khoản chạy Tool Manager (nếu dùng tài khoản Windows), ví dụ:
-- CREATE LOGIN [DOMAIN\ten_tai_khoan] FROM WINDOWS;
-- CREATE USER  [DOMAIN\ten_tai_khoan] FOR LOGIN [DOMAIN\ten_tai_khoan];
-- GRANT SELECT ON dbo.WebRegistry TO [DOMAIN\ten_tai_khoan];
