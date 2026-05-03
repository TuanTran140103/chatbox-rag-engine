# Admin Service Implementation Plan - Detailed Architecture

## 1. Cấu trúc Code (Application Layer)

Hệ thống chia nhỏ logic Admin thành 3 Domain chuyên biệt:
- **IAdminOrgService**: Quản lý OU (Path/Level) và nhân sự (UserPositions).
- **IAdminStatsService**: Truy vấn bảng thống kê và cung cấp dữ liệu Dashboard.
- **IAdminDatasetService**: Giám sát Dataset toàn hệ thống, chuyển nhượng Owner.

---

## 2. Tối ưu Dashboard (SystemStatistics Table)

Để Dashboard đạt hiệu năng O(1), chúng ta sử dụng bảng thống kê tập trung:

```sql
CREATE TABLE SystemStatistics (
    Id INT PRIMARY KEY IDENTITY(1,1),
    OUId UNIQUEIDENTIFIER NULL,        -- NULL = Tổng công ty, NOT NULL = Theo phòng
    TotalDatasets INT DEFAULT 0,
    TotalDocuments INT DEFAULT 0,
    TotalStorageUsage BIGINT DEFAULT 0, -- Đơn vị Byte
    UpdatedAt DATETIME2 DEFAULT GETDATE()
);
```

### Cơ chế cập nhật: Database Triggers
Thay vì dùng Background Jobs, chúng ta sử dụng **Triggers** trực tiếp trong DB trên các bảng `Datasets` và `Documents` (hoặc `DatasetItems`):
- **AFTER INSERT:** Tăng `TotalDatasets`/`TotalDocuments` và cộng dồn `FileSize` vào `TotalStorageUsage`.
- **AFTER DELETE:** Giảm các chỉ số tương ứng.
- **Lợi ích:** Đảm bảo tính nhất quán dữ liệu tuyệt đối và Dashboard luôn hiển thị số liệu thực tế nhất.

---

## 3. Nguyên tắc Thiết kế DTO (Tối ưu cho UX/UI)

- **Flattening Data:** Trả về `OuName` trực tiếp trong `UserDto`.
- **Display-Ready Fields:** BE trả về sẵn `SizeDisplay` (ví dụ: "1.2 GB") bên cạnh giá trị byte.
- **UI Indicators:** Trả về `HasChildren: true/false` cho Folder để UI biết có hiện icon mở rộng hay không.
- **Breadcrumb Data:** Trả về mảng các Folder cha khi truy vấn một thư mục để UI hiển thị đường dẫn.

---

## 4. Chi tiết API Endpoints

### 4.1 Organization & Personnel
- `GET /api/v1/admin/org/tree`: Trả về cây OU lồng nhau (Recursive DTO).
- `GET /api/v1/admin/org/{ouId}/users`: Danh sách nhân sự trong OU (đã làm phẳng thông tin).

### 4.2 Dashboard Statistics
- `GET /api/v1/admin/stats/summary`: Trả về các con số tổng quát từ `SystemStatistics`.
- `GET /api/v1/admin/stats/storage-chart`: Dữ liệu phân bổ tài nguyên giữa các OU.

### 4.3 Global Dataset Management
- `GET /api/v1/admin/datasets`: Liệt kê tất cả Dataset kèm thông tin Owner và OU.
- `GET /api/v1/datasets/{id}/items?parentId={guid?}`: 
    - Truy vấn theo Level: Chỉ lấy items trực tiếp của `parentId`.
    - Trả về: `Id`, `Name`, `Type`, `HasChildren`, `SizeDisplay`.
