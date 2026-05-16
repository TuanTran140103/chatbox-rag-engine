# Search API (AI Agent)

Base URL: `/api/v1/search`

Tất cả API trong nhóm này yêu cầu **đăng nhập** (`[Authorize]`), không yêu cầu Admin.

Thiết kế cho AI Agents sử dụng — mỗi endpoint tương ứng một tool riêng biệt.

---

## 1. Read Document

Đọc nội dung document, cho phép chọn loại nội dung muốn lấy (summary hoặc full OCR).

```
GET /api/v1/search/documents/{documentId:guid}?contentType=summary
```

### Query Parameters

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| contentType | string? | `null` | Loại nội dung: `summary`, `fullContent`, hoặc `null` để không trả content |

### Response (200)

```json
{
  "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "datasetId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
  "fileName": "Bao-cao-2025.pdf",
  "content": "# Bao cao 2025\n\n## Section 1...",
  "contentType": "fullContent"
}
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| documentId | guid | ID document |
| datasetId | guid | ID dataset chứa document |
| fileName | string | Tên file gốc |
| content | string? | Nội dung tuỳ theo `contentType`: `summary` → SummaryContent, `fullContent` → OcrContent, `null` → không trả |
| contentType | string? | Loại content đã chọn |

### Response (403)

```json
{
  "error": "Access denied"
}
```

### Response (404)

```json
{
  "error": "Document not found"
}
```

---

## 2. Search Documents By Name

Tìm kiếm document theo tên file (fuzzy search bằng trigram). Có thể giới hạn trong một số dataset nhất định.

```
POST /api/v1/search/documents/by-name
```

### Request Body

```json
{
  "queryText": "bao cao 2025",
  "datasetIds": [
    "3fa85f64-5717-4562-b3fc-2c963f66afa7"
  ]
}
```

### Request Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| queryText | string | Có | Chuỗi tìm kiếm (dùng `ILIKE` + `TrigramsAreSimilar`) |
| datasetIds | guid[]? | Không | Giới hạn search trong các dataset này. Nếu `null`, search toàn bộ dataset user có quyền |

### Behaviour

- Dùng PostgreSQL **trigram GIN index** trên `FileName` — hỗ trợ `ILIKE '%query%'` và `TrigramsAreSimilar`
- Kết quả sắp xếp theo độ tương đồng (trigram similarity distance)
- Giới hạn tối đa **20 kết quả**
- Nếu `datasetIds` chứa dataset không nằm trong quyền user → trả về **403**

### Response (200)

```json
[
  {
    "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "datasetId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
    "fileName": "Bao-cao-2025.pdf"
  },
  {
    "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa8",
    "datasetId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
    "fileName": "Bao-cao-2024.pdf"
  }
]
```

### Response Fields (array)

| Field | Type | Description |
|-------|------|-------------|
| documentId | guid | ID document |
| datasetId | guid | ID dataset chứa document |
| fileName | string | Tên file gốc |

### Response (403)

```json
{
  "error": "Access denied to datasets: 3fa85f64-5717-4562-b3fc-2c963f66afa9"
}
```

---

## 3. Vector Search

Semantic search trên nội dung document qua Qdrant vector database.

```
POST /api/v1/search/vector
```

### Request Body

```json
{
  "queryText": "báo cáo doanh thu quý 1",
  "datasetIds": [
    "3fa85f64-5717-4562-b3fc-2c963f66afa7"
  ],
  "metadataFilter": {
    "documentType": "Báo cáo",
    "year": 2025
  },
  "scoreThreshold": 0.3,
  "topK": 10
}
```

### Request Fields

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| queryText | string | Có | — | Câu query semantic |
| datasetIds | guid[]? | Không | `null` | Giới hạn search trong các dataset này. Dùng làm **shard key** để route đúng shard Qdrant |
| metadataFilter | object? | Không | `null` | Filter metadata (key-value). Server tự suy luận kiểu Qdrant condition từ `ValueKind` — xem bảng dưới |
| scoreThreshold | float | Không | 0.3 | Ngưỡng similarity score tối thiểu (0.0 - 1.0) |
| topK | int | Không | 10 | Số kết quả tối đa trả về |

### MetadataFilter Type Inference

Server tự động suy luận Qdrant condition từ `JsonElement.ValueKind`:

| JSON value | Kiểu JSON | Qdrant Condition |
|------------|-----------|------------------|
| `"chuỗi"` | String | `Match.Keyword` — keyword exact match |
| `2025` | Number integer | `Match.Integer` — integer exact match |
| `3.14` | Number float | `Range` — Gte = Lte = giá trị đó |
| `true` / `false` | Boolean | `Match.Boolean` |

> **Ghi chú:** Date values được lưu dưới dạng string trong Qdrant payload (vd: `"2025-01-15"`), nên dùng `Match.Keyword` (exact match) cho date.

### Behaviour

- Embed query text bằng `IEmbeddingGenerator<string, Embedding<float>>`
- Search trên collection `documents` trong Qdrant
- Dùng **shard key** là dataset IDs — chỉ query các shard có chứa dataset thuộc quyền
- Kết quả dedup theo `documentId` (nhiều chunks từ cùng document chỉ lấy 1)
- Document từ summary chunks: ưu tiên lấy `contentFullForSummary` (nội dung gốc đầy đủ) thay vì summary text
- Enrich tên file từ database trước khi trả về
- Nếu `datasetIds` chứa dataset không nằm trong quyền user → trả về **403**

### Response (200)

```json
[
  {
    "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "datasetId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
    "fileName": "Bao-cao-2025.pdf",
    "content": "Doanh thu quý 1 đạt 80 tỷ đồng...",
    "chunkType": "text",
    "score": 0.85
  }
]
```

### Response Fields (array)

| Field | Type | Description |
|-------|------|-------------|
| documentId | guid | ID document |
| datasetId | guid | ID dataset chứa document |
| fileName | string | Tên file gốc |
| content | string? | Nội dung chunk tìm thấy |
| chunkType | string? | Loại chunk: `text`, `table`, `summary`, ... |
| score | float | Similarity score (0.0 - 1.0) |

### Response (403)

```json
{
  "error": "Access denied to datasets: 3fa85f64-5717-4562-b3fc-2c963f66afa9"
}
```

---

## 4. List Dataset Documents

Liệt kê tất cả documents trong một dataset.

```
GET /api/v1/search/datasets/{datasetId:guid}/documents
```

### Response (200)

```json
[
  {
    "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "datasetId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
    "fileName": "Bao-cao-2025.pdf"
  },
  {
    "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa8",
    "datasetId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
    "fileName": "Bao-cao-2024.pdf"
  }
]
```

### Response Fields (array)

| Field | Type | Description |
|-------|------|-------------|
| documentId | guid | ID document |
| datasetId | guid | ID dataset |
| fileName | string | Tên file gốc |

### Response (403/404)

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
| 403 | Forbidden — Không có quyền truy cập dataset (hoặc `datasetIds` chứa dataset không được phép) |
| 404 | Not Found — Document/dataset không tồn tại |
| 500 | Internal Server Error — Lỗi hệ thống (Qdrant timeout, embedding lỗi, ...) |

---

## Security Notes

### Dataset Isolation
- User chỉ thấy documents trong datasets mà họ có quyền truy cập (owner, manager OU, được share, hoặc dataset public trong OU)
- **Admin**: Admin thấy tất cả datasets → tất cả documents
- **No silent intersection**: Nếu request gửi `datasetIds` chứa ID ngoài quyền, API trả về **403** rõ ràng — không im lặng bỏ qua
- **Shard routing**: Vector search dùng shard key là dataset IDs — chỉ query shard chứa dataset user có quyền

### Authentication Flow

Search API hỗ trợ **2 cơ chế auth**, tuỳ theo nguồn request:

| Nguồn | Cơ chế | Cách hoạt động |
|-------|--------|----------------|
| **Browser** (user trực tiếp) | Cookie `MarkdownGenQAs.Auth` | ASP.NET Identity middleware tự động xác thực |
| **LLM Agent Service** (internal) | Header `X-User-Id` | `GatewayUserMiddleware` tạo ClaimsPrincipal từ header |

### Flow chi tiết

#### 1. Browser gọi trực tiếp
```
Browser → Nginx (:8080) → Main API (:5184)
                             └── Cookie auth → ClaimsPrincipal → SearchController
```

#### 2. Browser gọi LLM → LLM gọi Search API
```
Browser → Nginx
           ├── auth_request → /_auth/validate (validate cookie)
           │                    → 200 + Response header X-User-Id
           │
           ├── proxy → LLM Service (kèm X-User-Id header)
           │              └── LLM → Search API (X-User-Id + X-Gateway-Secret)
           │                              └── GatewayUserMiddleware tạo ClaimsPrincipal
           │                              └── SearchController đọc User.FindFirstValue(...)
           │
           └── Nếu auth 401 → trả về 401, không proxy
```

### GatewayUserMiddleware

Nằm giữa `UseAuthentication()` và `UseAuthorization()` trong pipeline:

```csharp
if (User.Identity?.IsAuthenticated != true && Request.Headers["X-User-Id"] hợp lệ)
{
    // Dev: trust mọi nguồn
    // Prod: chỉ trust nếu X-Gateway-Secret match config
    context.User = new ClaimsPrincipal(/* ClaimTypes.NameIdentifier = userId */);
}
```

- **Dev** (`ASPNETCORE_ENVIRONMENT=Development`): trust `X-User-Id` từ mọi nguồn
- **Production**: require `X-Gateway-Secret` header match `Internal:GatewaySecret` config
- Khi đã có cookie auth thành công (User.IsAuthenticated = true), middleware **bỏ qua** `X-User-Id`

### Nginx auth_request Endpoint

`GET /api/auth/validate` (`[AllowAnonymous]`)
- Có cookie hợp lệ → `200` + `Response Header: X-User-Id: <guid>`
- Không có cookie → `401`

### Internal Config

```json
{
  "Internal": {
    "GatewaySecret": "your-secret-key"   // Prod: shared secret giữa Nginx và services
  }
}
```
