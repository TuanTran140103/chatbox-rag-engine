# Template: Văn bản Tài chính - Ngân hàng

Template JSON Schema (draft 2020-12) dùng để trích xuất metadata từ các văn bản pháp luật/quy định trong lĩnh vực **Tài chính - Ngân hàng** Việt Nam.

## Auto-seed

Template này **tự động được seed** khi app khởi động bởi `TemplateMetadataSeeder` (xem `Infrastructure/Services/TemplateMetadataSeeder.cs`).

- Đường dẫn: `data/templates/*.template.json`
- **Idempotent**: kiểm tra theo `Name` (bao gồm cả soft-deleted) — nếu đã tồn tại thì skip, không tạo lại
- Thêm template mới: chỉ cần drop thêm 1 file `*.template.json` vào folder
- Cũng tự tạo Qdrant payload index cho từng key trong `indexKeys`

## Files
- `finance-banking.template.json` — Template definition (được seed tự động)
- `examples/finance-banking.example.json` — Ví dụ output trích xuất
- `finance-banking.md` — File này

## Cấu trúc `*.template.json`

```json
{
  "name": "Văn bản Tài chính - Ngân hàng",
  "description": "Mô tả template",
  "indexKeys": ["documentType", "documentNature", "issueDate"],
  "jsonSchema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "properties": { ... },
    "required": [ ... ]
  }
}
```

| Field | Mô tả |
|---|---|
| `name` | Tên template (unique, dùng để check duplicate) |
| `description` | Mô tả (optional) |
| `indexKeys` | Danh sách field dùng tạo Qdrant payload index (optional) |
| `jsonSchema` | JSON Schema (draft 2020-12) định nghĩa metadata structure |

## Fields trong schema

| Field | Type | Required | Qdrant Index | Mô tả |
|---|---|---|---|---|
| `documentNumber` | string | ❌ | — | Số hiệu văn bản (VD: `01/2023/TT-NHNN`) |
| `title` | string | ✅ | — | Tên gọi / Tiêu đề |
| `documentType` | enum (8) | ✅ | **Keyword** | Luật / Nghị định / Thông tư / Quyết định / Công văn / Chỉ thị / Nghị quyết / other |
| `documentNature` | enum (7) | ✅ | **Keyword** | Mới ban hành / Thay thế / Sửa đổi / Bổ sung / Hướng dẫn thi hành / Hủy bỏ / other |
| `issueDate` | string `format: date` | ✅ | **Datetime** | Ngày ban hành (YYYY-MM-DD) — hỗ trợ range filter |
| `effectiveDate` | string `format: date` | ❌ | — | Ngày có hiệu lực (YYYY-MM-DD) |
| `relatedDocuments` | array of string | ❌ | — | Số hiệu văn bản liên quan (bổ sung/sửa đổi/thay thế) |

## indexKeys

```json
["documentType", "documentNature", "issueDate"]
```

- `documentType`, `documentNature` → **Keyword** index (low cardinality, ~7-8 giá trị) → filter nhanh theo phân loại
- `issueDate` → **Datetime** index → filter range theo năm/quý/tháng

⚠️ Lưu ý: `issueDate` tự động được tạo `Datetime` index (không phải `Keyword`) nhờ fix trong `MetadataSchemaHelper.GetPayloadSchemaType` detect `format: "date"`.

## Thêm template mới (thủ công qua API)

Nếu muốn thêm template qua API (không cần restart app), yêu cầu quyền **Admin**:

```bash
curl -X POST http://localhost:5184/api/v1/templates \
  -H "Authorization: Bearer <ADMIN_TOKEN>" \
  -H "Content-Type: application/json" \
  -d "{
    \"name\": \"Tên template mới\",
    \"description\": \"Mô tả\",
    \"jsonSchema\": \"$(jq -c . path/to/schema.json | jq -R .)\",
    \"indexKeys\": [\"field1\", \"field2\"]
  }"
```

## Filter Qdrant mẫu (sau khi có documents)

Lọc tất cả Thông tư ban hành trong năm 2024:
```json
{
  "must": [
    { "key": "documentType", "match": { "value": "Thông tư" } },
    { "key": "issueDate", "range": { "gte": "2024-01-01", "lte": "2024-12-31" } }
  ]
}
```

Lọc các văn bản sửa đổi/thay thế:
```json
{
  "should": [
    { "key": "documentNature", "match": { "value": "Sửa đổi" } },
    { "key": "documentNature", "match": { "value": "Thay thế" } }
  ]
}
```
