# RBAC Plan V2 - Bitwise Permissions, Granular Sharing & Virtual Context

## 1. Định nghĩa Quyền hạn (Bitwise Permissions)

Sử dụng kiểu `int` để lưu trữ mặt nạ quyền (Permission Mask), giúp tối ưu việc kiểm tra quyền bằng toán tử nhị phân.

```csharp
[Flags]
public enum DatasetPermissions
{
    None = 0,               // 0000
    Read = 1,               // 0001: Xem metadata, nội dung OCR/QA/Summary
    Update = 2,             // 0010: Sửa nội dung QA, Summary, Metadata
    Delete = 4,             // 0100: Xóa dataset
    Share = 8,              // 1000: Quyền chia sẻ cho người khác
    
    // Tổ hợp quyền thường dùng
    Collaborate = Read | Update,             // 0011 (3): Làm việc nhóm (Xem + Sửa)
    FullControl = Read | Update | Delete | Share // 1111 (15): Toàn quyền
}
```

---

## 2. Cấu trúc Database (Schema SQL)

### 2.1 Tổ chức & Nhân sự
```sql
-- Cây phân cấp tổ chức (Materialized Path)
CREATE TABLE OrganizationUnits (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    Name NVARCHAR(255) NOT NULL,
    Code NVARCHAR(50) NULL,
    ParentId UNIQUEIDENTIFIER NULL,
    [Path] NVARCHAR(1000) NOT NULL, -- Ví dụ: /ID_Root/ID_Dept/
    [Level] INT NOT NULL,
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    FOREIGN KEY (ParentId) REFERENCES OrganizationUnits(Id)
);

-- Vị trí nhân sự (Một người có thể thuộc nhiều Unit)
CREATE TABLE UserPositions (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    UserId UNIQUEIDENTIFIER NOT NULL,
    OUId UNIQUEIDENTIFIER NOT NULL,
    Role NVARCHAR(50) NOT NULL,     -- 'Manager', 'Staff'
    IsPrimary BIT DEFAULT 0,
    FOREIGN KEY (OUId) REFERENCES OrganizationUnits(Id)
);
```

### 2.2 Dataset & Chia sẻ chi tiết (Granular Access)
```sql
-- Dataset neo tại một OU
CREATE TABLE Datasets (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    Name NVARCHAR(255) NOT NULL,
    OwnerUserId UNIQUEIDENTIFIER NOT NULL,
    OUId UNIQUEIDENTIFIER NOT NULL,     -- Thuộc Unit nào
    IsPublicToUnit BIT DEFAULT 0,       -- Tự động share Read cho Unit + con
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    FOREIGN KEY (OUId) REFERENCES OrganizationUnits(Id)
);

-- Bảng chia sẻ (Hỗ trợ cả Dataset và Document lẻ)
CREATE TABLE AccessShares (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    DatasetId UNIQUEIDENTIFIER NOT NULL,
    DatasetItemId UNIQUEIDENTIFIER NULL,  -- NULL = Share cả Dataset, NOT NULL = Share lẻ
    ShareToUserId UNIQUEIDENTIFIER NULL,
    ShareToOUId UNIQUEIDENTIFIER NULL,
    PermissionMask INT NOT NULL DEFAULT 1,
    GrantedBy UNIQUEIDENTIFIER NOT NULL, -- Người share
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    FOREIGN KEY (DatasetId) REFERENCES Datasets(Id),
    FOREIGN KEY (DatasetItemId) REFERENCES DatasetItems(Id)
);
```

---

## 3. Workflow RAG & Metadata Filtering

Để giải quyết bài toán share lẻ mà không rời rạc dữ liệu:

1.  **Tại Vector DB:** Mọi chunk dữ liệu lưu kèm Metadata: `{ "dataset_id": "...", "document_id": "..." }`.
2.  **Khi Chat:**
    - Hệ thống tính toán danh sách `Allowed_Document_IDs` của User (bao gồm tài liệu sở hữu + tài liệu được share lẻ).
    - Tạo Metadata Filter: `WHERE document_id IN (Allowed_Document_IDs)`.
    - Gửi Query kèm Filter vào Vector DB để đảm bảo kết quả trả về chỉ nằm trong phạm vi được phép.

---

## 4. Logic Thư mục ảo (Virtual Folders) cho UX

Khi hiển thị danh sách "Shared with me", hệ thống tự động dựng cây ảo để User dễ quản lý:

*   **Cấp 1:** `Shared With Me` (Gốc)
    *   **Cấp 2 (Virtual Folder):** `[Tên Unit của người share]` (Ví dụ: [Phòng IT])
        *   **Cấp 3 (Virtual Folder):** `[Tên người share]` (Ví dụ: [Nguyễn Văn A])
            *   **Cấp 4 (Dữ liệu thật):** File/Folder được share lẻ hoặc Dataset được share.

---

## 5. Luồng tính toán quyền (Effective Mask)

`EffectiveMask = (Quyền mặc định) | (Quyền chia sẻ)`

*   **Quyền mặc định:**
    - Owner/Manager trực tiếp: `FullControl (15)`.
    - Manager cấp trên (Dựa vào Path): `Read (1)`.
    - Cùng Unit + `IsPublicToUnit = true`: `Read (1)`.
*   **Quyền chia sẻ:**
    - Lấy từ `AccessShares` (ưu tiên quyền cao nhất nếu bị trùng lặp).

---

## 6. Quy tắc Visibility (Tầm nhìn)

Hạn chế việc lộ thông tin nhân sự toàn công ty:
- **Staff:** Chỉ thấy người cùng Unit.
- **Manager:** Thấy Unit mình, các Unit con, Manager cấp trên trực tiếp và các Manager cùng cấp (Level).
- **Share:** Chỉ được chọn đối tượng trong tầm nhìn để thực hiện Share.

---

## 7. Kế hoạch triển khai

1.  **Database:** Cập nhật bảng, chuyển đổi dữ liệu cũ (Migration).
2.  **Backend:**
    - Triển khai `AccessControlService` tập trung.
    - Cập nhật logic `GetAccessibleDocuments` để phục vụ RAG Filtering.
3.  **Frontend:** Xây dựng Component hiển thị cây "Virtual Folder" cho phần Shared.
