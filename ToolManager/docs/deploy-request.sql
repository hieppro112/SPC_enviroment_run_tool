-- Bảng yêu cầu tạo file deploy cho Tool Manager (nút "Tạo từ repo" ở tab Tools)
-- Tool Manager: INSERT yêu cầu mới, SELECT danh sách, UPDATE khi hủy yêu cầu đang chờ.
-- Worker (giai đoạn 2, chạy trên cùng máy): nhận yêu cầu Pending của máy mình, gọi Claude Code, ghi kết quả.
-- Dùng chung database với bảng dbo.ToolManagerWebRegistry (cấu hình ở tab Ports > Cấu hình database).

CREATE TABLE dbo.ToolManagerDeployRequest (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    MachineName NVARCHAR(128) NOT NULL,              -- máy có folder; worker chỉ nhận yêu cầu của máy mình
    FolderPath  NVARCHAR(500) NOT NULL,              -- folder đã git clone/pull về, vd: D:\Hiep\heatpress\be\heatguideBE-git
    ToolName    NVARCHAR(200) NOT NULL,              -- tên tool gợi ý trong Tool Manager
    Note        NVARCHAR(1000) NULL,                 -- ghi chú cho Claude, vd: chạy port 5060
    Status      NVARCHAR(20) NOT NULL CONSTRAINT DF_ToolManagerDeployRequest_Status DEFAULT (N'Pending'),
    RequestedBy NVARCHAR(128) NOT NULL,              -- tài khoản Windows tạo yêu cầu
    CreatedAt   DATETIME2 NOT NULL CONSTRAINT DF_ToolManagerDeployRequest_CreatedAt DEFAULT (SYSDATETIME()),
    StartedAt   DATETIME2 NULL,
    FinishedAt  DATETIME2 NULL,
    ResultJson  NVARCHAR(MAX) NULL,                  -- worker ghi: file đã tạo + cấu hình tool gợi ý
    ErrorLog    NVARCHAR(MAX) NULL,
    CONSTRAINT CK_ToolManagerDeployRequest_Status CHECK (Status IN (N'Pending', N'Running', N'Done', N'Failed', N'Cancelled'))
);
GO

CREATE INDEX IX_ToolManagerDeployRequest_Machine ON dbo.ToolManagerDeployRequest (MachineName, Status, Id);
GO

-- Cấp quyền cho tài khoản chạy Tool Manager và worker (nếu dùng tài khoản Windows), ví dụ:
-- CREATE LOGIN [DOMAIN\ten_tai_khoan] FROM WINDOWS;
-- CREATE USER  [DOMAIN\ten_tai_khoan] FOR LOGIN [DOMAIN\ten_tai_khoan];
-- GRANT SELECT, INSERT, UPDATE ON dbo.ToolManagerDeployRequest TO [DOMAIN\ten_tai_khoan];
