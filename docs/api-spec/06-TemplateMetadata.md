# Template Metadata API

Base URL: `/api/v1/templates`

Template Metadata định nghĩa **JSON Schema** cho metadata extraction. Mỗi Dataset có thể gán một template để định nghĩa cấu trúc metadata mong muốn.

Yêu cầu **đăng nhập** (`[Authorize]`). Các thao tác ghi (Create/Update/Delete) yêu cầu **Admin**.

---

## JSON Schema Format

`jsonSchema` lưu dưới dạng string, là một **JSON Schema hợp lệ (draft 2020-12)**. Frontend cần parse schema này để render dynamic form cho user nhập metadata.

### Cấu trúc schema

```json
{
  "type": "object",
  "properties": {
    "fieldName": {
      "type": "string",
      "title": "Tên hiển thị",
      "description": "Mô tả chi tiết"
    }
  },
  "required": ["fieldName"]
}
```

### Field type → Input type mapping

| JSON Schema `type` | Input type | Ghi chú |
|-------------------|------------|---------|
| `string` | `<input type="text">` | Mặc định. Nếu có `format: "date"` → date picker, `format: "uri"` → URL input |
| `string` + `enum` | `<select>` | Dropdown. `enum` là mảng các option |
| `number` / `integer` | `<input type="number">` | `integer` chỉ cho số nguyên |
| `boolean` | `<input type="checkbox">` | Checkbox |
| `array` | `<textarea>` hoặc multi-input | Nhập JSON array hoặc từng item |

### Field properties

| Property | Type | Mục đích cho frontend |
|----------|------|----------------------|
| `title` | string | Label của field |
| `description` | string | Tooltip/helper text |
| `type` | string | Xác định loại input |
| `enum` | string[] | Dropdown options |
| `format` | string | Format đặc biệt: `date`, `email`, `uri`, ... |
| `default` | any | Giá trị mặc định |
| `minLength` / `maxLength` | int | Giới hạn độ dài string |
| `minimum` / `maximum` | int | Giới hạn số |
| `pattern` | string | Regex validate |

### Ví dụ schema hoàn chỉnh

```json
{
  "type": "object",
  "title": "Thông tin báo cáo",
  "properties": {
    "reportTitle": {
      "type": "string",
      "title": "Tên báo cáo",
      "description": "Nhập tên đầy đủ của báo cáo",
      "maxLength": 200
    },
    "reportDate": {
      "type": "string",
      "title": "Ngày báo cáo",
      "format": "date"
    },
    "department": {
      "type": "string",
      "title": "Phòng ban",
      "enum": ["IT", "HR", "Finance", "Operation"],
      "description": "Chọn phòng ban sở hữu báo cáo"
    },
    "revenue": {
      "type": "number",
      "title": "Doanh thu (tỷ VND)",
      "minimum": 0
    },
    "isApproved": {
      "type": "boolean",
      "title": "Đã phê duyệt",
      "default": false
    }
  },
  "required": ["reportTitle", "reportDate", "department"],
  "additionalProperties": false
}
```

### Flow từ template → form → metadata

```
Admin tạo schema → Dataset gán template → Document extract metadata 
                                    → Frontend parse schema render form
                                    → User điền → PUT metadata (isExtracted=true)
```

---

## 1. List Templates

Liệt kê tất cả template metadata.

```
GET /api/v1/templates
```

### Response (200)

```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "name": "Báo cáo template",
    "description": "Template cho báo cáo tài chính",
    "createdAt": "2025-01-01T00:00:00Z"
  }
]
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| id | guid | ID template |
| name | string | Tên template |
| description | string? | Mô tả |
| createdAt | datetime | Thời gian tạo |

### Behaviour

- Sort: `CreatedAt DESC` — template mới nhất lên đầu
- Chỉ trả về danh sách gọn (không bao gồm JsonSchema)
- Không phân trang

---

## 2. Get Template Detail

Lấy chi tiết template bao gồm JSON Schema.

```
GET /api/v1/templates/{id:guid}
```

### Response (200)

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "Báo cáo template",
  "description": "Template cho báo cáo tài chính",
  "jsonSchema": "{...}",
  "createdAt": "2025-01-01T00:00:00Z",
  "updatedAt": "2025-06-01T00:00:00Z",
  "createdBy": "3fa85f64-5717-4562-b3fc-2c963f66afa7"
}
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| id | guid | ID template |
| name | string | Tên template |
| description | string? | Mô tả |
| jsonSchema | string | JSON Schema định nghĩa cấu trúc metadata |
| createdAt | datetime | Thời gian tạo |
| updatedAt | datetime | Thời gian cập nhật cuối |
| createdBy | guid? | ID admin tạo template |

### Response (404)

```json
{
  "error": "Template metadata not found"
}
```

---

## 3. Create Template

Tạo template metadata mới. Yêu cầu **Admin**.

```
POST /api/v1/templates
```

### Request Body

```json
{
  "name": "Báo cáo template",
  "description": "Template cho báo cáo tài chính",
  "jsonSchema": "{\"type\":\"object\",\"properties\":{\"field1\":{\"type\":\"string\"}}}"
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| name | string | Có | Tên template (max 255 ký tự) |
| description | string? | Không | Mô tả (max 1000 ký tự) |
| jsonSchema | string | Có | JSON Schema (theo draft 2020-12). Dùng để validate metadata content của document thuộc dataset gán template này |

### Validation

- `name` không được rỗng hoặc chỉ whitespace
- `name` tối đa 255 ký tự
- `description` tối đa 1000 ký tự
- `jsonSchema` là bắt buộc (không validate cú pháp schema ở API layer — lỗi schema sẽ được phát hiện khi dùng)
- Chỉ Admin mới được tạo

### Response (201 Created)

Trả về `TemplateMetadataDetailDto` (cấu trúc giống GET detail).

### Response (400)

```json
{
  "error": "Only admins can create template metadata"
}
```

---

## 4. Update Template

Cập nhật template metadata. Yêu cầu **Admin**.

```
PUT /api/v1/templates/{id:guid}
```

### Request Body

```json
{
  "name": "Báo cáo template v2",
  "description": "Mô tả cập nhật",
  "jsonSchema": "{\"type\":\"object\",\"properties\":{\"field1\":{\"type\":\"string\"},\"field2\":{\"type\":\"integer\"}}}"
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| name | string? | Không | Tên mới (nếu cung cấp, không được rỗng, max 255) |
| description | string? | Không | Mô tả mới (nếu cung cấp, max 1000). Truyền `""` để xoá mô tả |
| jsonSchema | string? | Không | JSON Schema mới |

### Validation

- Chỉ Admin mới được update
- Partial update — chỉ cập nhật các field được gửi lên
- `name` không được rỗng nếu cung cấp

### Behaviour

- Cập nhật template KHÔNG ảnh hưởng đến document đã extract metadata trước đó
- Template chỉ ảnh hưởng đến lần extract metadata tiếp theo (khi regenerate)
- `UpdatedAt` tự động cập nhật

### Response (200)

Trả về `TemplateMetadataDetailDto` (cấu trúc giống GET detail).

### Response (404)

```json
{
  "error": "Template metadata not found"
}
```

---

## 5. Delete Template

Xoá template metadata. Yêu cầu **Admin**.

```
DELETE /api/v1/templates/{id:guid}
```

### Behaviour

- **Hard delete** — xoá khỏi database
- Các Dataset đang tham chiếu template này sẽ có `TemplateMetadataId = NULL` (FK `SetNull`)

### Response (204 No Content)

Không có body.

### Response (404)

```json
{
  "error": "Template metadata not found"
}
```

---

## Common Error Responses

| Status | Description |
|--------|-------------|
| 401 | Unauthorized — Chưa đăng nhập |
| 403 | Forbidden — Không phải Admin (chỉ với Create/Update/Delete) |
| 404 | Not Found — Template không tồn tại |
| 400 | Bad Request — Validation lỗi |
