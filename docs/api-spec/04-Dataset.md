# Dataset API

Base URL: `/api/v1/user/me/datasets`

Tất cả API trong nhóm này đều thao tác trên dữ liệu của **user hiện tại** (xác thực qua Cookie, không cần truyền userId).

Yêu cầu **đăng nhập** (`[Authorize]`), không yêu cầu Admin.

Các API này cho phép user thao tác với dataset mà họ có quyền truy cập (sở hữu, được share, hoặc dataset public trong OU).

---

## Permission Model

Dataset áp dụng phân quyền bitwise:

| Flag | Value | Mô tả |
|------|-------|-------|
| None | 0 | Không có quyền |
| Read | 1 | Xem dataset & items |
| Update | 2 | Cập nhật dataset & items |
| Delete | 4 | Xoá dataset & items |
| Share | 8 | Chia sẻ dataset |
| Collaborate | 3 | Read + Update |
| FullControl | 15 | Read + Update + Delete + Share |

### Permission Resolution

```
EffectiveMask = Default Permissions | Shared Permissions
```

**Default Permissions:**
- Owner: FullControl (15)
- Manager của OU mà dataset thuộc về: FullControl (15)
- User cùng OU + dataset.IsPublicToUnit=true: Read (1)

**Shared Permissions:** Từ AccessShares, ưu tiên quyền cao nhất nếu trùng lặp.

### Security Policy

- Khi user không có quyền truy cập dataset, API trả về **404 Not Found** thay vì 403 Forbidden — không leak thông tin về sự tồn tại của dataset.

---

## 1. List My Datasets

Liệt kê tất cả dataset mà user hiện tại có quyền truy cập, phân trang dạng offset.

```
GET /api/v1/user/me/datasets?page=1&pageSize=20
```

### Query Parameters

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| page | int | 1 | Số trang |
| pageSize | int | 20 | Số item mỗi trang (max 100) |

### Response (200)

```json
{
  "items": [
    {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "name": "Hợp đồng 2025",
      "ouName": "Phòng IT",
      "ouId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
      "itemCount": 15,
      "documentCount": 12,
      "isPublicToUnit": true,
      "templateMetadataId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "templateMetadataName": "Báo cáo template",
      "createdAt": "2025-01-01T00:00:00Z",
      "updatedAt": "2025-06-01T00:00:00Z"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 42,
  "totalPages": 3
}
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| items | array | Danh sách datasets |
| items[].id | guid | ID dataset |
| items[].name | string | Tên dataset |
| items[].ouName | string? | Tên OU mà dataset thuộc về |
| items[].ouId | guid? | ID OU |
| items[].itemCount | int | Tổng số items (folders + documents) |
| items[].documentCount | int | Số documents trong dataset |
| items[].isPublicToUnit | bool | Dataset có public cho OU không |
| items[].templateMetadataId | guid? | Template metadata ID (nếu có) |
| items[].templateMetadataName | string? | Tên template metadata (nếu có) |
| items[].createdAt | datetime | Thời gian tạo |
| items[].updatedAt | datetime | Thời gian cập nhật cuối |
| page | int | Trang hiện tại |
| pageSize | int | Kích thước trang |
| totalCount | int | Tổng số datasets |
| totalPages | int | Tổng số trang |

### Behaviour

- Kết quả bao gồm: dataset user sở hữu, dataset được share, dataset public trong OU user thuộc về, dataset trong OU user là manager
- Sort: `UpdatedAt DESC` — dataset mới cập nhật lên đầu

---

## 2. Get Dataset Detail

Lấy thông tin chi tiết một dataset.

```
GET /api/v1/user/me/datasets/{id:guid}
```

### Response (200)

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "Hợp đồng 2025",
  "description": "Tổng hợp hợp đồng năm 2025",
  "ownerName": "Nguyễn Văn A",
  "ouName": "Phòng IT",
  "ouId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
  "itemCount": 15,
  "documentCount": 12,
  "isPublicToUnit": true,
  "templateMetadataId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "templateMetadataName": "Báo cáo template",
  "createdAt": "2025-01-01T00:00:00Z",
  "updatedAt": "2025-06-01T00:00:00Z"
}
```

### Response (404)

```json
{
  "error": "Dataset not found"
}
```

---

## 3. Create Dataset

Tạo một dataset mới.

```
POST /api/v1/user/me/datasets
```

### Request Body

```json
{
  "name": "Hợp đồng 2025",
  "description": "Tổng hợp hợp đồng năm 2025",
  "ouId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
  "isPublicToUnit": false,
  "templateMetadataId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| name | string | Có | Tên dataset (max 255 ký tự) |
| description | string? | Không | Mô tả (max 1000 ký tự) |
| ouId | guid? | Không | OU mà dataset thuộc về. Nếu không cung cấp, dataset là personal |
| isPublicToUnit | bool | Không | Mặc định `false`. Nếu `true`, tất cả member của OU đều có quyền Read |
| templateMetadataId | guid | Có | Template metadata để định nghĩa schema cho metadata extraction |

### Validation

- `name` không được rỗng hoặc chỉ whitespace
- `name` tối đa 255 ký tự
- Nếu `ouId` được cung cấp, user phải thuộc OU đó (Staff hoặc Manager)
- `templateMetadataId` là bắt buộc, template phải tồn tại
- Owner được tự động set = user hiện tại

### Response (201 Created)

Trả về `DatasetDetailDto` (cấu trúc giống GET detail).

### Response (400)

```json
{
  "error": "Dataset name is required"
}
```

---

## 4. Update Dataset

Cập nhật thông tin dataset. Chỉ owner, manager của OU, hoặc user được share quyền Update mới có thể cập nhật.

```
PUT /api/v1/user/me/datasets/{id:guid}
```

### Request Body

```json
{
  "name": "Hợp đồng 2025 - Updated",
  "description": "Mô tả mới",
  "isPublicToUnit": true,
  "templateMetadataId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| name | string? | Không | Tên mới (nếu cung cấp, không được rỗng, max 255) |
| description | string? | Không | Mô tả mới (nếu cung cấp, max 1000). Truyền `""` để xoá mô tả |
| isPublicToUnit | bool? | Không | `true/false` để thay đổi, không gửi field để giữ nguyên |
| templateMetadataId | guid? | Không | Template metadata. **Chỉ gán được 1 lần** — không thể thay đổi nếu dataset đã có template |

### Behaviour

- Chỉ cập nhật các field được gửi lên (partial update)
- `templateMetadataId`: nếu dataset **chưa có** template → gán được lần đầu. Nếu **đã có** → báo lỗi, không cho thay đổi

### Response (200)

Trả về `DatasetDetailDto` (cấu trúc giống GET detail).

### Response (404)

```json
{
  "error": "Dataset not found"
}
```

---

## 5. Delete Dataset

Xoá dataset (soft delete → vào Trash). Chỉ owner, manager của OU, hoặc user được share quyền Delete mới có thể xoá.

```
DELETE /api/v1/user/me/datasets/{id:guid}
```

### Behaviour

**Non-cascading soft delete (Windows Trash pattern):**

| Entity | Hành vi |
|--------|---------|
| Dataset | `IsDeleted = true` — vào Trash |
| DatasetItem (tất cả items) | **Không bị ảnh hưởng** — tự động ẩn vì Dataset cha bị deleted |
| Document (file gốc) | **Không bị ảnh hưởng** — giữ nguyên |
| AccessShare | **Không bị ảnh hưởng** — giữ nguyên |
| SystemStatistics | Decrement TotalDatasets (per-OU + global) |
| S3 files (`ObjectKeyFilePdf`) | **Không xoá** |

> **Khôi phục:** Admin vào Trash → Restore Dataset → tất cả items/documents tự động reappear.

**Cơ chế:** `AuditEntityInterceptor` chuyển lệnh DELETE thành UPDATE, set `IsDeleted=true`, `DeletedAt=UtcNow`, `DeletedBy=currentUserId`.

### Response (204 No Content)

Không có body.

### Response (404)

```json
{
  "error": "Dataset not found"
}
```

---

## 6. List Dataset Items

Lấy danh sách items trong dataset (cây thư mục). Hỗ trợ lọc theo parentId để xem từng cấp thư mục.

```
GET /api/v1/user/me/datasets/{id:guid}/items?parentId=
```

### Query Parameters

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| parentId | guid? | (null) | Lọc items trong folder cụ thể. `null` = items ở root |

### Behavior

- Gọi lần đầu không gửi `parentId` → lấy items ở root
- Click vào folder → gọi với `parentId` là ID của folder đó để xem children
- Sort: Folder lên trước, theo `SortOrder` → theo `Name` alphabet

### Response (200)

```json
{
  "path": "/Hợp đồng 2025/",
  "level": 1,
  "hasChildren": true,
  "childCount": 2,
  "items": [
    {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa8",
      "name": "Báo cáo tháng 1",
      "itemType": "Folder",
      "documentId": null,
      "createdAt": "2025-01-01T00:00:00Z",
      "item": null
    },
    {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa9",
      "name": "Bao-cao-2025.pdf",
      "itemType": "Document",
      "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa9",
      "createdAt": "2025-01-02T00:00:00Z",
      "item": {
        "fileName": "Bao-cao-2025.pdf",
        "status": "ProcessingOcr",
        "isOcred": false,
        "isQaGenerated": false,
        "job": {
          "ocrJobId": "ext-task-xyz-123",
          "genQaJobId": null,
          "statusOcr": "Processing",
          "statusGenQa": "Pendding"
        }
      }
    }
  ]
}
```

### Response Fields

**Outer (DatasetItemsResponseDto)**

| Field | Type | Description |
|-------|------|-------------|
| path | string | Materialized path của PARENT folder |
| level | int | Level của PARENT folder + 1 |
| hasChildren | bool | Có items con không |
| childCount | int | Số lượng items con |
| items | array | Danh sách items |

**Inner (DatasetItemDto)**

| Field | Type | Description |
|-------|------|-------------|
| id | guid | ID của DatasetItem |
| name | string | Tên file/folder |
| itemType | string | `"Folder"` hoặc `"Document"` |
| documentId | guid? | ID của Document (chỉ có nếu itemType = Document) |
| createdAt | datetime | Thời gian tạo |
| item | object? | Metadata document. `null` khi itemType = Folder |

**Nested (DatasetItemDocumentDto — chỉ khi itemType = Document)**

| Field | Type | Description |
|-------|------|-------------|
| fileName | string | Tên file gốc |
| status | string | Trạng thái xử lý (xem bảng bên dưới) |
| isOcred | bool | Đã OCR chưa |
| isQaGenerated | bool | Đã gen Q&A chưa |
| job | object? | Thông tin job xử lý. `null` nếu không có job đang chạy |

**Job fields (DocumentJobBriefDto):**

| Field | Type | Description |
|-------|------|-------------|
| ocrJobId | string? | ID của OCR job bên ngoài (có khi status = `ProcessingOcr`) |
| genQaJobId | string? | ID của GenQA job (Hangfire) (có khi status = `ProcessingGenQa`) |
| statusOcr | string? | Trạng thái OCR job: `Pendding`, `Processing`, `Succeeded`, `Failed`, `Canceled` |
| statusGenQa | string? | Trạng thái GenQA job: `Pendding`, `Processing`, `Succeeded`, `Failed`, `Canceled` |

**Status values (Document status):**

| Status | Ý nghĩa |
|--------|---------|
| `Uploaded` | File vừa upload, chưa xử lý gì |
| `ProcessingOcr` | Đang chạy OCR |
| `Successed` | OCR hoàn tất, đã gen Q&A thành công |
| `Failed` | Xử lý thất bại (OCR hoặc GenQA lỗi) |
| `ProcessingGenQa` | Đang gen Q&A |
| `Canceled` | User hủy tiến trình xử lý |

### Response (404)

```json
{
  "error": "Dataset not found"
}
```

---

## 7. Create Item (Folder / Document)

Tạo một item mới trong dataset (folder hoặc document). Yêu cầu quyền Update trên dataset.

```
POST /api/v1/user/me/datasets/{id:guid}/create-item
```

### Request (multipart/form-data)

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| type | int | Có | `0` = Folder, `1` = Document |
| name | string | Có khi type=0 | Tên folder (max 255 ký tự). Bỏ qua khi type=1 |
| parentId | guid? | Không | ID của folder cha. `null` = tạo ở root |
| file | file | Có khi type=1 | File PDF cần upload (max 100MB) |

### Behaviour

- **type=0 (Folder):** Tạo DatasetItem với ItemType=Folder, DocumentId=null
- **type=1 (Document):**
  - Validate file: chỉ hỗ trợ PDF, kiểm tra magic number `%PDF`
  - Kiểm tra trùng tên file trong hệ thống
  - Tạo Document record + upload file lên S3 (bucket `ocr-upload-pdf`) + lưu cache local
  - Tạo DatasetItem với ItemType=Document, DocumentId=Document vừa tạo
- **Path** và **Level** tự động tính dựa trên parent:
  - Root: `Path = "/{name}/"`, `Level = 0`
  - Có parent: `Path = "{parent.Path}{name}/"`, `Level = parent.Level + 1`
- **SortOrder** tự động = max SortOrder trong cùng parent + 1

### Security

- Yêu cầu quyền **Update** trên dataset (Owner, Manager OU, hoặc được share quyền Update)
- Nếu dataset không tồn tại hoặc không có quyền: trả về **404**

### Response (201 Created)

```json
{
  "itemId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
  "name": "Bao-cao-2025.pdf",
  "itemType": "Document",
  "path": "/thu-muc/bao-cao/",
  "level": 2,
  "sortOrder": 0,
  "createdAt": "2025-01-01T00:00:00Z",
  "item": {
    "fileName": "Bao-cao-2025.pdf",
    "status": "Uploaded",
    "isOcred": false,
    "isQaGenerated": false
  }
}
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| itemId | guid | ID của DatasetItem vừa tạo |
| documentId | guid? | ID của Document (null nếu type=Folder) |
| name | string | Tên folder hoặc tên file gốc |
| itemType | string | `"Folder"` hoặc `"Document"` |
| path | string | Materialized path của item |
| level | int | Độ sâu trong cây thư mục |
| sortOrder | int | Thứ tự sắp xếp trong cùng parent |
| createdAt | datetime | Thời gian tạo |
| item | object? | Thông tin document. `null` khi type=Folder. Gồm: `fileName`, `status`, `isOcred`, `isQaGenerated` |

### Response (400)

```json
{
  "error": "Folder name is required"
}
```

```json
{
  "error": "Only PDF files are supported"
}
```

```json
{
  "error": "A file with name 'Bao-cao-2025.pdf' already exists"
}
```

---

## 8. Delete Item (Folder / Document)

Xoá một item trong dataset (soft delete → vào Trash). Yêu cầu quyền Update trên dataset.

```
DELETE /api/v1/user/me/datasets/{id:guid}/items/{itemId:guid}
```

### Behaviour

**Non-cascading soft delete (Windows Trash pattern):**

**type=Folder:**
- Chỉ soft-delete folder chính (`IsDeleted = true`)
- Tất cả con cháu **không bị ảnh hưởng** — tự động ẩn qua Path filter

**type=Document:**
- Chỉ soft-delete DatasetItem (`IsDeleted = true`)
- Document entity **không bị ảnh hưởng** — ẩn qua DatasetItem link

> **Khôi phục:** Admin vào Trash → Restore → tất cả con cháu tự động reappear.

### Response (204 No Content)

Không có body.

### Response (404)

```json
{
  "error": "Dataset not found"
}
```

---

## Common Error Responses

| Status | Description |
|--------|-------------|
| 401 | Unauthorized — Chưa đăng nhập |
| 404 | Not Found — Dataset không tồn tại hoặc không có quyền truy cập |
| 400 | Bad Request — Validation lỗi (name rỗng, quá dài, OU không hợp lệ) |
| 500 | Internal Server Error — Lỗi hệ thống |

---

## Performance

- **List datasets:** Query qua `GetAccessibleDatasetIdsAsync()` sử dụng index trên `OwnerUserId`, `OUId`, `AccessShares`
- **Get detail:** Single query với `Include(Owner).Include(OU).Include(Items)`
- **Items tree:** Query với composite index `(DatasetId, Level)` + `ParentId`. Thêm subquery filter để loại items dưới folder bị deleted (Path.StartsWith)
- **Soft delete:** O(1) — chỉ update 1 row, không cascade
- Tất cả query đều dùng `AsNoTracking()` cho read-only operations
