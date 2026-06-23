# Dataset API

Base URL: `/api/v1/datasets`

Tất cả API trong nhóm này đều thao tác trên dữ liệu của **user hiện tại** (xác thực qua Kong proxy headers, không cần truyền userId).

Yêu cầu **đăng nhập** (`[Authorize]`), không yêu cầu Admin.

Các API này cho phép user thao tác với dataset mà họ có quyền truy cập (sở hữu, được share, hoặc dataset trong Department họ là Manager).

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
- **Owner** của Dataset: FullControl (15)
- **Manager** của Department mà Dataset thuộc về: Read (1)

**Shared Permissions:** Từ AccessShares (ShareToUserId hoặc ShareToDepartmentId), ưu tiên quyền cao nhất nếu trùng lặp.

### Security Policy

- Khi user không có quyền truy cập dataset, API trả về **404 Not Found** thay vì 403 Forbidden — không leak thông tin về sự tồn tại của dataset.

---

## 1. List My Datasets

Liệt kê tất cả dataset mà user hiện tại có quyền truy cập, phân trang dạng offset.

```
GET /api/v1/datasets?page=1&pageSize=20
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
      "description": "Tổng hợp hợp đồng năm 2025",
      "itemCount": 15,
      "documentCount": 12,
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
| items[].description | string? | Mô tả dataset |
| items[].itemCount | int | Tổng số items (folders + documents) |
| items[].documentCount | int | Số documents trong dataset |
| items[].templateMetadataId | guid? | Template metadata ID (nếu có) |
| items[].templateMetadataName | string? | Tên template metadata (nếu có) |
| items[].createdAt | datetime | Thời gian tạo |
| items[].updatedAt | datetime | Thời gian cập nhật cuối |
| page | int | Trang hiện tại |
| pageSize | int | Kích thước trang |
| totalCount | int | Tổng số datasets |
| totalPages | int | Tổng số trang |

### Behaviour

- Kết quả bao gồm: dataset user sở hữu, dataset được share, dataset trong Department user là Manager
- Sort: `UpdatedAt DESC` — dataset mới cập nhật lên đầu

---

## 2. Get Dataset Detail

Lấy thông tin chi tiết một dataset (cấu trúc giống List, chỉ khác là single item).

```
GET /api/v1/datasets/{id:guid}
```

### Response (200)

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "Hợp đồng 2025",
  "description": "Tổng hợp hợp đồng năm 2025",
  "itemCount": 15,
  "documentCount": 12,
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
POST /api/v1/datasets
```

### Request Body

```json
{
  "name": "Hợp đồng 2025",
  "description": "Tổng hợp hợp đồng năm 2025",
  "departmentId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
  "templateMetadataId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| name | string | Có | Tên dataset (max 255 ký tự) |
| description | string? | Không | Mô tả (max 1000 ký tự) |
| departmentId | guid? | Không | Department mà dataset thuộc về. Nếu không cung cấp, dataset là personal. Manager của department có quyền Read |
| templateMetadataId | guid | Có | Template metadata để định nghĩa schema cho metadata extraction |

### Validation

- `name` không được rỗng hoặc chỉ whitespace
- `name` tối đa 255 ký tự
- Nếu `departmentId` được cung cấp, user phải thuộc department đó (Staff hoặc Manager)
- `templateMetadataId` là bắt buộc, template phải tồn tại
- Owner được tự động set = user hiện tại

### Response (201 Created)

Trả về `DatasetDto` (cấu trúc giống GET detail).

### Response (400)

```json
{
  "error": "Dataset name is required"
}
```

---

## 4. Update Dataset

Cập nhật thông tin dataset. Chỉ owner hoặc user được share quyền Update mới có thể cập nhật. (Manager của Department chỉ có Read, không có Update)

```
PUT /api/v1/datasets/{id:guid}
```

### Request Body

```json
{
  "name": "Hợp đồng 2025 - Updated",
  "description": "Mô tả mới"
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| name | string? | Không | Tên mới (nếu cung cấp, không được rỗng, max 255) |
| description | string? | Không | Mô tả mới (nếu cung cấp, max 1000). Truyền `""` để xoá mô tả |

### Behaviour

- Chỉ cập nhật các field được gửi lên (partial update)
- `templateMetadataId` chỉ gán được lúc tạo dataset, không thể thay đổi sau đó

### Response (200)

Trả về `DatasetDto` (cấu trúc giống GET detail).

### Response (404)

```json
{
  "error": "Dataset not found"
}
```

---

## 5. Delete Dataset

Xoá dataset (soft delete → vào Trash). Chỉ owner, hoặc user được share quyền Delete mới có thể xoá. (Manager của Department chỉ có Read, không có Delete)

```
DELETE /api/v1/datasets/{id:guid}
```

### Behaviour

**Non-cascading soft delete (Windows Trash pattern):**

| Entity | Hành vi |
|--------|---------|
| Dataset | `IsDeleted = true` — vào Trash |
| DatasetItem (tất cả items) | **Không bị ảnh hưởng** — tự động ẩn vì Dataset cha bị deleted |
| Document (file gốc) | **Không bị ảnh hưởng** — giữ nguyên |
| AccessShare | **Không bị ảnh hưởng** — giữ nguyên |
| SystemStatistics | Decrement TotalDatasets (global) |
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
GET /api/v1/datasets/{id:guid}/items?parentId=
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
| `Uploading` | Đang chờ client upload file lên MinIO qua presigned URL |
| `Uploaded` | File đã upload xong, chưa xử lý gì |
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

## 7. Create Folder

Tạo một folder mới trong dataset.

```
POST /api/v1/datasets/{id:guid}/create-folder
```

### Request Body

```json
{
  "name": "Báo cáo tháng 1",
  "parentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| name | string | Có | Tên folder (max 255 ký tự) |
| parentId | guid? | Không | ID của folder cha. `null` = tạo ở root |

### Behaviour

- Tạo DatasetItem với `ItemType=Folder`, `DocumentId=null`
- Path tự động tính dựa trên parent, `SortOrder` = max trong cùng parent + 1

### Response (201 Created)

```json
{
  "itemId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "documentId": null,
  "name": "Báo cáo tháng 1",
  "itemType": "Folder",
  "path": "/Báo cáo tháng 1/",
  "level": 1,
  "sortOrder": 0,
  "createdAt": "2025-01-01T00:00:00Z",
  "item": null
}
```

### Response (400)

```json
{
  "error": "Folder name is required"
}
```

---

## 8. Init Upload (Single File)

Tạo document record trước, trả về presigned URL để client upload trực tiếp lên MinIO.

```
POST /api/v1/datasets/{id:guid}/init-upload
```

### Request Body

```json
{
  "fileName": "Bao-cao-2025.pdf",
  "fileSize": 5242880,
  "contentType": "application/pdf",
  "parentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| fileName | string | Có | Tên file gốc (chỉ hỗ trợ .pdf) |
| fileSize | int64 | Có | Kích thước file (bytes). Tối đa 100MB (104857600 bytes) |
| contentType | string? | Không | MIME type. Mặc định `application/pdf` |
| parentId | guid? | Không | ID folder cha. `null` = root |

### Behaviour

1. Validate file extension (`.pdf`) và `fileSize ≤ 100MB`
2. Kiểm tra không trùng tên file với documents đang tồn tại
3. Tạo Document record với status `Uploading`
4. Sinh presigned URL cho PUT upload (thời hạn 1 giờ)
5. Trả về URL cho client

### Response (200)

```json
{
  "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
  "objectKey": "Bao-cao-2025.pdf",
  "presignedUrl": "http://192.168.1.4:9000/ocr-upload-pdf/Bao-cao-2025.pdf?X-Amz-Algorithm=...",
  "expiresAt": "2025-01-01T01:00:00Z"
}
```

### Client Flow

1. `init-upload` → nhận `presignedUrl`
2. `PUT {presignedUrl}` với body là file content, header `Content-Type: application/pdf`
3. `complete-upload` → tạo DatasetItem, chuyển document sang `Uploaded`

---

## 9. Init Upload Bulk

Tạo nhiều documents cùng lúc, trả về danh sách presigned URLs tương ứng.

```
POST /api/v1/datasets/{id:guid}/init-upload-bulk
```

### Request Body

```json
{
  "files": [
    {
      "fileName": "Bao-cao-Q1.pdf",
      "fileSize": 4194304,
      "contentType": "application/pdf"
    },
    {
      "fileName": "Bao-cao-Q2.pdf",
      "fileSize": 3145728
    }
  ]
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| files | array | Có | Danh sách files (tối đa 50) |
| files[].fileName | string | Có | Tên file gốc (`.pdf`) |
| files[].fileSize | int64 | Có | Kích thước file (bytes). Tối đa 100MB |
| files[].contentType | string? | Không | MIME type. Mặc định `application/pdf` |

### Behaviour

- Validate tất cả files trước khi tạo bất kỳ document nào (fail nhanh nếu có lỗi)
- Kiểm tra: extension `.pdf`, `fileSize ≤ 100MB`, không trùng tên trong request, không trùng tên trong DB
- Tất cả documents tạo ở **root** dataset (không hỗ trợ `parentId`)
- Thứ tự response khớp với thứ tự request

### Response (200)

```json
{
  "documents": [
    {
      "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
      "objectKey": "Bao-cao-Q1.pdf",
      "presignedUrl": "http://...",
      "expiresAt": "2025-01-01T01:00:00Z"
    },
    {
      "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa8",
      "objectKey": "Bao-cao-Q2.pdf",
      "presignedUrl": "http://...",
      "expiresAt": "2025-01-01T01:00:00Z"
    }
  ]
}
```

### Client Flow

1. `init-upload-bulk` → nhận danh sách `presignedUrl`
2. Upload từng file lên MinIO qua `PUT {presignedUrl}`
3. Gọi `complete-upload/{documentId}` cho từng document riêng lẻ

---

## 10. Complete Upload

Xác nhận upload hoàn tất, tạo DatasetItem và chuyển document từ `Uploading` → `Uploaded`.

```
POST /api/v1/datasets/{id:guid}/complete-upload/{documentId:guid}
```

### Request Body

```json
{
  "parentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| parentId | guid? | Không | ID folder cha. `null` = root |

### Behaviour

1. Kiểm tra document tồn tại và đang ở status `Uploading`
2. Kiểm tra file đã upload thành công trên MinIO
3. Lấy metadata từ MinIO: so sánh `ContentLength` với `FileSize` đã lưu
4. Đọc 8 bytes đầu file, kiểm tra magic bytes PDF (`%PDF`)
5. Tạo DatasetItem trỏ đến document
6. Chuyển document status → `Uploaded`

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

---

## 11. Renew Upload URL

Tạo lại presigned URL mới cho document đang ở trạng thái `Uploading` (khi URL cũ hết hạn).

```
POST /api/v1/datasets/{id:guid}/renew-upload-url/{documentId:guid}
```

### Response (200)

```json
{
  "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
  "objectKey": "Bao-cao-2025.pdf",
  "presignedUrl": "http://...",
  "expiresAt": "2025-01-01T02:00:00Z"
}
```

---

## 12. Delete Item (Folder / Document)

Xoá một item trong dataset (soft delete → vào Trash). Yêu cầu quyền Update trên dataset.

```
DELETE /api/v1/datasets/{id:guid}/items/{itemId:guid}
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
| 400 | Bad Request — Validation lỗi (name rỗng, quá dài, department không hợp lệ) |
| 500 | Internal Server Error — Lỗi hệ thống |

---

## Performance

- **List datasets:** Query qua `GetAccessibleDatasetIdsAsync()` sử dụng index trên `OwnerUserId`, `DepartmentId`, `AccessShares`
- **Get detail:** Single query với `Include(Items)`
- **Items tree:** Query với composite index `(DatasetId, Level)` + `ParentId`. Thêm subquery filter để loại items dưới folder bị deleted (Path.StartsWith)
- **Soft delete:** O(1) — chỉ update 1 row, không cascade
- Tất cả query đều dùng `AsNoTracking()` cho read-only operations
